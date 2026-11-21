using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayslipBalancesAndDependantApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "leave_balances",
                table: "payslips",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "nssf_contributions_to_date",
                table: "payslips",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "nssf_opening_balance",
                table: "employee_statutory_details",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approved_at",
                table: "dependants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by",
                table: "dependants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "document_file_ids",
                table: "dependants",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "dependants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rejected_at",
                table: "dependants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rejected_by",
                table: "dependants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "dependants",
                type: "text",
                nullable: true);

            // Dependants recorded before the approval workflow existed were already in effect: keep them approved.
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "dependants",
                type: "text",
                nullable: false,
                defaultValue: "Approved");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                table: "dependants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "verified_by",
                table: "dependants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_dependants_tenant_id_status",
                table: "dependants",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_dependants_tenant_id_status",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "leave_balances",
                table: "payslips");

            migrationBuilder.DropColumn(
                name: "nssf_contributions_to_date",
                table: "payslips");

            migrationBuilder.DropColumn(
                name: "nssf_opening_balance",
                table: "employee_statutory_details");

            migrationBuilder.DropColumn(
                name: "approved_at",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "approved_by",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "document_file_ids",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "rejected_at",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "rejected_by",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "status",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "dependants");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "dependants");
        }
    }
}
