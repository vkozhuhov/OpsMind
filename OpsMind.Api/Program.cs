using OpsMind.Infrastructure.Observability;
using OpsMind.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpsMindTelemetry(
    serviceName: "OpsMind.Api",
    includeAspNetCoreInstrumentation: true);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddOpsMindDatabase(builder.Configuration);
builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<OpsMindDbContext>("postgresql", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false
}).ExcludeFromDescription();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).ExcludeFromDescription();

app.Run();

public partial class Program;
