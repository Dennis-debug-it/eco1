using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>
/// The checks every employee record passes, wherever it comes from (the Employees screen or a migration import).
/// Each normalises Kenyan identifiers in place and returns every problem found, so a caller can show them all at once.
/// </summary>
public static class EmployeeRules
{
    public static List<string> IdentifierProblems(Employee e)
    {
        var problems = new List<string>();
        e.NationalId = KenyaIdentifiers.NormaliseNationalId(e.NationalId);
        if (e.NationalId is not null && !KenyaIdentifiers.IsValidNationalId(e.NationalId)) problems.Add("National ID should be 6–9 digits. For non-citizens, leave it blank and enter the passport or alien ID number.");
        e.PassportNumber = string.IsNullOrWhiteSpace(e.PassportNumber) ? null : e.PassportNumber.Trim().ToUpperInvariant();
        foreach (var (label, value, set) in new (string, string?, Action<string?>)[] { ("Phone", e.Phone, v => e.Phone = v), ("Emergency contact phone", e.EmergencyContactPhone, v => e.EmergencyContactPhone = v) })
        {
            if (string.IsNullOrWhiteSpace(value)) { set(null); continue; }
            var n = KenyaIdentifiers.NormalisePhone(value);
            if (n is null) problems.Add($"{label} '{value}' isn't a valid number. Use 07XX XXX XXX, 01XX XXX XXX or +country code.");
            else set(n);
        }
        e.Email = string.IsNullOrWhiteSpace(e.Email) ? null : e.Email.Trim().ToLowerInvariant();
        if (e.Email is not null && (!e.Email.Contains('@') || e.Email.Contains(' '))) problems.Add($"Email '{e.Email}' isn't valid.");
        return problems;
    }

    public static List<string> StatutoryProblems(EmployeeStatutoryDetails s)
    {
        var problems = new List<string>();
        s.KraPin = KenyaIdentifiers.NormalisePin(s.KraPin);
        if (s.KraPin is not null && !KenyaIdentifiers.IsValidPin(s.KraPin))
            problems.Add(s.KraPin.StartsWith('P') ? "KRA PIN starting with P belongs to a company. An employee's PIN starts with A." : "KRA PIN should be 11 characters: A, nine digits and a letter (e.g. A012345678Z).");
        s.NssfNumber = KenyaIdentifiers.NormaliseMembership(s.NssfNumber);
        if (s.NssfNumber is not null && !KenyaIdentifiers.IsPlausibleMembership(s.NssfNumber)) problems.Add("NSSF number should be 4–20 letters or digits.");
        s.ShifNumber = KenyaIdentifiers.NormaliseMembership(s.ShifNumber);
        if (s.ShifNumber is not null && !KenyaIdentifiers.IsPlausibleMembership(s.ShifNumber)) problems.Add("SHA (SHIF) number should be 4–20 letters or digits.");
        s.NhifNumber = KenyaIdentifiers.NormaliseMembership(s.NhifNumber);
        return problems;
    }

    public static void ThrowIfAny(List<string> problems)
    {
        if (problems.Count > 0) throw new DomainException(string.Join(" ", problems), "VALIDATION");
    }
}
