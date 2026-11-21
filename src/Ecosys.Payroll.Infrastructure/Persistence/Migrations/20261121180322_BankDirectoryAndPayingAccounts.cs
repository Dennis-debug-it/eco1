using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankDirectoryAndPayingAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "employee_bank_account_id",
                table: "payroll_payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "debit_account_id",
                table: "payment_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                table: "employee_bank_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "verified_by",
                table: "employee_bank_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bank_directory_banks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    swift_code = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_directory_banks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bank_directory_branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_code = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_directory_branches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "company_bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    bank_code = table.Column<string>(type: "text", nullable: false),
                    bank_name = table.Column<string>(type: "text", nullable: true),
                    branch_code = table.Column<string>(type: "text", nullable: true),
                    branch_name = table.Column<string>(type: "text", nullable: true),
                    account_name = table.Column<string>(type: "text", nullable: false),
                    account_number = table.Column<string>(type: "text", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_bank_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_company_bank_accounts_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_payments_employee_bank_account_id_status",
                table: "payroll_payments",
                columns: new[] { "employee_bank_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_batches_debit_account_id",
                table: "payment_batches",
                column: "debit_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_directory_banks_code_tenant_id",
                table: "bank_directory_banks",
                columns: new[] { "code", "tenant_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_bank_directory_branches_bank_code_code_tenant_id",
                table: "bank_directory_branches",
                columns: new[] { "bank_code", "code", "tenant_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_company_bank_accounts_legal_entity_id",
                table: "company_bank_accounts",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_company_bank_accounts_tenant_id",
                table: "company_bank_accounts",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_payment_batches_company_bank_accounts_debit_account_id",
                table: "payment_batches",
                column: "debit_account_id",
                principalTable: "company_bank_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // New setting: write the safe value (a second person verifies bank details) explicitly; existing keys win.
            migrationBuilder.Sql("""
                UPDATE legal_entities
                SET settings = '{"AllowBankDetailSelfVerification": false}'::jsonb || settings;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payment_batches_company_bank_accounts_debit_account_id",
                table: "payment_batches");

            migrationBuilder.DropTable(
                name: "bank_directory_banks");

            migrationBuilder.DropTable(
                name: "bank_directory_branches");

            migrationBuilder.DropTable(
                name: "company_bank_accounts");

            migrationBuilder.DropIndex(
                name: "ix_payroll_payments_employee_bank_account_id_status",
                table: "payroll_payments");

            migrationBuilder.DropIndex(
                name: "ix_payment_batches_debit_account_id",
                table: "payment_batches");

            migrationBuilder.DropColumn(
                name: "employee_bank_account_id",
                table: "payroll_payments");

            migrationBuilder.DropColumn(
                name: "debit_account_id",
                table: "payment_batches");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "employee_bank_accounts");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "employee_bank_accounts");
        }
    }
}
