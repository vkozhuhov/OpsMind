using OpsMind.Infrastructure.Observability;
using OpsMind.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOpsMindTelemetry(
    serviceName: "OpsMind.Worker");
builder.Services.AddOpsMindDatabase(builder.Configuration);

var host = builder.Build();
host.Run();
