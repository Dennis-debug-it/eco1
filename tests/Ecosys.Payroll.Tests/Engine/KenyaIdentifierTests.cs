using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Tests.Engine;

public class KenyaIdentifierTests
{
    [Theory]
    [InlineData("A012345678Z", true, true)]
    [InlineData(" a012345678z ", true, true)] // normalised
    [InlineData("P051234567A", false, true)]  // company PIN: valid for an employer…
    [InlineData("P051234567A", true, false)]  // …not for an employee
    [InlineData("A01234567Z", true, false)]   // 8 digits
    [InlineData("A0123456789", true, false)]  // no check letter
    [InlineData("B012345678Z", true, false)]
    public void Kra_pin(string raw, bool individual, bool ok) =>
        Assert.Equal(ok, KenyaIdentifiers.IsValidPin(KenyaIdentifiers.NormalisePin(raw), individual));

    [Theory]
    [InlineData("0712 345 678", "+254712345678")]
    [InlineData("0110-345-678", "+254110345678")]
    [InlineData("712345678", "+254712345678")]
    [InlineData("254712345678", "+254712345678")]
    [InlineData("+254 712 345 678", "+254712345678")]
    [InlineData("+256772123456", "+256772123456")] // Ugandan number kept as is
    [InlineData("0201234567", null)]                // Nairobi landline prefix 020 isn't a mobile
    [InlineData("07123", null)]
    [InlineData("call me", null)]
    public void Phone(string raw, string? expected) => Assert.Equal(expected, KenyaIdentifiers.NormalisePhone(raw));

    [Theory]
    [InlineData("12345678", true)]
    [InlineData("1234567", true)]
    [InlineData("12345", false)]
    [InlineData("A1234567", false)]
    public void National_id(string raw, bool ok) => Assert.Equal(ok, KenyaIdentifiers.IsValidNationalId(KenyaIdentifiers.NormaliseNationalId(raw)));

    [Fact]
    public void Business_date_is_nairobi_not_utc()
    {
        // 22:30 UTC on 31 January is 01:30 on 1 February in Nairobi.
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 31, 22, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 2, 1), BusinessClock.Today(clock));
        Assert.Equal(TimeSpan.FromHours(3), BusinessClock.Now(clock).Offset);
        Assert.Equal("EAT", BusinessClock.Abbreviation);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
