using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPayrollSettingsDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Settings added after legal entities were created deserialise as 0 when absent from the JSON
            // (e.g. 0 working days would value one absent day at a whole month's basic). Backfill defaults;
            // existing keys win because the right-hand side of || takes precedence.
            migrationBuilder.Sql("""
                UPDATE legal_entities
                SET settings = '{"StandardMonthlyHours": 195, "StandardMonthlyWorkingDays": 22, "OvertimeMultiplier": 1.5, "RestDayOvertimeMultiplier": 2.0, "NotifyEmployeesOnPayslip": true}'::jsonb || settings;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
