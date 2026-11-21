using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Infrastructure.Services.Saas;

namespace Ecosys.Payroll.Tests.Engine;

public class InvoiceCalculatorTests
{
    private static readonly Plan Growth = new() { Code = "growth", Name = "Growth", MonthlyBaseFee = 7_500m, IncludedEmployees = 25, PricePerAdditionalEmployee = 200m };
    private static readonly DateOnly Start = new(2026, 3, 1), End = new(2026, 3, 31);

    [Fact]
    public void Base_fee_only_when_within_included_employees()
    {
        // 18 employees ≤ 25 included: 7,500.00; VAT 16% = 1,200.00; total 8,700.00.
        var (lines, subtotal, vat, total) = InvoiceCalculator.Calculate(Growth, 18, 0.16m, Start, End);
        Assert.Single(lines);
        Assert.Equal(7_500m, subtotal); Assert.Equal(1_200m, vat); Assert.Equal(8_700m, total);
    }

    [Fact]
    public void Each_employee_above_the_allowance_is_charged()
    {
        // 30 employees: 5 extra × 200 = 1,000; subtotal 8,500; VAT 1,360; total 9,860.
        var (lines, subtotal, vat, total) = InvoiceCalculator.Calculate(Growth, 30, 0.16m, Start, End);
        Assert.Equal(2, lines.Count);
        Assert.Equal(5m, lines[1].Quantity); Assert.Equal(1_000m, lines[1].Amount);
        Assert.Equal(8_500m, subtotal); Assert.Equal(1_360m, vat); Assert.Equal(9_860m, total);
    }

    [Fact]
    public void Vat_is_rounded_to_cents()
    {
        // 1 extra at 133.33: subtotal 7,633.33; VAT 16% = 1,221.3328 → 1,221.33; total 8,854.66.
        var plan = new Plan { Code = "x", Name = "X", MonthlyBaseFee = 7_500m, IncludedEmployees = 0, PricePerAdditionalEmployee = 133.33m };
        var (_, subtotal, vat, total) = InvoiceCalculator.Calculate(plan, 1, 0.16m, Start, End);
        Assert.Equal(7_633.33m, subtotal); Assert.Equal(1_221.33m, vat); Assert.Equal(8_854.66m, total);
    }
}
