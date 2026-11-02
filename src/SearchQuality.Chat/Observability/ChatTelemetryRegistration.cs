using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SearchQuality.Chat.Observability;

public static class ChatTelemetryRegistration
{
    public static ChatTelemetry AddChatTelemetry(this IServiceCollection services, ChatTelemetryOptions? configured = null)
    {
        var options = configured ?? ChatTelemetryOptions.FromEnvironment();
        options.Validate();
        var telemetry = new ChatTelemetry(options);
        telemetry.RegisterSecret(options.ApiKey);
        services.AddSingleton(telemetry);
        if (!options.Enabled) return telemetry;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("chat-studio")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["openinference.project.name"] = options.Project,
                    ["deployment.environment.name"] = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"
                }))
            .WithTracing(tracing => tracing.AddSource(ChatTelemetry.SourceName)
                .SetSampler(new AlwaysOnSampler())
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = options.Endpoint!;
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                    exporter.Headers = "Authorization=Bearer " + options.ApiKey;
                    exporter.TimeoutMilliseconds = 3000;
                    exporter.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;
                    exporter.BatchExportProcessorOptions = new()
                    {
                        MaxQueueSize = 512,
                        MaxExportBatchSize = 32,
                        ScheduledDelayMilliseconds = 1000,
                        ExporterTimeoutMilliseconds = 3000
                    };
                    exporter.HttpClientFactory = () => new HttpClient(new SocketsHttpHandler
                    {
                        AllowAutoRedirect = false,
                        ConnectTimeout = TimeSpan.FromSeconds(2)
                    });
                }));
        return telemetry;
    }
}
