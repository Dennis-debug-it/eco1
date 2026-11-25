using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DailyPay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "daily_rate",
                table: "employee_salary_assignments",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pay_basis",
                table: "employee_salary_assignments",
                type: "text",
                nullable: false,
                // Every existing salary is a monthly one; "" would not read back as a PayBasis.
                defaultValue: "Monthly");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "daily_rate",
                table: "employee_salary_assignments");

            migrationBuilder.DropColumn(
                name: "pay_basis",
                table: "employee_salary_assignments");
        }
    }
}
