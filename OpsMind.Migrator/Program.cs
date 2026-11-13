using Microsoft.Extensions.Hosting;
using OpsMind.Infrastructure.Observability;
using OpsMind.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOpsMindTelemetry(
    serviceName: "OpsMind.Migrator");
builder.Services.AddOpsMindDatabase(builder.Configuration);

using var host = builder.Build();

await host.StartAsync();

try
{
    await using var scope = host.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OpsMindDbContext>();

    await dbContext.Database.MigrateAsync();
    await VerifyVectorExtensionAsync(dbContext);

    Console.WriteLine("OpsMind database migrations completed successfully.");
}
finally
{
    await host.StopAsync();
}

static async Task VerifyVectorExtensionAsync(OpsMindDbContext dbContext)
{
    var connection = dbContext.Database.GetDbConnection();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector');";

    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var result = await command.ExecuteScalarAsync();
    if (result is not true)
    {
        throw new InvalidOperationException(
            "The pgvector extension is not installed. Check the PostgreSQL image and migration permissions.");
    }
}
