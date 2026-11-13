using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpsMind.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddOpsMindDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString("OpsMind");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Database connection string is missing. Configure ConnectionStrings:OpsMind.");
        }

        services.AddDbContext<OpsMindDbContext>(options => options.UseNpgsql(connectionString));
        return services;
    }
}
