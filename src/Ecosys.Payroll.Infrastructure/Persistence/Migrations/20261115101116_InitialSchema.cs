using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.CreateTable(
                name: "legal_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    registration_number = table.Column<string>(type: "text", nullable: false),
                    tax_pin = table.Column<string>(type: "text", nullable: false),
                    nssf_employer_number = table.Column<string>(type: "text", nullable: true),
                    shif_employer_number = table.Column<string>(type: "text", nullable: true),
                    nita_registration_number = table.Column<string>(type: "text", nullable: true),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    financial_year_start_month = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_entities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    table.PrimaryKey("pk_payroll_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "statutory_rule_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_code = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    authority = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    @base = table.Column<string>(name: "base", type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    employee_rate = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    employer_rate = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    minimum_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    maximum_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    rounding = table.Column<string>(type: "jsonb", nullable: false),
                    applicable_categories = table.Column<string>(type: "jsonb", nullable: false),
                    approval_status = table.Column<string>(type: "text", nullable: false),
                    legal_reference = table.Column<string>(type: "text", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approval_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statutory_rule_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    default_currency = table.Column<string>(type: "text", nullable: false),
                    default_jurisdiction = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    external_subject = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    roles = table.Column<string>(type: "jsonb", nullable: false),
                    legal_entity_ids = table.Column<string>(type: "jsonb", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    mfa_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_branches", x => x.id);
                    table.ForeignKey(
                        name: "fk_branches_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cost_centres",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    gl_segment = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_cost_centres", x => x.id);
                    table.ForeignKey(
                        name: "fk_cost_centres_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    manager_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_cost_centre_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.ForeignKey(
                        name: "fk_departments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "employee_leave_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    entitlement = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    accrued = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    taken = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    carried_over = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
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
                    table.PrimaryKey("pk_employee_leave_balances", x => x.id);
                    table.ForeignKey(
                        name: "fk_employee_leave_balances_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gl_account_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_code = table.Column<string>(type: "text", nullable: false),
                    expense_account = table.Column<string>(type: "text", nullable: false),
                    liability_account = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_gl_account_mappings", x => x.id);
                    table.ForeignKey(
                        name: "fk_gl_account_mappings_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "leave_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    leave_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    days = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    approver_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approver_comment = table.Column<string>(type: "text", nullable: true),
                    processed_in_run_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_leave_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_leave_requests_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "leave_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    annual_entitlement_days = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    is_paid = table.Column<bool>(type: "boolean", nullable: false),
                    accrues_monthly = table.Column<bool>(type: "boolean", nullable: false),
                    max_carry_over_days = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    requires_attachment = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_leave_types", x => x.id);
                    table.ForeignKey(
                        name: "fk_leave_types_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    applied_in_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    salary_component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_payroll_adjustments", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_adjustments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    frequency = table.Column<string>(type: "text", nullable: false),
                    pay_day = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_payroll_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_groups_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_journals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    posting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total_debit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_credit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    reversal_of_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_reference = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_payroll_journals", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_journals_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "salary_components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    default_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    default_rate = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    taxable = table.Column<bool>(type: "boolean", nullable: false),
                    pensionable = table.Column<bool>(type: "boolean", nullable: false),
                    is_basic = table.Column<bool>(type: "boolean", nullable: false),
                    deduction_treatment = table.Column<string>(type: "text", nullable: false),
                    employer_match_rate = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    recurrence = table.Column<string>(type: "text", nullable: false),
                    gl_account_code = table.Column<string>(type: "text", nullable: true),
                    liability_gl_account_code = table.Column<string>(type: "text", nullable: true),
                    show_on_payslip = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_salary_components", x => x.id);
                    table.ForeignKey(
                        name: "fk_salary_components_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "salary_structures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_salary_structures", x => x.id);
                    table.ForeignKey(
                        name: "fk_salary_structures_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_number = table.Column<string>(type: "text", nullable: false),
                    first_name = table.Column<string>(type: "text", nullable: false),
                    middle_name = table.Column<string>(type: "text", nullable: true),
                    last_name = table.Column<string>(type: "text", nullable: false),
                    national_id = table.Column<string>(type: "text", nullable: true),
                    passport_number = table.Column<string>(type: "text", nullable: true),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    gender = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    physical_address = table.Column<string>(type: "text", nullable: true),
                    emergency_contact_name = table.Column<string>(type: "text", nullable: true),
                    emergency_contact_phone = table.Column<string>(type: "text", nullable: true),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    hire_date = table.Column<DateOnly>(type: "date", nullable: false),
                    contract_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    termination_date = table.Column<DateOnly>(type: "date", nullable: true),
                    job_title = table.Column<string>(type: "text", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_centre_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporting_manager_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_location = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_employees", x => x.id);
                    table.ForeignKey(
                        name: "fk_employees_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_employees_cost_centres_cost_centre_id",
                        column: x => x.cost_centre_id,
                        principalTable: "cost_centres",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_employees_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_employees_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_employees_payroll_groups_payroll_group_id",
                        column: x => x.payroll_group_id,
                        principalTable: "payroll_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    pay_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_payroll_periods", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_periods_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_periods_payroll_groups_payroll_group_id",
                        column: x => x.payroll_group_id,
                        principalTable: "payroll_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payroll_journal_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    debit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    credit = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    cost_centre_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_payroll_journal_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_journal_lines_payroll_journals_payroll_journal_id",
                        column: x => x.payroll_journal_id,
                        principalTable: "payroll_journals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "salary_structure_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    rate = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_salary_structure_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_salary_structure_lines_salary_components_salary_component_id",
                        column: x => x.salary_component_id,
                        principalTable: "salary_components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_salary_structure_lines_salary_structures_salary_structure_id",
                        column: x => x.salary_structure_id,
                        principalTable: "salary_structures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    bank_name = table.Column<string>(type: "text", nullable: true),
                    bank_code = table.Column<string>(type: "text", nullable: true),
                    branch_name = table.Column<string>(type: "text", nullable: true),
                    branch_code = table.Column<string>(type: "text", nullable: true),
                    account_name = table.Column<string>(type: "text", nullable: false),
                    account_number = table.Column<string>(type: "text", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    split_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
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
                    table.PrimaryKey("pk_employee_bank_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_employee_bank_accounts_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employee_bank_accounts_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "employee_salary_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: true),
                    basic_salary = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    change_reason = table.Column<string>(type: "text", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_employee_salary_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_employee_salary_assignments_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employee_salary_assignments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_employee_salary_assignments_salary_structures_salary_struct",
                        column: x => x.salary_structure_id,
                        principalTable: "salary_structures",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "employee_statutory_details",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kra_pin = table.Column<string>(type: "text", nullable: true),
                    nssf_number = table.Column<string>(type: "text", nullable: true),
                    shif_number = table.Column<string>(type: "text", nullable: true),
                    nhif_number = table.Column<string>(type: "text", nullable: true),
                    has_disability_exemption = table.Column<bool>(type: "boolean", nullable: false),
                    disability_exemption_certificate = table.Column<string>(type: "text", nullable: true),
                    disability_exempt_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    disability_exemption_expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    statutory_exemptions = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_employee_statutory_details", x => x.id);
                    table.ForeignKey(
                        name: "fk_employee_statutory_details_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employee_statutory_details_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "employment_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    job_title = table.Column<string>(type: "text", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_centre_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_reason = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_employment_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_employment_records_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employment_records_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    principal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    balance = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    instalment_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_staff_loans", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_loans_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_loans_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    run_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    prepared_by = table.Column<Guid>(type: "uuid", nullable: true),
                    prepared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    calculated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finalised_by = table.Column<Guid>(type: "uuid", nullable: true),
                    finalised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversal_reason = table.Column<string>(type: "text", nullable: true),
                    original_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duplicate_authorised = table.Column<bool>(type: "boolean", nullable: false),
                    employee_count = table.Column<int>(type: "integer", nullable: false),
                    total_gross = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_taxable = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_paye = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_nssf_employee = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_nssf_employer = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_shif = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_housing_levy_employee = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_housing_levy_employer = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_nita = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_other_deductions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_employee_deductions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_net = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_employer_contributions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_employer_cost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    warning_count = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_payroll_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_runs_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_runs_payroll_groups_payroll_group_id",
                        column: x => x.payroll_group_id,
                        principalTable: "payroll_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_payroll_runs_payroll_periods_payroll_period_id",
                        column: x => x.payroll_period_id,
                        principalTable: "payroll_periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_salary_assignment_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_salary_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    rate = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
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
                    table.PrimaryKey("pk_employee_salary_assignment_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_employee_salary_assignment_lines_employee_salary_assignment",
                        column: x => x.employee_salary_assignment_id,
                        principalTable: "employee_salary_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employee_salary_assignment_lines_salary_components_salary_c",
                        column: x => x.salary_component_id,
                        principalTable: "salary_components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    payment_count = table.Column<int>(type: "integer", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    generated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_payment_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_batches_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_batches_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payroll_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "text", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    from_status = table.Column<string>(type: "text", nullable: false),
                    to_status = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_payroll_approvals", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_approvals_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_approvals_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payroll_run_employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_number = table.Column<string>(type: "text", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_centre_id = table.Column<Guid>(type: "uuid", nullable: true),
                    salary_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    basic_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    gross_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    taxable_earnings = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    pensionable_earnings = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    taxable_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    gross_tax = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_reliefs = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    paye = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    nssf = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    shif = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    housing_levy = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_statutory_deductions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_other_deductions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_employee_deductions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    net_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_employer_contributions = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_employer_cost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    previous_net_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    has_errors = table.Column<bool>(type: "boolean", nullable: false),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    statutory_rule_version_ids = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_payroll_run_employees", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_run_employees_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_run_employees_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_run_employees_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payroll_variable_inputs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_payroll_variable_inputs", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_variable_inputs_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_variable_inputs_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_payroll_variable_inputs_salary_components_salary_component_",
                        column: x => x.salary_component_id,
                        principalTable: "salary_components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payslips",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payslip_number = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    gross_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    net_pay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    first_viewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_payslips", x => x.id);
                    table.ForeignKey(
                        name: "fk_payslips_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payslips_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payroll_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    bank_name = table.Column<string>(type: "text", nullable: true),
                    bank_code = table.Column<string>(type: "text", nullable: true),
                    branch_code = table.Column<string>(type: "text", nullable: true),
                    account_name = table.Column<string>(type: "text", nullable: true),
                    account_number = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_payroll_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_payments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_payments_payment_batches_payment_batch_id",
                        column: x => x.payment_batch_id,
                        principalTable: "payment_batches",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_payroll_payments_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payroll_result_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    working = table.Column<string>(type: "text", nullable: true),
                    statutory_rule_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    statutory_rule_code = table.Column<string>(type: "text", nullable: true),
                    component_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gl_account_code = table.Column<string>(type: "text", nullable: true),
                    taxable = table.Column<bool>(type: "boolean", nullable: false),
                    pensionable = table.Column<bool>(type: "boolean", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_payroll_result_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_result_lines_payroll_run_employees_payroll_run_empl",
                        column: x => x.payroll_run_employee_id,
                        principalTable: "payroll_run_employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_branches_legal_entity_id",
                table: "branches",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_branches_legal_entity_id_code",
                table: "branches",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_branches_tenant_id",
                table: "branches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_cost_centres_legal_entity_id",
                table: "cost_centres",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_cost_centres_legal_entity_id_code",
                table: "cost_centres",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cost_centres_tenant_id",
                table: "cost_centres",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_legal_entity_id",
                table: "departments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_legal_entity_id_code",
                table: "departments",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_tenant_id",
                table: "departments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_bank_accounts_employee_id",
                table: "employee_bank_accounts",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_bank_accounts_legal_entity_id",
                table: "employee_bank_accounts",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_bank_accounts_tenant_id",
                table: "employee_bank_accounts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_leave_balances_employee_id_leave_type_id_year",
                table: "employee_leave_balances",
                columns: new[] { "employee_id", "leave_type_id", "year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_leave_balances_legal_entity_id",
                table: "employee_leave_balances",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_leave_balances_tenant_id",
                table: "employee_leave_balances",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignment_lines_employee_salary_assignment",
                table: "employee_salary_assignment_lines",
                column: "employee_salary_assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignment_lines_salary_component_id",
                table: "employee_salary_assignment_lines",
                column: "salary_component_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignment_lines_tenant_id",
                table: "employee_salary_assignment_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignments_employee_id_effective_from",
                table: "employee_salary_assignments",
                columns: new[] { "employee_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignments_legal_entity_id",
                table: "employee_salary_assignments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignments_salary_structure_id",
                table: "employee_salary_assignments",
                column: "salary_structure_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_salary_assignments_tenant_id",
                table: "employee_salary_assignments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_statutory_details_employee_id",
                table: "employee_statutory_details",
                column: "employee_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_statutory_details_legal_entity_id",
                table: "employee_statutory_details",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_statutory_details_tenant_id",
                table: "employee_statutory_details",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_branch_id",
                table: "employees",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_cost_centre_id",
                table: "employees",
                column: "cost_centre_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_department_id",
                table: "employees",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_legal_entity_id",
                table: "employees",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_legal_entity_id_employee_number",
                table: "employees",
                columns: new[] { "legal_entity_id", "employee_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employees_payroll_group_id",
                table: "employees",
                column: "payroll_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_tenant_id",
                table: "employees",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_tenant_id_status",
                table: "employees",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_employment_records_employee_id_effective_from",
                table: "employment_records",
                columns: new[] { "employee_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_employment_records_legal_entity_id",
                table: "employment_records",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employment_records_tenant_id",
                table: "employment_records",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_gl_account_mappings_legal_entity_id",
                table: "gl_account_mappings",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_gl_account_mappings_legal_entity_id_source_code",
                table: "gl_account_mappings",
                columns: new[] { "legal_entity_id", "source_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gl_account_mappings_tenant_id",
                table: "gl_account_mappings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_leave_requests_employee_id_status",
                table: "leave_requests",
                columns: new[] { "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_leave_requests_legal_entity_id",
                table: "leave_requests",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_leave_requests_tenant_id",
                table: "leave_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_leave_types_legal_entity_id",
                table: "leave_types",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_leave_types_legal_entity_id_code",
                table: "leave_types",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_leave_types_tenant_id",
                table: "leave_types",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_legal_entities_tenant_id",
                table: "legal_entities",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_legal_entities_tenant_id_tax_pin",
                table: "legal_entities",
                columns: new[] { "tenant_id", "tax_pin" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_batches_legal_entity_id",
                table: "payment_batches",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_batches_payroll_run_id",
                table: "payment_batches",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_batches_tenant_id",
                table: "payment_batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_adjustments_employee_id_status",
                table: "payroll_adjustments",
                columns: new[] { "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_adjustments_legal_entity_id",
                table: "payroll_adjustments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_adjustments_tenant_id",
                table: "payroll_adjustments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_approvals_legal_entity_id",
                table: "payroll_approvals",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_approvals_payroll_run_id",
                table: "payroll_approvals",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_approvals_tenant_id",
                table: "payroll_approvals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_audit_logs_entity_type_entity_id",
                table: "payroll_audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_audit_logs_tenant_id",
                table: "payroll_audit_logs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_audit_logs_tenant_id_occurred_at",
                table: "payroll_audit_logs",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_groups_legal_entity_id",
                table: "payroll_groups",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_groups_legal_entity_id_code",
                table: "payroll_groups",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_groups_tenant_id",
                table: "payroll_groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_journal_lines_payroll_journal_id",
                table: "payroll_journal_lines",
                column: "payroll_journal_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_journal_lines_tenant_id",
                table: "payroll_journal_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_journals_legal_entity_id",
                table: "payroll_journals",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_journals_payroll_run_id",
                table: "payroll_journals",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_journals_tenant_id",
                table: "payroll_journals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_payments_legal_entity_id",
                table: "payroll_payments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_payments_payment_batch_id",
                table: "payroll_payments",
                column: "payment_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_payments_payroll_run_id_status",
                table: "payroll_payments",
                columns: new[] { "payroll_run_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_payments_tenant_id",
                table: "payroll_payments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_periods_legal_entity_id",
                table: "payroll_periods",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_periods_payroll_group_id_period_start_period_end",
                table: "payroll_periods",
                columns: new[] { "payroll_group_id", "period_start", "period_end" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_periods_tenant_id",
                table: "payroll_periods",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_result_lines_payroll_run_employee_id_sequence",
                table: "payroll_result_lines",
                columns: new[] { "payroll_run_employee_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_result_lines_tenant_id",
                table: "payroll_result_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_run_employees_employee_id",
                table: "payroll_run_employees",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_run_employees_legal_entity_id",
                table: "payroll_run_employees",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_run_employees_payroll_run_id_employee_id",
                table: "payroll_run_employees",
                columns: new[] { "payroll_run_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_run_employees_tenant_id",
                table: "payroll_run_employees",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_runs_legal_entity_id",
                table: "payroll_runs",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_runs_legal_entity_id_reference",
                table: "payroll_runs",
                columns: new[] { "legal_entity_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_runs_payroll_group_id_payroll_period_id_status",
                table: "payroll_runs",
                columns: new[] { "payroll_group_id", "payroll_period_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_runs_payroll_period_id",
                table: "payroll_runs",
                column: "payroll_period_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_runs_tenant_id",
                table: "payroll_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_variable_inputs_legal_entity_id",
                table: "payroll_variable_inputs",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_variable_inputs_payroll_run_id_employee_id",
                table: "payroll_variable_inputs",
                columns: new[] { "payroll_run_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_variable_inputs_salary_component_id",
                table: "payroll_variable_inputs",
                column: "salary_component_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_variable_inputs_tenant_id",
                table: "payroll_variable_inputs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payslips_employee_id_period_end",
                table: "payslips",
                columns: new[] { "employee_id", "period_end" });

            migrationBuilder.CreateIndex(
                name: "ix_payslips_legal_entity_id",
                table: "payslips",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_payslips_payroll_run_employee_id",
                table: "payslips",
                column: "payroll_run_employee_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payslips_payroll_run_id",
                table: "payslips",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_payslips_tenant_id",
                table: "payslips",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_components_legal_entity_id",
                table: "salary_components",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_components_legal_entity_id_code",
                table: "salary_components",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_salary_components_tenant_id",
                table: "salary_components",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_structure_lines_salary_component_id",
                table: "salary_structure_lines",
                column: "salary_component_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_structure_lines_salary_structure_id",
                table: "salary_structure_lines",
                column: "salary_structure_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_structure_lines_tenant_id",
                table: "salary_structure_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_structures_legal_entity_id",
                table: "salary_structures",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_salary_structures_legal_entity_id_code",
                table: "salary_structures",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_salary_structures_tenant_id",
                table: "salary_structures",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_loans_employee_id_status",
                table: "staff_loans",
                columns: new[] { "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_loans_legal_entity_id",
                table: "staff_loans",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_loans_tenant_id",
                table: "staff_loans",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_statutory_rule_versions_jurisdiction_effective_from_effecti",
                table: "statutory_rule_versions",
                columns: new[] { "jurisdiction", "effective_from", "effective_to" });

            migrationBuilder.CreateIndex(
                name: "ix_statutory_rule_versions_jurisdiction_rule_code_version_tena",
                table: "statutory_rule_versions",
                columns: new[] { "jurisdiction", "rule_code", "version", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id",
                table: "users",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_email",
                table: "users",
                columns: new[] { "tenant_id", "email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_bank_accounts");

            migrationBuilder.DropTable(
                name: "employee_leave_balances");

            migrationBuilder.DropTable(
                name: "employee_salary_assignment_lines");

            migrationBuilder.DropTable(
                name: "employee_statutory_details");

            migrationBuilder.DropTable(
                name: "employment_records");

            migrationBuilder.DropTable(
                name: "gl_account_mappings");

            migrationBuilder.DropTable(
                name: "leave_requests");

            migrationBuilder.DropTable(
                name: "leave_types");

            migrationBuilder.DropTable(
                name: "payroll_adjustments");

            migrationBuilder.DropTable(
                name: "payroll_approvals");

            migrationBuilder.DropTable(
                name: "payroll_audit_logs");

            migrationBuilder.DropTable(
                name: "payroll_journal_lines");

            migrationBuilder.DropTable(
                name: "payroll_payments");

            migrationBuilder.DropTable(
                name: "payroll_result_lines");

            migrationBuilder.DropTable(
                name: "payroll_variable_inputs");

            migrationBuilder.DropTable(
                name: "payslips");

            migrationBuilder.DropTable(
                name: "salary_structure_lines");

            migrationBuilder.DropTable(
                name: "staff_loans");

            migrationBuilder.DropTable(
                name: "statutory_rule_versions");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "employee_salary_assignments");

            migrationBuilder.DropTable(
                name: "payroll_journals");

            migrationBuilder.DropTable(
                name: "payment_batches");

            migrationBuilder.DropTable(
                name: "payroll_run_employees");

            migrationBuilder.DropTable(
                name: "salary_components");

            migrationBuilder.DropTable(
                name: "salary_structures");

            migrationBuilder.DropTable(
                name: "employees");

            migrationBuilder.DropTable(
                name: "payroll_runs");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "cost_centres");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "payroll_periods");

            migrationBuilder.DropTable(
                name: "payroll_groups");

            migrationBuilder.DropTable(
                name: "legal_entities");
        }
    }
}
