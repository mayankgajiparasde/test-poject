using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace HotelUnderwriting.Api;

public static partial class ApiHost
{
    private const int MaxBodyBytes = 1_048_576;

    /// <summary>Wire format: camelCase, DebtEvent as a "$type" object, "NaN"/"Infinity" strings parse so the engine (not the parser) rejects them with 422.</summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        o.Converters.Add(new DebtEventConverter());
        return o;
    }

    private const string CorrelationHeader = "X-Correlation-Id";
    private const string Actor = "anonymous"; // Auth:Mode none

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex SafeCorrelationId();

    private static string CorrelationId(HttpContext ctx) => (string)ctx.Items[CorrelationHeader]!;

    private sealed record RunRequest(string AssumptionSetId, string Scenario);

    /// <param name="services">Test seam: runs after the app's own registrations, so it can replace a store.</param>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(ApiHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // HTTP_PORTS / HTTPS_PORTS only take effect when no urls are set, so the default must not hide them.
        if (new[] { "urls", "HTTP_PORTS", "HTTPS_PORTS" }.All(k => string.IsNullOrWhiteSpace(builder.Configuration[k]))) builder.WebHost.UseUrls("http://127.0.0.1:5080");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxBodyBytes);
        var jwt = Auth.Add(builder);
        // The request-start and -finish lines quote the client's raw path and query, so a %0A in them would start a forged line in the console log. Warnings and errors of the category stay.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        builder.Services.AddSingleton<IClock, SystemClock>();
        var pg = builder.Configuration["Persistence:ConnectionString"];
        var schema = "public";
        if (!string.IsNullOrWhiteSpace(pg)) (pg, schema) = PostgresConnection.Parse(pg);
        if (string.IsNullOrWhiteSpace(pg))
        {
            builder.Services.AddSingleton<ILedger, InMemoryLedger>();
            builder.Services.AddSingleton<IDealAccess, InMemoryDealAccess>();
            builder.Services.AddSingleton<IAssumptionSetStore>(sp => new InMemoryAssumptionSetStore(sp.GetRequiredService<ILedger>()));
            builder.Services.AddSingleton<IRunStore>(sp => new InMemoryRunStore(sp.GetRequiredService<ILedger>()));
            builder.Services.AddSingleton<IJobStore>(sp => new InMemoryJobStore(sp.GetRequiredService<ILedger>()));
        }
        else
        {
            builder.Services.AddSingleton<IAssumptionSetStore>(_ => new PostgresAssumptionSetStore(pg, schema));
            builder.Services.AddSingleton<IRunStore>(_ => new PostgresRunStore(pg, schema));
            builder.Services.AddSingleton<IJobStore>(_ => new PostgresJobStore(pg, schema));
            builder.Services.AddSingleton<ILedger>(_ => new PostgresLedger(pg, schema));
            builder.Services.AddSingleton<IDealAccess>(_ => new PostgresDealAccess(pg, schema));
        }
        builder.Services.AddSingleton(sp => new PublishAssumptionSet(sp.GetRequiredService<IAssumptionSetStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILedger>()));
        builder.Services.AddSingleton(sp => new SubmitRun(sp.GetRequiredService<IRunStore>(), sp.GetRequiredService<IClock>(), ledger: sp.GetRequiredService<ILedger>()));
        builder.Services.AddSingleton(sp => new SubmitSweep(sp.GetRequiredService<IJobStore>(), sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<SweepPlanCache>();
        builder.Services.AddSingleton<GoldenSelfTest>();
        builder.Services.AddSingleton<IWorkLog>(sp => new LoggerWorkLog(sp.GetRequiredService<ILoggerFactory>().CreateLogger("HotelUnderwriting.Worker")));
        builder.Services.AddSingleton(sp => new QueueGauges(sp.GetRequiredService<IJobStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IWorkLog>()));
        builder.Services.AddSingleton(sp => new SweepWorker(sp.GetRequiredService<IJobStore>(), sp.GetRequiredService<IAssumptionSetStore>(), sp.GetRequiredService<SubmitRun>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<SweepPlanCache>(), sp.GetRequiredService<IWorkLog>(), sp.GetRequiredService<IDealAccess>()));

        // Export is opt-in: with no endpoint nothing is registered, so a plain run has no export overhead and makes no outbound calls.
        var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            // The exporter reads the same key itself; setting Endpoint here would stop it appending /v1/traces and /v1/metrics for http/protobuf.
            if (!Uri.TryCreate(otlp, UriKind.Absolute, out var otlpUri) || otlpUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute http or https URL.");
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(Telemetry.Name, serviceVersion: EngineInfo.Version))
                .WithTracing(t => t.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation().AddOtlpExporter())
                .WithMetrics(m => m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation().AddOtlpExporter());
        }
        var role = builder.Configuration["Role"] ?? "all";
        if (role is not ("api" or "worker" or "all")) throw new InvalidOperationException("Role must be api, worker or all.");
        var workerCount = int.TryParse(builder.Configuration["Worker:Count"] ?? "2", out var wc) && wc is >= 1 and <= 16 ? wc : throw new InvalidOperationException("Worker:Count must be a whole number from 1 to 16.");
        if (role != "api") builder.Services.AddHostedService(sp => new SweepWorkerService(sp.GetRequiredService<SweepWorker>(), workerCount, sp.GetRequiredService<ILogger<SweepWorkerService>>()));

        builder.Services.AddSingleton(UploadLimits.Read(builder.Configuration));
        builder.Services.AddSingleton<WorkbookParser>();

        services?.Invoke(builder.Services);

        var app = builder.Build();
        if (!jwt) Auth.RequireKestrel(app);
        _ = app.Services.GetRequiredService<QueueGauges>(); // created now so the gauges exist for the whole life of the host
        var migrated = false;
        var migrateGate = new object();
        // An unreachable database must not stop the process: the startup and ready probes retry the migration and report 503 until it works.
        bool TryMigrate()
        {
            lock (migrateGate)
            {
                if (migrated) return true;
                try { PostgresMigrator.Apply(pg!, schema); migrated = true; }
                catch (Exception e) { app.Logger.LogError(e, "Database migration failed."); }
                return migrated;
            }
        }
        if (string.IsNullOrWhiteSpace(pg)) app.Logger.LogWarning("Persistence:ConnectionString not set: using in-memory stores, data is lost on restart");
        else TryMigrate();
        if (jwt)
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                if (app.Urls.Any(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !(Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.IsLoopback)))
                    app.Logger.LogWarning("Listening on plain http beyond loopback: bearer tokens need TLS in front of this service.");
            });
        var openApi = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "openapi.v1.json"));
        var access = app.Services.GetRequiredService<IDealAccess>();
        // Null in Auth:Mode none: everything is visible and nothing is granted.
        Caller? Who(HttpContext ctx) => jwt ? Caller.From(ctx.User) : null;
        // A resource the caller may not see must be answered exactly like a missing one.
        bool Sees(Caller? caller, DealResource kind, string id) => caller is null || caller.Admin || caller.MaySee(access.DealsOf(kind, id));
        // The one deal a new run or job of a visible set goes to: X-Deal-Id, or the only candidate. Empty in mode none and for an admin on a set without deals.
        (string[] Deals, IResult? Refused) Target(HttpContext ctx, Caller? caller, string setId)
        {
            if (caller is null) return ([], null);
            var named = ctx.Request.Headers.TryGetValue(Auth.DealHeader, out var header);
            var setDeals = access.DealsOf(DealResource.Set, setId);
            var candidates = setDeals.Where(caller.Deals.Contains).ToArray();
            if (caller.Admin && (named || candidates.Length == 0)) candidates = setDeals.ToArray();
            if (named)
            {
                var deal = header.ToString();
                if (!Auth.IsDealId(deal)) return ([], Problem(400, $"Header {Auth.DealHeader} must be 1 to 64 characters of A-Z a-z 0-9 . _ -."));
                return candidates.Contains(deal, StringComparer.Ordinal) ? ([deal], null) : ([], Problem(403, "The token does not permit this deal for this assumption set."));
            }
            return candidates.Length <= 1 ? (candidates, null) : ([], Problem(400, $"The assumption set belongs to several deals: name one in header {Auth.DealHeader}."));
        }

        app.Use(async (ctx, next) =>
        {
            var supplied = ctx.Request.Headers[CorrelationHeader].ToString();
            var correlationId = SafeCorrelationId().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString();
            using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["correlation_id"] = correlationId });
            ctx.Items[CorrelationHeader] = correlationId;
            ctx.Response.Headers[CorrelationHeader] = correlationId;
            IResult? problem = null;
            try { await next(ctx); }
            catch (BadHttpRequestException e) { problem = Problem(e.StatusCode, e.StatusCode == 413 ? "Request body exceeds 1 MB." : "The request could not be read."); }
            catch (JsonException e) { problem = Problem(400, "The request body is malformed or does not match the schema.", e.Path is { } p ? new Dictionary<string, string[]> { [p] = ["invalid or missing value"] } : null); }
            catch (IdempotencyConflictException e) { problem = Problem(409, e.Message); }
            catch (InvalidAssumptionsException e) { problem = Problem(422, Regex.Replace(e.Message, @"\s*\(Parameter '[^']*'\)", "")); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return; } // client went away: nothing to write or log
            catch (Exception e) when (IsDatabaseFailure(e))
            {
                app.Logger.LogError("Database unavailable ({ExceptionType}).", e.GetType().Name); // no detail: it may quote the connection
                ctx.Response.Headers.RetryAfter = "5";
                problem = Problem(503, "The database is unavailable. Try again shortly.");
            }
            catch (Exception e) { app.Logger.LogError(e, "Unhandled exception"); problem = Problem(500, "An unexpected error occurred."); }

            // Routing's own 404/405 carry no body; give them one.
            // So do the 401 and 403 of authentication; they say nothing about why.
            if (problem is null && ctx.Response.StatusCode >= 400 && ctx.Response.ContentLength is null && ctx.Response.ContentType is null)
                problem = Problem(ctx.Response.StatusCode, ctx.Response.StatusCode switch
                {
                    401 => "A valid bearer token is required.",
                    403 => "The token does not permit this request.",
                    404 => "No such resource.",
                    _ => "Method not allowed for this resource.",
                });
            if (problem is not null && !ctx.Response.HasStarted) await problem.ExecuteAsync(ctx);
        });

        if (jwt)
        {
            app.UseAuthentication(); // after the middleware above, so 401 and 403 get a problem body and a correlation id
            app.UseAuthorization();
        }

        app.MapGet("/health/live", () => Results.Json(new { status = "live" })).AllowAnonymous();
        // Startup retries too: an orchestrator holds back readiness probes until startup succeeds.
        app.MapGet("/health/startup", () => string.IsNullOrWhiteSpace(pg) || TryMigrate()
            ? Results.Json(new { status = "started" })
            : Problem(503, "The service has not started."))
            .AllowAnonymous();
        app.MapGet("/health/ready", (IAssumptionSetStore sets, IRunStore runs, GoldenSelfTest golden) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(pg))
                {
                    if (!TryMigrate()) return Problem(503, "The service is not ready.");
                    PostgresMigrator.Probe(pg, schema);
                }
                sets.TryGet("", out _);
                runs.TryGet("", out _);
            }
            catch (Exception e)
            {
                app.Logger.LogError(e, "Readiness check failed.");
                return Problem(503, "The service is not ready.");
            }
            if (!golden.Check())
            {
                app.Logger.LogError("Readiness self-test failed: the engine no longer reproduces the golden scenario.");
                return Problem(503, "The service is not ready.");
            }
            return Results.Json(new { status = "ready" });
        }).AllowAnonymous();
        app.MapGet("/openapi/v1.json", () => Results.Content(openApi, "application/json")).AllowAnonymous();

        // Both publish routes: the deal checks come before the body is read, the grant after the write.
        (Caller? Caller, string Deal, IResult? Refused) PublishTarget(HttpContext ctx)
        {
            var caller = Who(ctx);
            var deal = ctx.Request.Headers[Auth.DealHeader].ToString();
            if (caller is not null)
            {
                if (!Auth.IsDealId(deal)) return (caller, deal, Problem(400, $"Header {Auth.DealHeader} is required: 1 to 64 characters of A-Z a-z 0-9 . _ -."));
                if (!caller.Admin && !caller.Deals.Contains(deal)) return (caller, deal, Problem(403, "The token does not permit this deal."));
            }
            return (caller, deal, null);
        }
        IResult Published(HttpContext ctx, PublishAssumptionSet publish, Assumptions a, Caller? caller, string deal)
        {
            var (record, created) = publish.Publish(a, caller?.Subject ?? Actor, CorrelationId(ctx));
            if (caller is not null) access.Grant(DealResource.Set, record.Id, [deal]); // after the write; a repeat repairs a failed grant
            if (created) ctx.Response.Headers.Location = $"/v1/assumption-sets/{record.Id}";
            return Results.Json(SetBody(record), Json, statusCode: created ? 201 : 200);
        }

        app.MapPost("/v1/assumption-sets", async (HttpContext ctx, PublishAssumptionSet publish) =>
        {
            if (!IsJson(ctx.Request)) return Problem(415, "Content-Type must be application/json.");
            var (caller, deal, refused) = PublishTarget(ctx);
            if (refused is not null) return refused;
            var a = await ReadJson<Assumptions>(ctx);
            if (a is null) return Problem(400, "The request body must be an Assumptions object.");
            return Published(ctx, publish, a, caller, deal);
        });

        app.MapPost("/v1/assumption-sets/excel", async (HttpContext ctx, PublishAssumptionSet publish, WorkbookParser parser, UploadLimits limits, IHostApplicationLifetime life) =>
        {
            if (!IsXlsx(ctx.Request)) return Problem(415, $"Content-Type must be {XlsxContentType}.");
            var (caller, deal, refused) = PublishTarget(ctx);
            if (refused is not null) return refused;
            return await Upload(ctx, parser, limits, life, a => Published(ctx, publish, a, caller, deal));
        });

        app.MapGet("/v1/assumption-sets/{id}", (string id, HttpContext ctx, IAssumptionSetStore sets) =>
            sets.TryGet(id, out var r) && Sees(Who(ctx), DealResource.Set, id) ? Results.Json(SetBody(r), Json) : Problem(404, NotFound("Assumption set", id)));

        app.MapPost("/v1/runs", async (HttpContext ctx, IAssumptionSetStore sets, SubmitRun submit) =>
        {
            if (!IsJson(ctx.Request)) return Problem(415, "Content-Type must be application/json.");
            var req = await ReadJson<RunRequest>(ctx);
            if (req is null) return Problem(400, "The request body must be {assumptionSetId, scenario}.");
            var caller = Who(ctx);
            if (!sets.TryGet(req.AssumptionSetId, out var set) || !Sees(caller, DealResource.Set, set.Id)) return Problem(404, NotFound("Assumption set", req.AssumptionSetId));
            var (deals, refused) = Target(ctx, caller, set.Id);
            if (refused is not null) return refused;
            var scenario = set.Assumptions.Scenarios.FirstOrDefault(s => s.Name == req.Scenario);
            if (scenario is null) return Problem(404, NotFound("Scenario", req.Scenario, "in the assumption set"));
            var r = submit.Submit(set.Assumptions, scenario, actor: caller?.Subject ?? Actor, correlationId: CorrelationId(ctx));
            if (deals.Length > 0) access.Grant(DealResource.Run, r.Record.RunKey, deals);
            return Results.Json(RunBody(r.Record, r.WasCached, false, false), Json);
        });

        app.MapGet("/v1/runs/{runKey}", (string runKey, HttpContext ctx, IRunStore runs) =>
        {
            var include = ctx.Request.Query["include"].SelectMany(v => (v ?? "").ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
            var unknown = include.Where(i => i is not ("monthly" or "proforma")).ToList();
            if (unknown.Count > 0) return Problem(400, "Unknown include value. Allowed: monthly, proforma.");
            if (!runs.TryGet(runKey, out var r) || !Sees(Who(ctx), DealResource.Run, runKey)) return Problem(404, NotFound("Run", runKey));
            return Results.Json(RunBody(r, true, include.Contains("monthly"), include.Contains("proforma")), Json);
        });

        app.MapPost("/v1/sweeps", async (HttpContext ctx, IAssumptionSetStore sets, SubmitSweep submit, IJobStore jobs) =>
        {
            if (!IsJson(ctx.Request)) return Problem(415, "Content-Type must be application/json.");
            string? key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (key is not null && !SweepLimits.IsValidIdempotencyKey(key)) return Problem(400, $"Idempotency-Key must be 1 to {SweepLimits.MaxIdempotencyKeyLength} characters of A-Z a-z 0-9 . _ -.");
            var req = await ReadJson<SweepRequest>(ctx);
            if (req is null || req.AssumptionSetId is null || req.Scenario is null) return Problem(400, "The request body must be {assumptionSetId, scenario, cases or grid}.");
            var caller = Who(ctx);
            if (!sets.TryGet(req.AssumptionSetId, out var set) || !Sees(caller, DealResource.Set, set.Id)) return Problem(404, NotFound("Assumption set", req.AssumptionSetId));
            var (deals, refused) = Target(ctx, caller, set.Id);
            if (refused is not null) return refused;
            if (set.Assumptions.Scenarios.All(s => s.Name != req.Scenario)) return Problem(404, NotFound("Scenario", req.Scenario, "in the assumption set"));
            if (key is not null && caller is not null) key = Auth.ScopedKey(caller.Subject, deals.FirstOrDefault() ?? "", key);
            var (job, _) = submit.Submit(req, key, caller?.Subject ?? Actor, CorrelationId(ctx), dealScoped: deals.Length > 0);
            // A repeat only repairs a missing grant: it never widens who sees an existing job.
            if (deals.Length > 0 && SubmitSweep.IsDealScoped(job.Id) && access.DealsOf(DealResource.Job, job.Id).Count == 0) access.Grant(DealResource.Job, job.Id, deals);
            ctx.Response.Headers.Location = $"/v1/jobs/{job.Id}";
            return Results.Json(new { jobId = job.Id, status = jobs.GetProgress(job.Id)!.Status, caseCount = job.CaseCount, chunkCount = job.ChunkCount }, Json, statusCode: 202);
        });

        app.MapGet("/v1/jobs/{id}", (string id, HttpContext ctx, IJobStore jobs) =>
            jobs.TryGetJob(id) is { } job && Sees(Who(ctx), DealResource.Job, id) && jobs.GetProgress(id) is { } p
                ? Results.Json(new
                {
                    jobId = job.Id, status = p.Status, createdUtc = job.CreatedUtc, caseCount = job.CaseCount,
                    completedCases = p.CompletedCases, failedCases = p.FailedCases,
                    chunks = new { total = p.Total, done = p.Done, queued = p.Queued, leased = p.Leased, dead = p.Dead },
                }, Json)
                : Problem(404, NotFound("Job", id)));

        app.MapGet("/v1/jobs/{id}/results", (string id, HttpContext ctx, IJobStore jobs, IRunStore runs, SweepPlanCache plans) =>
        {
            var limit = 100;
            var after = -1;
            if (ctx.Request.Query.TryGetValue("limit", out var l) && !(int.TryParse(l, out limit) && limit is >= 1 and <= 1000)) return Problem(400, "limit must be a whole number from 1 to 1000.");
            if (ctx.Request.Query.TryGetValue("after", out var a) && !(int.TryParse(a, out after) && after >= 0)) return Problem(400, "after must be a whole number of 0 or more.");
            if (jobs.TryGetJob(id) is not { } job || !Sees(Who(ctx), DealResource.Job, id)) return Problem(404, NotFound("Job", id));
            var page = jobs.GetResults(id, after, limit + 1);
            var plan = plans.Get(job);
            var items = page.Take(limit).Select(r =>
            {
                ReturnsSummary? sm = r.RunKey is not null && runs.TryGet(r.RunKey, out var run) ? run.Result.Summary : null;
                return new
                {
                    caseIndex = r.CaseIndex, name = plan.CaseAt(r.CaseIndex).Name, runKey = r.RunKey, error = r.Error,
                    summary = sm is null ? null : new { sm.LeveredIrr, sm.UnleveredIrr, sm.LeveredMultiple, sm.LpIrr, sm.GpIrr, sm.EquityAtClose, sm.GrossSalePrice },
                };
            }).ToList();
            return Results.Json(new { items, next = page.Count > limit ? items[^1].caseIndex : (int?)null }, Json);
        });

        return app;
    }

    private static object SetBody(AssumptionSetRecord r) =>
        new { id = r.Id, createdUtc = r.CreatedUtc, scenarios = r.Assumptions.Scenarios.Select(s => s.Name).ToArray() };

    private static Dictionary<string, object?> RunBody(RunRecord r, bool wasCached, bool monthly, bool proforma)
    {
        var body = new Dictionary<string, object?>
        {
            ["runKey"] = r.RunKey,
            ["engineVersion"] = r.EngineVersion,
            ["createdUtc"] = r.CreatedUtc,
            ["wasCached"] = wasCached,
            ["scenario"] = r.Result.Inputs.Name,
            ["summary"] = r.Result.Summary,
        };
        if (monthly) body["monthly"] = r.Result.Monthly;
        if (proforma)
        {
            // Proforma.Lines is a tuple list, which System.Text.Json would write as empty objects.
            var node = JsonSerializer.SerializeToNode(r.Result.Proforma, Json)!.AsObject();
            node["lines"] = new JsonArray(r.Result.Proforma.Lines.Select(l => (JsonNode?)new JsonObject
            {
                ["code"] = l.Line.Code, ["name"] = l.Line.Name, ["section"] = l.Line.Section,
                ["amounts"] = JsonSerializer.SerializeToNode(l.Amounts, Json),
            }).ToArray());
            body["proforma"] = node;
        }
        return body;
    }

    internal static bool IsDatabaseFailure(Exception e)
    {
        // A server answer about the data (constraint, trigger, syntax) is a server fault, not an outage; connection, resource and shutdown classes are outages.
        // A bare TimeoutException counts only when Npgsql threw it (pool exhausted); one from anywhere else is an ordinary fault.
        for (var x = e; x is not null; x = x.InnerException)
            if (x is PostgresException pg ? pg.SqlState is { } st && (st.StartsWith("08") || st.StartsWith("53") || st.StartsWith("57"))
                : x is NpgsqlException || x is TimeoutException t && t.TargetSite?.DeclaringType?.Assembly == typeof(NpgsqlException).Assembly) return true;
        return false;
    }

    private static readonly Regex PlainId = SafeCorrelationId();

    /// <summary>A 404 text: the client's value is quoted only when it looks like an id; anything else (free text) is not echoed.</summary>
    private static string NotFound(string what, string? value, string? where = null) =>
        value is not null && PlainId.IsMatch(value) ? $"{what} '{value}' was not found{(where is null ? "" : " " + where)}." : $"{what} was not found{(where is null ? "" : " " + where)}.";

    /// <summary>The charset of a JSON request: null = none given (UTF-8), otherwise its encoding; the third value is false for a Content-Type that is not JSON or whose charset cannot be used.</summary>
    private static (bool Json, Encoding? Charset) JsonType(HttpRequest request)
    {
        try
        {
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !request.HasJsonContentType()) return (false, null);
            var charsets = type.Parameters.Where(p => p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)).ToList();
            if (charsets.Count == 0) return (true, null);
            if (charsets.Count > 1) return (false, null);
            var name = HeaderUtilities.RemoveQuotes(charsets[0].Value).Value ?? "";
            if (name.Length is 0 or > 64) return (false, null);
            var encoding = Encoding.GetEncoding(name);
            return encoding.CodePage == 65000 ? (false, null) : (true, encoding); // UTF-7 is refused
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or FormatException) { return (false, null); }
    }

    private static bool IsJson(HttpRequest request) => JsonType(request).Json;

    /// <summary>Reads the body as JSON in the charset <see cref="JsonType"/> found. Never fails on the Content-Type itself.</summary>
    private static async Task<T?> ReadJson<T>(HttpContext ctx)
    {
        var (_, charset) = JsonType(ctx.Request);
        Stream body = ctx.Request.Body;
        if (charset is not null && charset.CodePage != 65001) body = Encoding.CreateTranscodingStream(body, charset, Encoding.UTF8, leaveOpen: true);
        try { return await JsonSerializer.DeserializeAsync<T>(body, Json, ctx.RequestAborted); }
        finally { if (!ReferenceEquals(body, ctx.Request.Body)) await body.DisposeAsync(); }
    }

    private static IResult Problem(int status, string detail, Dictionary<string, string[]>? errors = null) =>
        Results.Problem(
            type: "about:blank",
            title: ReasonPhrases.GetReasonPhrase(status),
            statusCode: status,
            detail: detail,
            extensions: errors is null ? null : new Dictionary<string, object?> { ["errors"] = errors });
}
