namespace Trading.Domain.MarketData;

// Values are the duration in minutes. Session/calendar alignment belongs to the importer.
public enum Timeframe { Minute1 = 1, Minute3 = 3, Minute5 = 5, Minute15 = 15, Minute30 = 30, Hour1 = 60, Day1 = 1440 }
