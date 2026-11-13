using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpsMind.Infrastructure.Observability;

public static class OpsMindTelemetry
{
    public const string ActivitySourceName = "OpsMind";

    public static IServiceCollection AddOpsMindTelemetry(
        this IServiceCollection services,
        string serviceName,
        bool includeAspNetCoreInstrumentation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ActivitySourceName)
                    .AddHttpClientInstrumentation();

                if (includeAspNetCoreInstrumentation)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                tracing.AddOtlpExporter();
            });

        return services;
    }
}
