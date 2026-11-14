using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenBook.Web.Diagnostics;

/// <summary>
/// OpenTelemetry for whoever runs an OpenBook Server: traces of page and API requests and of calls OpenBook makes, and
/// metrics (requests, outgoing calls, the .NET runtime, and OpenBook's own: background job runs and their time, alerts).
/// Off unless an OTLP endpoint is given (<c>OpenBook:Telemetry:OtlpEndpoint</c>, or the standard
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>): nothing is collected or sent otherwise. Paths go out with record ids, tokens and
/// other long values replaced by <c>{id}</c>, and query strings with their values redacted, so links that grant access
/// (a customer's portal, an invitation) don't reach the collector.
/// </summary>
public static partial class Telemetry
{
    public const string Name = "OpenBook";

    private static readonly Meter Meter = new(Name);
    private static readonly ActivitySource Source = new(Name);
    private static readonly Counter<long> JobRuns = Meter.CreateCounter<long>("openbook.job.runs", description: "Background job runs, by job and outcome.");
    private static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>("openbook.job.duration", "s", "How long background job runs took.");
    private static readonly Counter<long> Alerts = Meter.CreateCounter<long>("openbook.alerts", description: "Admin alerts, by kind and whether they were sent.");

    /// <summary>The OTLP endpoint, or null when telemetry is off.</summary>
    public static string? Endpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration["OpenBook:Telemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
    }

    public static IServiceCollection AddOpenBookTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Endpoint(configuration) is not { } endpoint)
            return services;
        var uri = new Uri(endpoint);
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(Name, serviceVersion: Hosting.UpdateService.CurrentVersion?.ToString()))
            .WithTracing(t => t
                .AddSource(Name)
                .AddAspNetCoreInstrumentation(o =>
                {
                    // The interactive pages' connection and static files would drown out the pages themselves.
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/_blazor") && !http.Request.Path.StartsWithSegments("/_framework")
                        && !Path.HasExtension(http.Request.Path.Value);
                    o.EnrichWithHttpRequest = (activity, request) =>
                    {
                        var path = Redact(request.Path.Value);
                        activity.SetTag("url.path", path);
                        activity.DisplayName = $"{request.Method} {activity.GetTagItem("http.route") as string ?? path}";
                    };
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = uri))
            .WithMetrics(m => m
                .AddMeter(Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = uri));
        return services;
    }

    /// <summary>A path with each segment that could identify a record or grant access (ids, tokens, long values) as <c>{id}</c>.</summary>
    public static string Redact(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";
        return string.Join('/', path.Split('/').Select(segment => Identifying().IsMatch(segment) ? "{id}" : segment));
    }

    // A GUID, anything with a digit in it, or anything long: page names are short words with dashes.
    [GeneratedRegex(@"^(?=.*\d).+$|^.{25,}$")]
    private static partial Regex Identifying();

    internal static void JobRan(string job, DateTimeOffset startedAt, TimeSpan took, string? error)
    {
        var tags = new TagList { { "job", job }, { "outcome", error is null ? "ok" : "failed" } };
        JobRuns.Add(1, tags);
        JobDuration.Record(took.TotalSeconds, tags);

        // A span for the run, timed as it ran, when something listens.
        using var activity = Source.StartActivity($"job {job}", ActivityKind.Internal, default(ActivityContext), startTime: startedAt);
        if (activity is null)
            return;
        activity.SetTag("job", job);
        if (error is not null)
            activity.SetStatus(ActivityStatusCode.Error, error);
        activity.SetEndTime((startedAt + took).UtcDateTime);
    }

    internal static void AlertRaised(string key, bool sent) =>
        Alerts.Add(1, new TagList { { "kind", key.Split(':')[0] }, { "sent", sent } });
}
