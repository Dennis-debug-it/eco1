using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Engine.Rules;

namespace Ecosys.Payroll.Engine;

/// <summary>Jurisdiction-specific gross-to-net calculator. Implementations must be pure and thread-safe.</summary>
public interface IPayrollCalculator
{
    string Jurisdiction { get; }

    EmployeePayrollResult Calculate(EmployeePayrollInput employee, PayrollPeriodInput period, StatutoryRuleSet rules);
}

/// <summary>Resolves the calculator for a jurisdiction. Additional countries register here.</summary>
public sealed class PayrollCalculatorRegistry
{
    private readonly Dictionary<string, IPayrollCalculator> _calculators;

    public PayrollCalculatorRegistry(IEnumerable<IPayrollCalculator> calculators)
    {
        _calculators = calculators.ToDictionary(c => c.Jurisdiction, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Jurisdictions => _calculators.Keys;

    public IPayrollCalculator For(string jurisdiction) =>
        _calculators.TryGetValue(jurisdiction, out var calc)
            ? calc
            : throw new StatutoryConfigurationException($"No payroll calculator is registered for jurisdiction '{jurisdiction}'.");
}
