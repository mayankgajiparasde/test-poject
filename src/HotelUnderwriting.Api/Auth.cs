using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HotelUnderwriting.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;

namespace HotelUnderwriting.Api;

/// <summary>The validated <c>Auth:*</c> settings of mode jwt. Errors name the setting and never repeat a value.</summary>
public sealed class JwtSettings
{
    public const int MinKeyBytes = 32;

    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    /// <summary>Set exactly when <see cref="DevKey"/> is null.</summary>
    public Uri? Authority { get; init; }
    public byte[]? DevKey { get; init; }

    public static JwtSettings Read(IConfiguration config, bool development)
    {
        string? Get(string name) => config[$"Auth:{name}"] is { } v && !string.IsNullOrWhiteSpace(v) ? v : null;
        var issuer = Get("Issuer") ?? throw new InvalidOperationException("Auth:Issuer is required.");
        var audience = Get("Audience") ?? throw new InvalidOperationException("Auth:Audience is required.");
        var authority = Get("Authority");
        var key = Get("DevSigningKey");
        if (authority is null == key is null) throw new InvalidOperationException("Exactly one of Auth:Authority and Auth:DevSigningKey is required.");
        if (key is not null)
        {
            if (!development) throw new InvalidOperationException("Auth:DevSigningKey is only allowed in the Development environment.");
            var bytes = Encoding.UTF8.GetBytes(key);
            if (bytes.Length < MinKeyBytes) throw new InvalidOperationException($"Auth:DevSigningKey must be at least {MinKeyBytes} bytes.");
            return new JwtSettings { Issuer = issuer, Audience = audience, DevKey = bytes };
        }
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new InvalidOperationException("Auth:Authority must be an absolute https URL (http only for loopback).");
        return new JwtSettings { Issuer = issuer, Audience = audience, Authority = uri };
    }
}

/// <summary>The authenticated caller of mode jwt.</summary>
public sealed record Caller(string Subject, bool Admin, IReadOnlySet<string> Deals)
{
    public static Caller From(ClaimsPrincipal user) => new(
        user.Claims.First(c => c.Type == "sub").Value, // exactly one: checked when the token was validated
        user.IsInRole(Auth.Admin),
        user.Claims.Where(c => c.Type == "deals" && Auth.IsDealId(c.Value)).Select(c => c.Value).ToHashSet(StringComparer.Ordinal));

    public bool MaySee(IReadOnlyCollection<string> resourceDeals) => Admin || resourceDeals.Any(Deals.Contains);
}

/// <summary>Authentication and authorization (AD-16): <c>Auth:Mode</c> none (loopback only) or jwt (bearer tokens, roles, per-deal access).</summary>
public static partial class Auth
{
    public const string Analyst = "analyst", Reviewer = "reviewer", Admin = "admin";
    public const string DealHeader = "X-Deal-Id";
    public const int MaxSubjectLength = 128;
    private const string NotLoopback = "Auth:Mode 'none' only allows loopback listen addresses (localhost, 127.0.0.0/8, ::1). Set Auth:Mode to jwt to listen elsewhere.";

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex DealId();

    public static bool IsDealId(string value) => DealId().IsMatch(value);

    /// <summary>A usable subject: it becomes the ledger actor, so it may not pose as one of the service's own.</summary>
    public static bool IsSubject(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxSubjectLength && value is not ("worker" or "anonymous") && !value.Any(char.IsControl);

    /// <summary>The stored form of a caller's idempotency key for one target deal ("" for none): fixed length, and with a ':' that no client key may contain.
    /// Unambiguous because none of the three parts can contain NUL.</summary>
    public static string ScopedKey(string subject, string deal, string key) =>
        "jwt:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{subject}\0{deal}\0{key}")));

    /// <summary>Configures the chosen mode. True for jwt.</summary>
    internal static bool Add(WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Auth:Mode"] ?? "none";
        if (mode == "none")
        {
            // Runs for every endpoint, however its address was configured, before it is bound: an exposed one is never opened.
            builder.WebHost.ConfigureKestrel(o => o.ConfigureEndpointDefaults(listen =>
            {
                if (listen.IPEndPoint is not { } ip || !IPAddress.IsLoopback(ip.Address)) throw new InvalidOperationException(NotLoopback);
            }));
            // A web page can point its own host name at 127.0.0.1 (DNS rebinding); only loopback Host headers are served.
            // PostConfigure, so the AllowedHosts setting cannot widen it.
            builder.Services.PostConfigure<HostFilteringOptions>(o =>
            {
                o.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];
                o.AllowEmptyHosts = false;
            });
            return false;
        }
        if (mode != "jwt") throw new InvalidOperationException("Auth:Mode must be none or jwt.");

        var settings = JwtSettings.Read(builder.Configuration, builder.Environment.IsDevelopment());
        // The handler's own messages quote claim values (such as token lifetimes); only the failure type is logged, below.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.JwtBearer", LogLevel.None);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.IncludeErrorDetails = false; // WWW-Authenticate: Bearer, with no reason
            o.SaveToken = false;
            o.Authority = settings.Authority?.AbsoluteUri;
            o.MetadataAddress = null!;
            o.RequireHttpsMetadata = settings.Authority?.Scheme != Uri.UriSchemeHttp;
            o.BackchannelTimeout = TimeSpan.FromSeconds(10);
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = settings.Issuer,
                ValidAudience = settings.Audience,
                IssuerSigningKey = settings.DevKey is { } key ? new SymmetricSecurityKey(key) : null,
                ValidAlgorithms = settings.DevKey is not null
                    ? [SecurityAlgorithms.HmacSha256]
                    : [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSsaPssSha256],
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(60),
                RoleClaimType = "roles",
                NameClaimType = "sub",
            };
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    var subjects = ctx.Principal!.Claims.Where(c => c.Type == "sub").ToList();
                    // With an authority the library would also accept the discovery document's issuer; only Auth:Issuer is allowed.
                    if (ctx.SecurityToken.Issuer != settings.Issuer || subjects.Count != 1 || !IsSubject(subjects[0].Value))
                        ctx.Fail("The token was rejected.");
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = ctx =>
                {
                    // A bad token is routine. A missing signing key, or anything that is not about the token (discovery unreachable), needs an operator.
                    var e = ctx.Exception;
                    var level = e is SecurityTokenSignatureKeyNotFoundException || e is not (SecurityTokenException or ArgumentException) ? LogLevel.Warning : LogLevel.Information;
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HotelUnderwriting.Auth")
                        .Log(level, "Bearer token rejected: {Reason}", e.GetType().Name);
                    return Task.CompletedTask;
                },
            };
        });
        // One fallback policy covers every endpoint not marked anonymous, and unknown paths too: a new route is protected by default.
        builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(c => c.User.IsInRole(Analyst) || c.User.IsInRole(Admin)
                || (c.User.IsInRole(Reviewer) && c.Resource is HttpContext http && HttpMethods.IsGet(http.Request.Method)))
            .Build());
        return true;
    }

    /// <summary>Mode none relies on the Kestrel endpoint check, so another server (such as IIS in-process) is refused.</summary>
    internal static void RequireKestrel(WebApplication app)
    {
        // by assembly: the registered implementation type is internal to Kestrel
        if (app.Services.GetRequiredService<IServer>().GetType().Assembly != typeof(KestrelServer).Assembly) throw new InvalidOperationException(NotLoopback);
    }
}
