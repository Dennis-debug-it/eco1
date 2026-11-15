namespace Ecosys.Payroll.Engine.Jurisdictions.Kenya;

public static class KenyaRuleCodes
{
    public const string Jurisdiction = "KE";

    public const string Paye = "KE.PAYE";
    public const string Nssf = "KE.NSSF";
    public const string Shif = "KE.SHIF";
    public const string HousingLevy = "KE.AHL";
    public const string Nita = "KE.NITA";

    /// <summary>Parameters-only rule holding allowable-deduction caps (pension, PRMF, mortgage interest).</summary>
    public const string AllowableDeductions = "KE.PAYE.ALLOWABLE";

    public static readonly string[] All = [Paye, Nssf, Shif, HousingLevy, Nita, AllowableDeductions];
}
