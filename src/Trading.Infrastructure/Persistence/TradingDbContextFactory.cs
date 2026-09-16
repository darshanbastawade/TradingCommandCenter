using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;

namespace Trading.Infrastructure.Persistence;

// Uses the API's local development settings so CLI migrations and the application target the same database.
public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__TradingDatabase");
        if (string.IsNullOrWhiteSpace(connection))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln")))
                directory = directory.Parent;
            if (directory is null)
                throw new InvalidOperationException("Set ConnectionStrings__TradingDatabase when running EF tools outside the solution checkout.");
            var settings = Path.Combine(directory.FullName, "src", "Trading.Api", "appsettings.Development.json");
            using var document = JsonDocument.Parse(File.ReadAllText(settings));
            connection = document.RootElement.GetProperty("ConnectionStrings").GetProperty("TradingDatabase").GetString();
            var localSettings = Path.Combine(directory.FullName, "src", "Trading.Api", "appsettings.Local.json");
            if (File.Exists(localSettings))
            {
                using var localDocument = JsonDocument.Parse(File.ReadAllText(localSettings));
                if (localDocument.RootElement.TryGetProperty("ConnectionStrings", out var strings) &&
                    strings.TryGetProperty("TradingDatabase", out var database) &&
                    !string.IsNullOrWhiteSpace(database.GetString()))
                    connection = database.GetString();
            }
        }
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Configure ConnectionStrings:TradingDatabase in API development settings or its environment variable.");
        return new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlServer(connection).Options);
    }
}
