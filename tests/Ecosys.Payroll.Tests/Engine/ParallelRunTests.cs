using Ecosys.Payroll.Infrastructure.Services;

namespace Ecosys.Payroll.Tests.Engine;

public class ParallelRunTests
{
    [Fact]
    public void Parses_common_register_layouts()
    {
        const string csv = "Staff No;Employee Name;Gross Pay;P.A.Y.E;NSSF;SHA;A.H.L;Net Pay\n" +
                           "SL001;\"Kamau, Wanjiru\";\"KES 450,000.00\";121,523.35;6,480.00;12,375.00;6,750.00;302,871.65\n" +
                           "SL002;Otieno Odhiambo;(0);-;;;;0\n" +
                           ";TOTAL;450000;;;;;\n";
        var (rows, columns, warnings) = ParallelRunReconciler.ParseCsv(csv);
        Assert.Equal(["Gross", "PAYE", "NSSF", "SHIF", "HousingLevy", "Net"], columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Kamau, Wanjiru", rows[0].Name);
        Assert.Equal(450_000m, rows[0].Gross);
        Assert.Equal(121_523.35m, rows[0].Paye);
        Assert.Equal(6_750m, rows[0].HousingLevy);
        Assert.Equal(0m, rows[1].Paye);
        Assert.Null(rows[0].Taxable); // column not supplied
        Assert.Single(warnings); // the totals row
    }

    [Fact]
    public void Rejects_a_file_without_employee_numbers()
    {
        var ex = Assert.Throws<Ecosys.Payroll.Domain.Common.DomainException>(() => ParallelRunReconciler.ParseCsv("Name,Gross\nA,1\n"));
        Assert.Contains("employee number", ex.Message);
    }

    [Fact]
    public void Reconciles_within_tolerance_and_flags_missing_people()
    {
        RegisterFigures R(string no, decimal gross, decimal paye, decimal net) => new(no, no, gross, null, paye, null, null, null, null, net);
        var ecosys = new[] { R("A", 100_000, 23_000, 70_000), R("B", 50_000, 8_000, 38_000.40m), R("C", 40_000, 5_000, 31_000) };
        var legacy = new[] { R("A", 100_000, 23_000, 70_000), R("B", 50_000, 8_000, 38_000), R("D", 30_000, 2_000, 25_000) };
        string[] cols = ["Gross", "PAYE", "Net"];

        var strict = ParallelRunReconciler.Reconcile(ecosys, legacy, cols, 0m);
        Assert.Equal((1, 1, 1, 1), (strict.Matched, strict.Different, strict.OnlyInEcosys, strict.OnlyInLegacy));
        var b = strict.Rows.Single(r => r.EmployeeNumber == "B");
        Assert.Equal(0.40m, Assert.Single(b.Differences).Difference);
        Assert.False(strict.FullyReconciled);
        Assert.Equal(190_000m - 180_000m, strict.Totals.Single(t => t.Field == "Gross").Difference);

        // A whole-shilling tolerance absorbs the outgoing system's rounding.
        var rounded = ParallelRunReconciler.Reconcile(ecosys, legacy, cols, 1m);
        Assert.Equal(ReconcileStatus.Matched, rounded.Rows.Single(r => r.EmployeeNumber == "B").Status);
    }
}
