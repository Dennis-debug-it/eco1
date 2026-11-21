using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfferLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offer_letter_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    signatory_name = table.Column<string>(type: "text", nullable: true),
                    signatory_title = table.Column<string>(type: "text", nullable: true),
                    probation_months = table.Column<int>(type: "integer", nullable: false),
                    accept_within_days = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_offer_letter_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_offer_letter_templates_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_letters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    signatory_name = table.Column<string>(type: "text", nullable: true),
                    signatory_title = table.Column<string>(type: "text", nullable: true),
                    basic = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    job_title = table.Column<string>(type: "text", nullable: false),
                    accept_by = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_by = table.Column<string>(type: "text", nullable: true),
                    sent_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response_token_hash = table.Column<string>(type: "text", nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    response_name = table.Column<string>(type: "text", nullable: true),
                    response_ip = table.Column<string>(type: "text", nullable: true),
                    recorded_by_hr = table.Column<bool>(type: "boolean", nullable: false),
                    decline_reason = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_offer_letters", x => x.id);
                    table.ForeignKey(
                        name: "fk_offer_letters_job_applications_job_application_id",
                        column: x => x.job_application_id,
                        principalTable: "job_applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_offer_letters_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_offer_letter_templates_legal_entity_id",
                table: "offer_letter_templates",
                column: "legal_entity_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offer_letter_templates_tenant_id",
                table: "offer_letter_templates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_offer_letters_job_application_id_version",
                table: "offer_letters",
                columns: new[] { "job_application_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offer_letters_legal_entity_id",
                table: "offer_letters",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_offer_letters_response_token_hash",
                table: "offer_letters",
                column: "response_token_hash");

            migrationBuilder.CreateIndex(
                name: "ix_offer_letters_tenant_id",
                table: "offer_letters",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offer_letter_templates");

            migrationBuilder.DropTable(
                name: "offer_letters");
        }
    }
}
