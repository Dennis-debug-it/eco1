using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataMigrationImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "go_live_from",
                table: "tenants",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "go_live_signed_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "go_live_signed_by",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "migration_started_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "migration_started_by",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    applied_by_name = table.Column<string>(type: "text", nullable: true),
                    undone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    undone_by_name = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: false),
                    records = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_import_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batches_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_history_months",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    gross = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    taxable_earnings = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    allowable_deductions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    taxable_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    gross_tax = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    reliefs = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    paye = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    nssf = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    shif = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    housing_levy = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    net_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_payroll_history_months", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_history_months_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_history_months_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_history_months_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_legal_entity_id",
                table: "import_batches",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_legal_entity_id_created_at",
                table: "import_batches",
                columns: new[] { "legal_entity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_tenant_id",
                table: "import_batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_history_months_employee_id_year_month",
                table: "payroll_history_months",
                columns: new[] { "employee_id", "year", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_history_months_import_batch_id",
                table: "payroll_history_months",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_history_months_legal_entity_id",
                table: "payroll_history_months",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_history_months_legal_entity_id_year",
                table: "payroll_history_months",
                columns: new[] { "legal_entity_id", "year" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_history_months_tenant_id",
                table: "payroll_history_months",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_history_months");

            migrationBuilder.DropTable(
                name: "import_batches");

            migrationBuilder.DropColumn(
                name: "go_live_from",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "go_live_signed_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "go_live_signed_by",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "migration_started_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "migration_started_by",
                table: "tenants");
        }
    }
}
