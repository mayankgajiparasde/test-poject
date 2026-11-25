using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HotelUnderwriting.Api;

/// <summary><c>dev-token --sub alice --roles analyst --deals d1,d2 [--hours 8]</c>: prints one HS256 token for local development.</summary>
public static class DevToken
{
    public const int DefaultHours = 8, MaxHours = 24;

    /// <summary>0 and the token on <paramref name="output"/>, or 1, a message on <paramref name="error"/> and no token.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            // the same configuration sources and environment as the host
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ApplicationName = typeof(ApiHost).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
            });
            if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("dev-token is only available in the Development environment (set --environment Development).");
            var settings = JwtSettings.Read(builder.Configuration, development: true);
            if (settings.DevKey is not { } key) throw new InvalidOperationException("dev-token needs Auth:DevSigningKey.");

            var own = new ConfigurationBuilder().AddCommandLine(args).Build(); // command line only: no stray SUB or ROLES environment variable
            string[] List(string name) => (own[name] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var sub = own["sub"];
            if (sub is null || !Auth.IsSubject(sub)) throw new InvalidOperationException($"--sub is required: at most {Auth.MaxSubjectLength} characters, no control characters, not worker or anonymous.");
            var roles = List("roles");
            if (roles.Length == 0 || roles.Any(r => r is not (Auth.Analyst or Auth.Reviewer or Auth.Admin))) throw new InvalidOperationException("--roles is required: analyst, reviewer and/or admin.");
            var deals = List("deals");
            // a leading '-' is most likely the next option swallowed as the value
            if (!deals.All(d => Auth.IsDealId(d) && !d.StartsWith('-'))) throw new InvalidOperationException("--deals must be deal ids of 1 to 64 characters of A-Z a-z 0-9 . _ -, not starting with '-'.");
            var hours = DefaultHours;
            if (own["hours"] is { } h && !(int.TryParse(h, out hours) && hours is >= 1 and <= MaxHours)) throw new InvalidOperationException($"--hours must be a whole number from 1 to {MaxHours}.");

            var now = DateTime.UtcNow;
            output.WriteLine(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = settings.Issuer,
                Audience = settings.Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddHours(hours),
                Claims = new Dictionary<string, object> { ["sub"] = sub, ["roles"] = roles, ["deals"] = deals },
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
            }));
            return 0;
        }
        catch (InvalidOperationException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        catch (FormatException) // the command line could not be parsed; the message could repeat a value
        {
            error.WriteLine("The arguments could not be read: use --name value.");
            return 1;
        }
    }
}
