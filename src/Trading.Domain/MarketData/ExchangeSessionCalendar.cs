using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Trading.Domain.MarketData;

public sealed record ExchangeSession(DateOnly Date, TimeOnly Open, TimeOnly Close);

/// <summary>Immutable deterministic exchange-session truth shared by research and execution.</summary>
public sealed record ExchangeSessionCalendar
{
    public ExchangeSessionCalendar(string id, TimeZoneInfo timeZone, TimeOnly defaultSessionOpen,
        TimeOnly defaultSessionClose, IReadOnlySet<DateOnly> holidays,
        IReadOnlyDictionary<DateOnly, ExchangeSession> specialSessions)
    {
        if (string.IsNullOrWhiteSpace(id) || timeZone is null || defaultSessionOpen >= defaultSessionClose ||
            holidays is null || specialSessions is null || specialSessions.Keys.Any(holidays.Contains) ||
            specialSessions.Any(item => item.Key != item.Value.Date || item.Value.Open >= item.Value.Close))
            throw new ArgumentException("Exchange calendar is invalid.");

        Id = id.Trim();
        TimeZone = timeZone;
        DefaultSessionOpen = defaultSessionOpen;
        DefaultSessionClose = defaultSessionClose;
        Holidays = holidays.ToFrozenSet();
        SpecialSessions = specialSessions.ToFrozenDictionary();
        Sha256 = Fingerprint();
    }

    public ExchangeSessionCalendar(string id, TimeZoneInfo timeZone, TimeOnly sessionOpen,
        TimeOnly sessionClose, IReadOnlySet<DateOnly> holidays)
        : this(id, timeZone, sessionOpen, sessionClose, holidays,
            new Dictionary<DateOnly, ExchangeSession>()) { }

    public string Id { get; }
    public TimeZoneInfo TimeZone { get; }
    public TimeOnly DefaultSessionOpen { get; }
    public TimeOnly DefaultSessionClose { get; }
    public IReadOnlySet<DateOnly> Holidays { get; }
    public IReadOnlyDictionary<DateOnly, ExchangeSession> SpecialSessions { get; }
    public TimeOnly SessionOpen => DefaultSessionOpen;
    public TimeOnly SessionClose => DefaultSessionClose;
    public string Sha256 { get; }

    public bool IsTradingDate(DateOnly date) => TryGetSession(date, out _);

    public bool TryGetSession(DateOnly date, out ExchangeSession session)
    {
        if (SpecialSessions.TryGetValue(date, out session!)) return true;
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || Holidays.Contains(date))
        {
            session = default!;
            return false;
        }

        session = new(date, DefaultSessionOpen, DefaultSessionClose);
        return true;
    }

    public DateTime ToUtc(DateOnly date, TimeOnly time) => TimeZoneInfo.ConvertTimeToUtc(
        date.ToDateTime(time, DateTimeKind.Unspecified), TimeZone);

    public DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone));

    public TimeOnly LocalTime(DateTime utc) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone));

    private string Fingerprint()
    {
        var builder = new StringBuilder();
        builder.Append(TimeZone.Id).Append('|')
            .Append(DefaultSessionOpen.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)).Append('|')
            .Append(DefaultSessionClose.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)).AppendLine();
        foreach (var holiday in Holidays.Order())
            builder.Append("holiday|").Append(holiday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine();
        foreach (var item in SpecialSessions.OrderBy(item => item.Key))
            builder.Append("special|").Append(item.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
                .Append(item.Value.Open.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)).Append('|')
                .Append(item.Value.Close.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
