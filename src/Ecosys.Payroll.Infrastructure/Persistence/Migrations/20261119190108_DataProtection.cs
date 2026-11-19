using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymised_at",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "data_breaches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    data_categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    subjects_affected = table.Column<int>(type: "integer", nullable: true),
                    risk = table.Column<string>(type: "text", nullable: false),
                    cause = table.Column<string>(type: "text", nullable: true),
                    containment = table.Column<string>(type: "text", nullable: true),
                    reported_to_commissioner_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    commissioner_reference = table.Column<string>(type: "text", nullable: true),
                    not_reported_reason = table.Column<string>(type: "text", nullable: true),
                    subjects_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subjects_not_notified_reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_breaches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_breaches_tenant_id",
                table: "data_breaches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_breaches_tenant_id_reference",
                table: "data_breaches",
                columns: new[] { "tenant_id", "reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_breaches");

            migrationBuilder.DropColumn(
                name: "anonymised_at",
                table: "employees");
        }
    }
}
