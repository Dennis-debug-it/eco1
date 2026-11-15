namespace Ecosys.Payroll.Engine.Model;

public enum ResultLineType
{
    Earning,
    StatutoryDeduction,
    Deduction,
    EmployerContribution,
    TaxRelief,
    Information,
}

/// <summary>One line of the gross-to-net breakdown.</summary>
public sealed record PayrollResultLine
{
    public required ResultLineType Type { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>Human-readable working, e.g. "6% x 8,000.00".</summary>
    public string? Working { get; init; }

    /// <summary>The statutory rule version that produced this line, when applicable.</summary>
    public Guid? StatutoryRuleVersionId { get; init; }
    public string? StatutoryRuleCode { get; init; }

    public Guid? ComponentId { get; init; }
    public string? GlAccountCode { get; init; }
    public bool Taxable { get; init; }
    public bool Pensionable { get; init; }
    public int Sequence { get; init; }
}

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record PayrollValidationIssue(ValidationSeverity Severity, string Code, string Message);

/// <summary>Immutable gross-to-net result for one employee.</summary>
public sealed record EmployeePayrollResult
{
    public required Guid EmployeeId { get; init; }
    public required string EmployeeNumber { get; init; }
    public required string DisplayName { get; init; }

    public required decimal BasicPay { get; init; }
    public required decimal GrossPay { get; init; }
    public required decimal TaxableEarnings { get; init; }
    public required decimal PensionableEarnings { get; init; }

    /// <summary>Taxable earnings less allowable deductions — the figure PAYE bands are applied to.</summary>
    public required decimal TaxablePay { get; init; }

    public required decimal GrossTax { get; init; }
    public required decimal TotalReliefs { get; init; }
    public required decimal Paye { get; init; }

    public required decimal TotalStatutoryDeductions { get; init; }
    public required decimal TotalOtherDeductions { get; init; }
    public required decimal TotalEmployeeDeductions { get; init; }
    public required decimal NetPay { get; init; }
    public required decimal TotalEmployerContributions { get; init; }
    public required decimal TotalEmployerCost { get; init; }

    public required IReadOnlyList<PayrollResultLine> Lines { get; init; }
    public required IReadOnlyList<PayrollValidationIssue> Issues { get; init; }

    /// <summary>Every statutory rule version consulted, for audit and historical reproduction.</summary>
    public required IReadOnlyList<Guid> StatutoryRuleVersionIds { get; init; }

    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);

    public decimal StatutoryAmount(string ruleCode) => Lines
        .Where(l => l.Type == ResultLineType.StatutoryDeduction && l.StatutoryRuleCode == ruleCode)
        .Sum(l => l.Amount);

    public decimal EmployerAmount(string ruleCode) => Lines
        .Where(l => l.Type == ResultLineType.EmployerContribution && l.StatutoryRuleCode == ruleCode)
        .Sum(l => l.Amount);
}
