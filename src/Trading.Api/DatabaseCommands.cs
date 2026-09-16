using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trading.Infrastructure.Persistence;

namespace Trading.Api;

internal static class DatabaseCommands
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "database-update", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output, TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<TradingDbContext>().Database;
            var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            await database.MigrateAsync(cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "database-current",
                appliedMigrations = pending
            }));
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            await error.WriteLineAsync("Database update failed. Check the configured server, credentials, and SQL Server service.");
            return 1;
        }
    }
}
