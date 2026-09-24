using Trading.Domain.MarketData;
using Trading.MarketData.Quality;

namespace Trading.Api;

public sealed record ExchangeCalendarSettings
{
    public const string SectionName = "ExchangeCalendar";
    public string Id { get; init; } = "nse-2025-v2";
    public string TimeZoneId { get; init; } = "India Standard Time";
    public TimeOnly DefaultSessionOpen { get; init; } = new(9, 15);
    public TimeOnly DefaultSessionClose { get; init; } = new(15, 30);
    public string FilePath { get; init; } = "data/nse-calendar-2025.csv";
}

public static class ExchangeCalendarLoader
{
    public static async Task<ExchangeSessionCalendar> LoadAsync(IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(ExchangeCalendarSettings.SectionName)
            .Get<ExchangeCalendarSettings>() ?? new();
        if (string.IsNullOrWhiteSpace(settings.Id) || string.IsNullOrWhiteSpace(settings.TimeZoneId) ||
            string.IsNullOrWhiteSpace(settings.FilePath) ||
            settings.DefaultSessionOpen >= settings.DefaultSessionClose)
            throw new ArgumentException("Exchange-calendar configuration is invalid.");

        var path = ResolvePath(settings.FilePath);
        var entries = DatasetQualityCertifier.ParseCalendar(
            await File.ReadAllLinesAsync(path, cancellationToken));
        return new(settings.Id, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId),
            settings.DefaultSessionOpen, settings.DefaultSessionClose,
            entries.Holidays, entries.SpecialSessions);
    }

    private static string ResolvePath(string value)
    {
        var direct = Path.GetFullPath(value);
        if (Path.IsPathRooted(value) || File.Exists(direct)) return direct;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) continue;
            return Path.GetFullPath(value, directory.FullName);
        }
        throw new FileNotFoundException("The configured exchange-calendar file was not found.", direct);
    }
}
