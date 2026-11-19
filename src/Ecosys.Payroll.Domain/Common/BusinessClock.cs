namespace Ecosys.Payroll.Domain.Common;

/// <summary>
/// Calendar dates in the organisation's time zone (East Africa Time, UTC+3, no daylight saving). Using the UTC date
/// is wrong between midnight and 03:00 in Nairobi: it gives yesterday, which matters for leave, expiry dates,
/// certificates and anything stamped "today".
/// </summary>
public static class BusinessClock
{
    public const string DefaultZoneId = "Africa/Nairobi";

    public static TimeZoneInfo Zone { get; private set; } = Resolve(DefaultZoneId);

    /// <summary>Set once at startup from configuration (<c>Payroll:TimeZone</c>).</summary>
    public static void Configure(string? zoneId) => Zone = Resolve(string.IsNullOrWhiteSpace(zoneId) ? DefaultZoneId : zoneId);

    public static DateTimeOffset Now(TimeProvider? clock = null) => TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), Zone);

    public static DateOnly Today(TimeProvider? clock = null) => DateOnly.FromDateTime(Now(clock).DateTime);

    public static DateTimeOffset ToLocal(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

    /// <summary>"EAT" for Nairobi; otherwise the UTC offset.</summary>
    public static string Abbreviation => Zone.Id == DefaultZoneId ? "EAT" : $"UTC{Zone.BaseUtcOffset:+hh\\:mm;-hh\\:mm}";

    private static TimeZoneInfo Resolve(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) when (id == DefaultZoneId)
        {
            // Minimal container images can lack tzdata. Kenya has used UTC+3 without daylight saving since 1960.
            return TimeZoneInfo.CreateCustomTimeZone(DefaultZoneId, TimeSpan.FromHours(3), "East Africa Time", "EAT");
        }
    }
}
