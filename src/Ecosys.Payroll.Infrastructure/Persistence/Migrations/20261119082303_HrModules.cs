using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "payroll_run_id",
                table: "payroll_journals",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "narrative",
                table: "payroll_journals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "payroll_journals",
                type: "text",
                nullable: false,
                defaultValue: "Payroll");

            migrationBuilder.AddColumn<bool>(
                name: "counts_working_days_only",
                table: "leave_types",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "full_pay_days_per_year",
                table: "leave_types",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "half_pay_days_per_year",
                table: "leave_types",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "min_service_months",
                table: "leave_types",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "approver_employee_id",
                table: "leave_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "attachment_file_id",
                table: "leave_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "full_pay_days",
                table: "leave_requests",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "half_pay_days",
                table: "leave_requests",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "unpaid_days",
                table: "leave_requests",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "notice_period_days",
                table: "employees",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<Guid>(
                name: "position_id",
                table: "employees",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "probation_end_date",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    serial_number = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    purchased_on = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_assets_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "compensation_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    merit_matrix = table.Column<string>(type: "jsonb", nullable: false),
                    bonus_matrix = table.Column<string>(type: "jsonb", nullable: false),
                    increase_budget = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    bonus_budget = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    prepared_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_compensation_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_compensation_reviews_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dependants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    relationship = table.Column<string>(type: "text", nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    id_number = table.Column<string>(type: "text", nullable: true),
                    is_beneficiary = table.Column<bool>(type: "boolean", nullable: false),
                    beneficiary_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
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
                    table.PrimaryKey("pk_dependants", x => x.id);
                    table.ForeignKey(
                        name: "fk_dependants_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "employee_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    visible_to_employee = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_employee_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_employee_documents_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expense_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    gl_account_code = table.Column<string>(type: "text", nullable: true),
                    receipt_required = table.Column<bool>(type: "boolean", nullable: false),
                    daily_tax_free_cap = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    max_per_claim = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
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
                    table.PrimaryKey("pk_expense_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_expense_categories_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expense_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    route = table.Column<string>(type: "text", nullable: false),
                    manager_approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    finance_approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_expense_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_expense_claims_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "feedback_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    performance_review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: true),
                    strengths = table.Column<string>(type: "text", nullable: true),
                    improvements = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_feedback_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_feedback_requests_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    incident_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    show_cause_issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    response_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    hearing_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hearing_held = table.Column<bool>(type: "boolean", nullable: false),
                    companion = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    outcome_reason = table.Column<string>(type: "text", nullable: true),
                    appeal_deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    events = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("pk_hr_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_hr_cases_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "improvement_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manager_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    objectives = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_improvement_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_improvement_plans_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_requisitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    openings = table.Column<int>(type: "integer", nullable: false),
                    salary_min = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    salary_max = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    employment_type = table.Column<string>(type: "text", nullable: false),
                    justification = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    closing_date = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("pk_job_requisitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_job_requisitions_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medical_schemes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    policy_number = table.Column<string>(type: "text", nullable: true),
                    premium_by_cover = table.Column<string>(type: "jsonb", nullable: false),
                    employee_share_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    deduction_component_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_medical_schemes", x => x.id);
                    table.ForeignKey(
                        name: "fk_medical_schemes_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nita_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total_claimed = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    amount_approved = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    nita_reference = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_nita_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_nita_claims_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "onboarding_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    done = table.Column<bool>(type: "boolean", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done_by = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_onboarding_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_onboarding_tasks_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "positions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade = table.Column<string>(type: "text", nullable: true),
                    budgeted_headcount = table.Column<int>(type: "integer", nullable: false),
                    salary_min = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    salary_max = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    reports_to_position_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_positions", x => x.id);
                    table.ForeignKey(
                        name: "fk_positions_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "public_holidays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    observed_date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_gazetted_ad_hoc = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_public_holidays", x => x.id);
                    table.ForeignKey(
                        name: "fk_public_holidays_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    goal_weight_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    competencies = table.Column<string>(type: "jsonb", nullable: false),
                    include360 = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_review_cycles", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_cycles_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_pay_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_component_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_scheduled_pay_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_scheduled_pay_items_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "separations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    notice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: false),
                    notice_days_served = table.Column<int>(type: "integer", nullable: false),
                    pay_notice_in_lieu = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    checklist = table.Column<string>(type: "jsonb", nullable: false),
                    exit_interview_notes = table.Column<string>(type: "text", nullable: true),
                    eligible_for_rehire = table.Column<bool>(type: "boolean", nullable: false),
                    dues = table.Column<string>(type: "jsonb", nullable: true),
                    initiated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_separations", x => x.id);
                    table.ForeignKey(
                        name: "fk_separations_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_separations_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stored_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    owner_type = table.Column<string>(type: "text", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_stored_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "training_bonds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_enrolment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    months = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_training_bonds", x => x.id);
                    table.ForeignKey(
                        name: "fk_training_bonds_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_courses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: true),
                    delivery = table.Column<string>(type: "text", nullable: false),
                    duration_hours = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    cost_per_participant = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    nita_approved = table.Column<bool>(type: "boolean", nullable: false),
                    nita_course_code = table.Column<string>(type: "text", nullable: true),
                    certificate_validity_months = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("pk_training_courses", x => x.id);
                    table.ForeignKey(
                        name: "fk_training_courses_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    returned_on = table.Column<DateOnly>(type: "date", nullable: true),
                    condition_out = table.Column<string>(type: "text", nullable: true),
                    condition_in = table.Column<string>(type: "text", nullable: true),
                    chargeable_if_not_returned = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_asset_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_assignments_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asset_assignments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "compensation_review_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    compensation_review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_basic = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: true),
                    increase_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    new_basic = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    bonus_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    excluded = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_compensation_review_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_compensation_review_lines_compensation_reviews_compensation",
                        column: x => x.compensation_review_id,
                        principalTable: "compensation_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_requisition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_name = table.Column<string>(type: "text", nullable: false),
                    last_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    cv_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stage = table.Column<string>(type: "text", nullable: false),
                    interviews = table.Column<string>(type: "jsonb", nullable: false),
                    offer_basic = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    offer_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    offer_job_title = table.Column<string>(type: "text", nullable: true),
                    offer_status = table.Column<string>(type: "text", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    hired_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_to_retain = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_job_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_job_applications_job_requisitions_job_requisition_id",
                        column: x => x.job_requisition_id,
                        principalTable: "job_requisitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_job_applications_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "benefit_enrolments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    medical_scheme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cover = table.Column<string>(type: "text", nullable: false),
                    covered_dependant_ids = table.Column<string>(type: "jsonb", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    membership_number = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_benefit_enrolments", x => x.id);
                    table.ForeignKey(
                        name: "fk_benefit_enrolments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_benefit_enrolments_medical_schemes_medical_scheme_id",
                        column: x => x.medical_scheme_id,
                        principalTable: "medical_schemes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nita_claim_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nita_claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_enrolment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_title = table.Column<string>(type: "text", nullable: false),
                    nita_course_code = table.Column<string>(type: "text", nullable: true),
                    completed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
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
                    table.PrimaryKey("pk_nita_claim_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_nita_claim_lines_nita_claims_nita_claim_id",
                        column: x => x.nita_claim_id,
                        principalTable: "nita_claims",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "goals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_goal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    measure = table.Column<string>(type: "text", nullable: true),
                    target = table.Column<string>(type: "text", nullable: true),
                    weight_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    progress_percent = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
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
                    table.PrimaryKey("pk_goals", x => x.id);
                    table.ForeignKey(
                        name: "fk_goals_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_goals_review_cycles_review_cycle_id",
                        column: x => x.review_cycle_id,
                        principalTable: "review_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "performance_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    goal_ratings = table.Column<string>(type: "jsonb", nullable: false),
                    competency_ratings = table.Column<string>(type: "jsonb", nullable: false),
                    self_comments = table.Column<string>(type: "text", nullable: true),
                    manager_comments = table.Column<string>(type: "text", nullable: true),
                    manager_score = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    final_rating = table.Column<int>(type: "integer", nullable: true),
                    calibration_note = table.Column<string>(type: "text", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    employee_response = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_performance_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_performance_reviews_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_performance_reviews_review_cycles_review_cycle_id",
                        column: x => x.review_cycle_id,
                        principalTable: "review_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "training_requirements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_title = table.Column<string>(type: "text", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mandatory = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_training_requirements", x => x.id);
                    table.ForeignKey(
                        name: "fk_training_requirements_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_requirements_training_courses_training_course_id",
                        column: x => x.training_course_id,
                        principalTable: "training_courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "training_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    venue = table.Column<string>(type: "text", nullable: true),
                    trainer = table.Column<string>(type: "text", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    actual_total_cost = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    cost_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_training_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_training_sessions_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_sessions_training_courses_training_course_id",
                        column: x => x.training_course_id,
                        principalTable: "training_courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "training_enrolments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    certificate_number = table.Column<string>(type: "text", nullable: true),
                    certified_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    certificate_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_allocated = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
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
                    table.PrimaryKey("pk_training_enrolments", x => x.id);
                    table.ForeignKey(
                        name: "fk_training_enrolments_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_enrolments_legal_entities_legal_entity_id",
                        column: x => x.legal_entity_id,
                        principalTable: "legal_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_enrolments_training_sessions_training_session_id",
                        column: x => x.training_session_id,
                        principalTable: "training_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_journals_legal_entity_id_source",
                table: "payroll_journals",
                columns: new[] { "legal_entity_id", "source" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_assignments_asset_id_returned_on",
                table: "asset_assignments",
                columns: new[] { "asset_id", "returned_on" });

            migrationBuilder.CreateIndex(
                name: "ix_asset_assignments_employee_id",
                table: "asset_assignments",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_assignments_legal_entity_id",
                table: "asset_assignments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_assignments_tenant_id",
                table: "asset_assignments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_legal_entity_id",
                table: "assets",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_legal_entity_id_tag",
                table: "assets",
                columns: new[] { "legal_entity_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id",
                table: "assets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_benefit_enrolments_employee_id",
                table: "benefit_enrolments",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_benefit_enrolments_legal_entity_id",
                table: "benefit_enrolments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_benefit_enrolments_medical_scheme_id",
                table: "benefit_enrolments",
                column: "medical_scheme_id");

            migrationBuilder.CreateIndex(
                name: "ix_benefit_enrolments_tenant_id",
                table: "benefit_enrolments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_compensation_review_lines_compensation_review_id_employee_id",
                table: "compensation_review_lines",
                columns: new[] { "compensation_review_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_compensation_review_lines_tenant_id",
                table: "compensation_review_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_compensation_reviews_legal_entity_id",
                table: "compensation_reviews",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_compensation_reviews_tenant_id",
                table: "compensation_reviews",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_dependants_employee_id",
                table: "dependants",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_dependants_legal_entity_id",
                table: "dependants",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_dependants_tenant_id",
                table: "dependants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_documents_employee_id_category",
                table: "employee_documents",
                columns: new[] { "employee_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_documents_expires_on",
                table: "employee_documents",
                column: "expires_on");

            migrationBuilder.CreateIndex(
                name: "ix_employee_documents_legal_entity_id",
                table: "employee_documents",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_employee_documents_tenant_id",
                table: "employee_documents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_expense_categories_legal_entity_id",
                table: "expense_categories",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_expense_categories_legal_entity_id_code",
                table: "expense_categories",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_expense_categories_tenant_id",
                table: "expense_categories",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_expense_claims_employee_id_status",
                table: "expense_claims",
                columns: new[] { "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_expense_claims_legal_entity_id",
                table: "expense_claims",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_expense_claims_legal_entity_id_reference",
                table: "expense_claims",
                columns: new[] { "legal_entity_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_expense_claims_tenant_id",
                table: "expense_claims",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_requests_legal_entity_id",
                table: "feedback_requests",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_requests_performance_review_id",
                table: "feedback_requests",
                column: "performance_review_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_requests_provider_employee_id",
                table: "feedback_requests",
                column: "provider_employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_requests_tenant_id",
                table: "feedback_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_goals_legal_entity_id",
                table: "goals",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_goals_review_cycle_id_employee_id",
                table: "goals",
                columns: new[] { "review_cycle_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_goals_tenant_id",
                table: "goals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_hr_cases_employee_id",
                table: "hr_cases",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_hr_cases_legal_entity_id",
                table: "hr_cases",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_hr_cases_legal_entity_id_reference",
                table: "hr_cases",
                columns: new[] { "legal_entity_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hr_cases_tenant_id",
                table: "hr_cases",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_improvement_plans_employee_id",
                table: "improvement_plans",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_improvement_plans_legal_entity_id",
                table: "improvement_plans",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_improvement_plans_tenant_id",
                table: "improvement_plans",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_applications_job_requisition_id_stage",
                table: "job_applications",
                columns: new[] { "job_requisition_id", "stage" });

            migrationBuilder.CreateIndex(
                name: "ix_job_applications_legal_entity_id",
                table: "job_applications",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_applications_tenant_id",
                table: "job_applications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_requisitions_legal_entity_id",
                table: "job_requisitions",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_requisitions_legal_entity_id_reference",
                table: "job_requisitions",
                columns: new[] { "legal_entity_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_requisitions_tenant_id",
                table: "job_requisitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_medical_schemes_legal_entity_id",
                table: "medical_schemes",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_medical_schemes_tenant_id",
                table: "medical_schemes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_nita_claim_lines_nita_claim_id",
                table: "nita_claim_lines",
                column: "nita_claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_nita_claim_lines_tenant_id",
                table: "nita_claim_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_nita_claim_lines_training_enrolment_id",
                table: "nita_claim_lines",
                column: "training_enrolment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nita_claims_legal_entity_id",
                table: "nita_claims",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_nita_claims_legal_entity_id_reference",
                table: "nita_claims",
                columns: new[] { "legal_entity_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nita_claims_tenant_id",
                table: "nita_claims",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_tasks_employee_id_done",
                table: "onboarding_tasks",
                columns: new[] { "employee_id", "done" });

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_tasks_legal_entity_id",
                table: "onboarding_tasks",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_tasks_tenant_id",
                table: "onboarding_tasks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_performance_reviews_legal_entity_id",
                table: "performance_reviews",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_performance_reviews_review_cycle_id_employee_id",
                table: "performance_reviews",
                columns: new[] { "review_cycle_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_performance_reviews_reviewer_employee_id",
                table: "performance_reviews",
                column: "reviewer_employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_performance_reviews_tenant_id",
                table: "performance_reviews",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_positions_legal_entity_id",
                table: "positions",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_positions_legal_entity_id_code",
                table: "positions",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_positions_tenant_id",
                table: "positions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_public_holidays_legal_entity_id",
                table: "public_holidays",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_public_holidays_legal_entity_id_date",
                table: "public_holidays",
                columns: new[] { "legal_entity_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_public_holidays_tenant_id",
                table: "public_holidays",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_cycles_legal_entity_id",
                table: "review_cycles",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_cycles_tenant_id",
                table: "review_cycles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_pay_items_employee_id_status_due_date",
                table: "scheduled_pay_items",
                columns: new[] { "employee_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_pay_items_legal_entity_id",
                table: "scheduled_pay_items",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_pay_items_source_type_source_id",
                table: "scheduled_pay_items",
                columns: new[] { "source_type", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_pay_items_tenant_id",
                table: "scheduled_pay_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_separations_employee_id",
                table: "separations",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_separations_legal_entity_id",
                table: "separations",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_separations_tenant_id",
                table: "separations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_employee_id",
                table: "stored_files",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_owner_type_owner_id",
                table: "stored_files",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_tenant_id",
                table: "stored_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_bonds_employee_id_status",
                table: "training_bonds",
                columns: new[] { "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_training_bonds_legal_entity_id",
                table: "training_bonds",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_bonds_tenant_id",
                table: "training_bonds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_courses_legal_entity_id",
                table: "training_courses",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_courses_legal_entity_id_code",
                table: "training_courses",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_courses_tenant_id",
                table: "training_courses",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_enrolments_employee_id_expires_on",
                table: "training_enrolments",
                columns: new[] { "employee_id", "expires_on" });

            migrationBuilder.CreateIndex(
                name: "ix_training_enrolments_legal_entity_id",
                table: "training_enrolments",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_enrolments_tenant_id",
                table: "training_enrolments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_enrolments_training_session_id_employee_id",
                table: "training_enrolments",
                columns: new[] { "training_session_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_requirements_legal_entity_id",
                table: "training_requirements",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_requirements_tenant_id",
                table: "training_requirements",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_requirements_training_course_id",
                table: "training_requirements",
                column: "training_course_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_sessions_legal_entity_id",
                table: "training_sessions",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_sessions_tenant_id",
                table: "training_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_sessions_training_course_id",
                table: "training_sessions",
                column: "training_course_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_assignments");

            migrationBuilder.DropTable(
                name: "benefit_enrolments");

            migrationBuilder.DropTable(
                name: "compensation_review_lines");

            migrationBuilder.DropTable(
                name: "dependants");

            migrationBuilder.DropTable(
                name: "employee_documents");

            migrationBuilder.DropTable(
                name: "expense_categories");

            migrationBuilder.DropTable(
                name: "expense_claims");

            migrationBuilder.DropTable(
                name: "feedback_requests");

            migrationBuilder.DropTable(
                name: "goals");

            migrationBuilder.DropTable(
                name: "hr_cases");

            migrationBuilder.DropTable(
                name: "improvement_plans");

            migrationBuilder.DropTable(
                name: "job_applications");

            migrationBuilder.DropTable(
                name: "nita_claim_lines");

            migrationBuilder.DropTable(
                name: "onboarding_tasks");

            migrationBuilder.DropTable(
                name: "performance_reviews");

            migrationBuilder.DropTable(
                name: "positions");

            migrationBuilder.DropTable(
                name: "public_holidays");

            migrationBuilder.DropTable(
                name: "scheduled_pay_items");

            migrationBuilder.DropTable(
                name: "separations");

            migrationBuilder.DropTable(
                name: "stored_files");

            migrationBuilder.DropTable(
                name: "training_bonds");

            migrationBuilder.DropTable(
                name: "training_enrolments");

            migrationBuilder.DropTable(
                name: "training_requirements");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "medical_schemes");

            migrationBuilder.DropTable(
                name: "compensation_reviews");

            migrationBuilder.DropTable(
                name: "job_requisitions");

            migrationBuilder.DropTable(
                name: "nita_claims");

            migrationBuilder.DropTable(
                name: "review_cycles");

            migrationBuilder.DropTable(
                name: "training_sessions");

            migrationBuilder.DropTable(
                name: "training_courses");

            migrationBuilder.DropIndex(
                name: "ix_payroll_journals_legal_entity_id_source",
                table: "payroll_journals");

            migrationBuilder.DropColumn(
                name: "narrative",
                table: "payroll_journals");

            migrationBuilder.DropColumn(
                name: "source",
                table: "payroll_journals");

            migrationBuilder.DropColumn(
                name: "counts_working_days_only",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "full_pay_days_per_year",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "half_pay_days_per_year",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "min_service_months",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "approver_employee_id",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "attachment_file_id",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "full_pay_days",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "half_pay_days",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "unpaid_days",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "notice_period_days",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "position_id",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "probation_end_date",
                table: "employees");

            migrationBuilder.AlterColumn<Guid>(
                name: "payroll_run_id",
                table: "payroll_journals",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
