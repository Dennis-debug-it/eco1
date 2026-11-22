# Ecosys Payroll

Cloud payroll for Kenyan organisations — part of the Ecosys platform but independently deployable and licensable.
It automates gross-to-net calculation, statutory deductions (PAYE, NSSF, SHIF, Affordable Housing Levy, NITA),
maker-checker approval, bank payment files, payslips, accounting journals and statutory reporting, with an
employee self-service portal and HR modules (leave, performance, learning, exits and the wider employee lifecycle)
that feed payroll directly.

> **Status:** Phase 1 (core payroll MVP), most of Phase 2 (leave, self-service, attendance, overtime, loans,
> notifications, expanded reporting) and the HR modules described under [HR modules](#hr-modules). **Not production-ready** until the statutory parameters are signed off by a
> qualified payroll/tax practitioner and a parallel run has been reconciled (PRD §18). See
> [docs/statutory-verification.md](docs/statutory-verification.md).

---

## Contents

- [Architecture](#architecture)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Testing](#testing)
- [Statutory rules](#statutory-rules)
- [HR modules](#hr-modules)
- [SaaS mode](#saas-mode)
- [Security model](#security-model)
- [API overview](#api-overview)
- [Known limitations](#known-limitations)

## Architecture

```text
src/
  Ecosys.Payroll.Engine          Pure calculation engine: statutory rule model, effective-dated rule sets,
                                 Kenya gross-to-net calculator. No database, no UI, fully unit-tested.
  Ecosys.Payroll.Domain          Entities and the payroll-run state machine, roles and permissions.
  Ecosys.Payroll.Infrastructure  EF Core / PostgreSQL, tenant isolation, field encryption, payroll services,
                                 payment files, journals, PDF/Excel export, notifications, migrations.
  Ecosys.Payroll.Api             ASP.NET Core minimal-API REST endpoints, JWT auth, MFA enforcement;
                                 also serves the built React app.
tests/Ecosys.Payroll.Tests       Engine unit tests + HTTP integration tests against a throwaway PostgreSQL DB.
web/                             React 19 + TypeScript + Vite admin console and employee portal.
```

Key decisions (PRD §19):

| Concern | Approach |
|---|---|
| Money | `decimal` end to end, `numeric(18,4)` in PostgreSQL, per-rule rounding |
| Statutory rules | Data, not code: effective-dated versions with approval workflow; every result line records the rule version that produced it, so historical payrolls reproduce exactly |
| Pay frequency | Rules are monthly; weekly / semi-monthly periods scale every threshold (bands, reliefs, caps, LEL/UEL) |
| Tenancy | Shared database, `tenant_id` on every row, EF global query filters, insert stamping, and a guard against cross-tenant foreign keys |
| Workflow | Draft → Prepared → Calculated → Under review → Approved → Payment processing → Paid → Finalised (→ Reversed). Approved runs are immutable; corrections go through adjustments or reversal |
| Controls | Maker-checker (preparer cannot approve), duplicate-run prevention, payment file reconciled to approved net, balanced journal enforced |
| Sensitive data | AES-256-GCM on bank account numbers, KRA PINs, national IDs, MFA secrets and notification recipients; masked for readers without write access |
| Auth | JWT. `Local` mode issues tokens itself (dev/standalone); `Ecosys` mode validates tokens from the shared Ecosys identity provider |
| Notifications | Transactional outbox + background dispatcher → Ecosys Notifications (email, WhatsApp). Messages never contain salary figures |

## Quick start

### Docker (single host)

```bash
cp .env.example .env
```

Set `POSTGRES_PASSWORD`, `PAYROLL_ENCRYPTION_KEY` (`openssl rand -base64 32`) and `AUTH_SIGNING_KEY` (`openssl rand -base64 48`) in `.env`. To try it with sample data on a demo server, also set `SEED_DEMO_DATA=true` and `ALLOW_DEMO_DATA=true`. Demo users with payroll roles will be asked to set up an authenticator app.

```bash
docker compose up -d --build
```

Open http://localhost:8080.

For a production install (HTTPS, backups, creating the customer's organisation with `provision-tenant`), follow [docs/operations.md](docs/operations.md).

### Local development

Prerequisites: .NET 10 SDK, Node 22, PostgreSQL 16 (`postgres`/`postgres` on localhost, or change
`ConnectionStrings:Payroll`).

```bash
createdb -h localhost -U postgres ecosys_payroll
```

```bash
dotnet run --project src/Ecosys.Payroll.Api --launch-profile http
```

```bash
cd web && npm install && npm run dev
```

The API migrates the database and, in Development, seeds a demo tenant. The web app is at http://localhost:5173.

**Demo sign-in** (organisation `demo`, password `Demo!2345`):

| Email | Role |
|---|---|
| `admin@demo.ecosys` | Tenant administrator |
| `payroll@demo.ecosys` | Payroll administrator (prepares runs) |
| `reviewer@demo.ecosys` | Payroll reviewer |
| `approver@demo.ecosys` | Payroll approver |
| `finance@demo.ecosys` | Finance officer (payments, journals) |
| `hr@demo.ecosys` | HR administrator (no salary visibility) |
| `auditor@demo.ecosys` | Auditor (read-only) |
| `brian.mwangi@demo.ecosys` | Employee (self-service) |

MFA is relaxed in Development; in any other environment privileged roles must enrol an authenticator app.

## Configuration

Settings use standard ASP.NET Core configuration (appsettings, environment variables with `__` separators).

| Key | Purpose |
|---|---|
| `ConnectionStrings:Payroll` | PostgreSQL connection string |
| `Payroll:Encryption:Key` | **Required.** Base64 32-byte AES key for field encryption. Store in a secrets manager; losing it makes encrypted fields unreadable |
| `Payroll:MigrateOnStartup` | Apply EF migrations at startup (default `true`) |
| `Careers:ApplicationsPerHour` | Applications accepted per network address per hour on the public careers page (default `10`) |
| `Payroll:SeedDemoData` | Seed the `demo` tenant (default `false`; `true` in Development) |
| `Payroll:PortalUrl` | Link used in notification messages |
| `Auth:Mode` | `Local` or `Ecosys` |
| `Auth:SigningKey` / `Issuer` / `Audience` | HS256 settings for `Local` mode |
| `Auth:Authority` / `Audience` | OIDC settings for `Ecosys` mode |
| `Auth:RequireMfaForPrivilegedRoles` | Enforce TOTP for admin, payroll admin, approver and finance roles (default `true`) |
| `Notifications:Provider` | `Log` (default) or `Ecosys` |
| `Notifications:Ecosys:BaseUrl` / `ApiKey` | Ecosys Notifications endpoint and key |
| `Cors:Origins` | Allowed browser origins when the SPA is hosted separately |
| `Saas:Enabled` | Run as a multi-customer service: sign-up, plans, billing, operator console (default `false`; `true` in Development) |
| `Saas:SignupEnabled` / `RequireEmailVerification` | Public sign-up, and email confirmation before first sign-in (both default `true`) |
| `Saas:TrialDays` / `InvoiceDueDays` / `GraceDays` | Trial length, invoice terms, and days overdue before an account becomes read-only (14 / 14 / 7) |
| `Saas:VatRate` | VAT on subscription invoices (default `0.16`) |
| `Saas:SellerName` / `SellerTaxPin` / `SellerAddress` / `SupportEmail` / `PaymentInstructions` | Printed on invoices and the billing page |

Tokens from the Ecosys identity provider must carry `sub`, `tenant_id`, `role` (one claim per role), and optionally
`employee_id`, `legal_entity_ids` and `amr=mfa`.

## Testing

```bash
dotnet test
```

- **Engine tests**: hand-worked Kenyan examples with the arithmetic in the comments, so a payroll practitioner can check them independently: rule-version resolution, NSSF year 4, pay frequencies, leave accrual, and RFC 6238 TOTP vectors.
- **Integration tests**: boot the real API against a throwaway PostgreSQL database (created and dropped per run). They cover the PRD §18 acceptance criteria:
  - tenant isolation, including cross-tenant references
  - salary visibility
  - encryption at rest
  - the full lifecycle with reconciliation, journal, payslips, audit and notifications
  - maker-checker
  - rejection and reversal
  - attendance import
  - rule approval
  - exports
  - MFA
  - upgrading existing databases

  Point them at another server with `PAYROLL_TEST_POSTGRES="Host=...;Username=...;Password=..."`.

CI (`.github/workflows/ci.yml`) runs both suites against a PostgreSQL service. It also checks that migrations match the model, builds the web app and builds the Docker image.

## Statutory rules

Seeded platform rules (Kenya) — **verify before production use**:

| Rule | Current version | Parameters |
|---|---|---|
| PAYE | from 1 Jul 2023 | Monthly bands 10% ≤24,000 · 25% ≤32,333 · 30% ≤500,000 · 32.5% ≤800,000 · 35% above; personal relief 2,400; insurance relief 15% capped 5,000 |
| Allowable deductions | from 27 Dec 2024 | Pension (incl. NSSF) cap 30,000; post-retirement medical 15,000; mortgage interest 30,000 |
| NSSF | from 1 Feb 2026 (year 4) | 6% + 6%; Tier I to LEL 9,000, Tier II to UEL 108,000 (max 6,480 each) |
| SHIF | from 27 Dec 2024 | 2.75% of gross, min 300, no cap; deductible from taxable pay |
| Affordable Housing Levy | from 27 Dec 2024 | 1.5% employee + 1.5% employer on gross; deductible from taxable pay |
| NITA | from 1 Jan 2023 | KES 50 per employee per month, employer-paid |

Earlier versions (NSSF years 2–3, pre-December-2024 SHIF/AHL treatment, the KES 20,000 pension cap) remain for historical recalculation.

To change a rule, a user with `statutory.manage` creates a new draft version and submits it. A different user with `statutory.approve` then approves it. Approval closes the open-ended predecessor the day before the new version starts. Tenant-specific versions override platform versions per rule code. When new platform versions ship, startup syncs the predecessors' end dates.

## HR modules

Each module either feeds payroll or reads from it. Payroll effects go through **scheduled pay items**: one-off
amounts that the next regular run picks up at calculation, marks processed at finalisation and releases on reversal.

| Module | What it does | Payroll link |
|---|---|---|
| **Leave** | Requests route to the line manager (or anyone with `leave.approve`). Working days exclude weekends and gazetted holidays, and the Sunday-substitution rule is applied (Public Holidays Act). Sick leave splits into full-pay and half-pay tiers (Employment Act s.30). Evidence uploads, a team calendar with clash warnings and a leave-liability report are included. | Half-pay and unpaid days reduce pay in every month they fall in. The liability provision posts as a journal (Dr 6160 / Cr 2195). |
| **Exits & final dues** | HR starts the exit and works the checklist, including asset returns. Payroll calculates dues: notice in lieu (s.35/36), untaken leave, severance on redundancy (s.40), and bond, loan and asset recoveries. A different approver signs off. Statement and certificate of service (s.51) are PDFs. | Approved dues become scheduled items in the final regular run. Leavers are paid pro-rata to their termination date. |
| **Performance** | Cycles move from goal setting to self review, manager review, calibration and acknowledgement. Goal weights must total 100. Also covers 360° feedback requests, improvement plans and a rating distribution. | A compensation review applies a merit/bonus matrix with budget checks and maker-checker. Approval creates salary assignments and BONUS items. |
| **Learning** | Course catalogue, mandatory requirements by job title or department, sessions with capacity, and manager or self nominations. Certificates expire, and a compliance matrix shows gaps. Training bonds are recovered pro rata. | Session cost posts as a journal (6180/2196). NITA claims are built from completed sessions without double-claiming. Bond balances flow into final dues. |
| **Recruitment & onboarding** | Requisitions, applicant pipeline, interviews and offers. Approved jobs can be posted on a public careers page (`/careers/{company}`) where candidates apply with a CV, no account needed; applications land in the pipeline at Applied and the candidate gets an acknowledgement. Offer letters are drafted from a per-company template, sent as a PDF, and the candidate can accept or decline online through a private link. Hiring creates the employee, salary, statutory record and an onboarding checklist, and files the accepted letter in the employee's documents. | New hires are payable from their hire date. The careers page never shows the salary range or justification. |
| **People records** | Employee documents with expiry dates and visibility levels, positions with grades and reporting lines, dependants and beneficiaries, medical schemes and enrolments, and an asset register. Dependants and beneficiaries need supporting documents, HR verification and a second person's approval before they count. | The employee share of medical premiums is deducted. Unreturned assets are recovered at exit. |
| **Cases** | Disciplinary cases and grievances, with a timeline. A s.41 hearing must be recorded before dismissal, final warning or suspension. Every case view is audited. | None. |
| **Expenses** | Claims with receipts are approved first by the line manager, then by finance (a different person). Per diem above the KES 10,000/day tax-free amount is taxed. | Reimbursements are paid in the next payroll (REIMB, REIMB_TAXABLE) or marked paid directly. |
| **HR analytics** | Headcount trend, turnover, absenteeism, tenure, department, gender, training hours, rating distribution and open items. | Shows employer cost per head to users with `salary.read`. |

**Contracts of service.** On an employee's record, **Contracts & letters** drafts a contract from the company template for their type (permanent, fixed-term, casual or internship), filled from their record and current pay. Gaps such as a missing address are listed before issue. HR issues it, prints it for signature and uploads the signed scan, which is filed in the employee's documents. **Variation letters** (s.10(5)) list each change to job, place of work, reporting line, pay, allowances, notice or end date since the last contract or letter. Templates are under People records → Contract templates. Contracts state pay, so they need `salary.read` plus documents or salary-write access.

**Letterheads.** Each company can set a letterhead under Organisation → Legal entities → Letterhead. Either a logo (PNG or JPG, under 1 MB) with a brand colour, tagline, website, P.O. Box and footer, or its own wide header artwork. Letters (offer, certificate of service, final dues) and payslips print it. Images are embedded by the built-in PDF writer, with transparency supported. Documents already sent keep the letterhead they were sent with.

**Organogram.** People → Organogram draws the organisation top-down from employees' line managers, one row per level of seniority. Each card shows the person's title, department (colour-coded) and grade. With the HR suite there is also a positions view with vacant seats. It supports search, collapsing teams, zoom and print. It flags people whose manager has left and reporting loops. It is part of every plan and needs `employees.read`.

**Access.** `HrAccess` decides access in this order: the employee themselves, their line manager (`Employee.ReportingManagerId`), then an HR permission (`hr.performance`, `hr.learning`, `hr.recruitment`, `hr.cases`, `hr.documents`, `hr.assets`, `hr.benefits`, `hr.separations`, `expenses.finance`). HR roles do not get `salary.read`. They see an exit's checklist but not its final dues.

**Reminders.** `HrReminderJob` sends outbox notifications 30 and 7 days before these expire:
- certificates
- documents
- fixed-term contracts
- probation periods

## SaaS mode

With `Saas:Enabled=true`, one installation serves many employers. It adds:
- **Sign-up.** "Start a free trial" on the sign-in page creates the organisation, its first administrator and a trial. The email address is confirmed before the first sign-in, and an employer KRA PIN can only have one account.
- **Plans.** A base fee covering some employees, a price per extra employee, employee and company limits, and the HR suite as a feature. Plans are data, edited in the operator console. The first start seeds placeholder prices.
- **Billing.** Monthly invoices in arrears with VAT, as PDF and by email. Overdue accounts become past due, then read-only after a grace period. Read-only accounts can still sign in, view and export. Payments are recorded by the operator.
- **Operator console.** For platform administrators (organisation code `platform`, created with `create-platform-admin`). Shows customers, usage, invoices and plans, and offers plan changes, trial extensions, suspension and sign-in blocking. It never shows employee or payroll records.

Organisations without a subscription (licensed installs, contract customers) are *managed*: never billed or restricted. With SaaS mode off, the product behaves exactly as a single-employer install. Operating it is covered in [docs/operations.md §9](docs/operations.md#9-running-as-a-saas-many-customers).

In Development, the sign-up confirmation link is written to the API log, and you can create an operator account with:

```bash
dotnet run --project src/Ecosys.Payroll.Api --launch-profile http -- create-platform-admin --email ops@example.com --name "Ops"
```

## Security model

- **Roles → permissions.** `salary.read` is separate from `employees.read`, so HR can manage people without seeing remuneration.
- **Legal-entity scoping.** A user can be restricted to specific legal entities.
- **Separation of duties.** The preparer or submitter of a run cannot review or approve it. When the legal entity requires review, a reviewer signs off first. With `MinimumApprovals` above one, that many different approvers must approve. Rule authors cannot approve their own rules, and adjustment requesters cannot approve their own adjustments. A small employer with a single payroll officer can choose **One person runs payroll** (Organisation → Legal entities → Edit → Approval workflow). That person can then approve their own runs, adjustments and exit final dues, and every self-approval is flagged in the audit log.
- **TOTP MFA** for privileged roles. A password-only token for such a user can only reach `/auth/*` endpoints. Codes can't be replayed.
- **Sign-in protection.** Five failures lock an account for 15 minutes, with an email to the user. Each address is rate-limited. Passwords are checked against a common-password list, and administrator-issued passwords must be changed at first sign-in.
- **Session revocation.** A password change, deactivation, role change or "Sign out everywhere" ends existing sessions within 30 seconds, through a per-user security stamp.
- **Production guard.** The app refuses to start outside Development with demo data, development keys, MFA off or a default database password.
- **Data protection (Kenya DPA 2019).** Subject-access export, retention-based anonymisation, applicant purge and a breach register with the 72-hour deadline. See Administration → Data protection.
- **Audit trail.** Logins (including failures), every workflow action, sensitive data changes, exports and payslip downloads.
- **Self-service bank changes.** Bank accounts added by employees stay unverified, and are never paid, until HR verifies them.
- **CSV exports** neutralise spreadsheet formula injection.
- **Uploads** (leave evidence, receipts, documents, certificates) are limited to 5 MB and an allow-list of types; PDFs are checked by magic bytes. Downloads go through `/files/{id}`, which re-checks access to the owning record.
- **Plan enforcement (SaaS).** Limits are checked on the server: employee and company caps when records are added or re-activated, HR-suite modules answer `402 FEATURE_NOT_IN_PLAN`, and a read-only account answers `402 SUBSCRIPTION_INACTIVE` to every change except sign-in, security and billing. Sign-up confirmation tokens are stored hashed and work once.
- **HR separation of duties.** The person who initiates an exit cannot approve its dues. Expense finance approval needs someone other than the manager approver. Compensation reviews need an approver other than the preparer.

## API overview

All endpoints are under `/api/payroll` and require a bearer token, except `/auth/login`, `/signup/*`, `/offers/{token}/*` (the candidate's private offer link) and `/health`. The operator console is under `/api/platform` (platform administrators only).

| Area | Endpoints |
|---|---|
| Auth & users | `POST /auth/login`, `GET /auth/me`, `POST /auth/mfa/{setup,enable,disable}`, `GET/POST /auth/users`, `POST /auth/users/{id}/mfa/reset` |
| Organisation | `/organisation/{legal-entities,branches,departments,cost-centres,payroll-groups}`, `/organisation/legal-entities/{id}/letterhead[/logo,/header,/preview]`, `/contract-templates/{legalEntityId}/{kind}`, `/employees/{id}/{contracts,variations}`, `/contracts/{id}[/pdf,/issue,/signed,/cancel]`, `GET /organisation/chart` (organogram), `POST /organisation/payroll-groups/{id}/periods/generate` |
| Employees | `GET/POST /employees`, `GET/PUT /employees/{id}`, `PUT /employees/{id}/statutory`, `POST /employees/{id}/{bank-accounts,salary,loans}` |
| Compensation | `/compensation/components`, `/compensation/salary-structures` |
| Statutory | `GET /statutory-rules`, `GET /statutory-rules/effective`, `POST /statutory-rules`, `POST /statutory-rules/{id}/{submit,approve,reject}`, `POST /statutory-rules/simulate` |
| Payroll runs | `GET/POST /runs`, `POST /runs/{id}/{prepare,calculate,validate,submit,approve,reject,finalise,reverse}`, `GET /runs/{id}/employees[/{employeeId}]`, `/runs/{id}/variable-inputs`, `/runs/{id}/attendance[/import]`, `POST /runs/{id}/payment-file`, `GET /runs/{id}/payments`, `POST /runs/{id}/payments/confirm`, `GET /runs/{id}/journal` |
| Adjustments | `GET/POST /adjustments`, `POST /adjustments/{id}/approve` |
| Self-service | `GET /employees/{id}/payslips`, `GET /payslips/{id}[/pdf]`, `GET /hr/me` |
| Leave | `/leave/{types,balances,requests,days,calendar,holidays,liability}`, `POST /leave/requests/{id}/{approve,reject,cancel,attachment}`, `POST /leave/holidays/generate`, `POST /leave/liability/post` |
| Exits | `GET/POST /separations`, `POST /separations/{id}/{checklist,exit-interview,calculate,approve,complete,cancel}`, `GET /separations/{id}/statement.pdf`, `GET /separations/employees/{employeeId}/certificate-of-service.pdf` |
| Performance | `/performance/{cycles,reviews,goals,feedback,plans,compensation-reviews}` with workflow actions (`advance`, `self`, `manager`, `calibrate`, `acknowledge`, `submit`, `approve`) |
| Learning | `/learning/{courses,requirements,sessions,bonds,compliance,mine}`, `/learning/nita/{claims,position}`, `POST /learning/sessions/{id}/{nominate,complete,cancel}`, `POST /learning/enrolments/{id}/{approve,withdraw,certificate,bond}` |
| Careers (public, no sign-in) | `GET /public/careers/{slug}`, `GET /public/careers/{slug}/jobs/{id}`, `POST /public/careers/{slug}/jobs/{id}/apply` (multipart; rate-limited per address) |
| HR | `/recruitment/{requisitions,applications,onboarding}`, `POST /recruitment/requisitions/{id}/posting`, `/recruitment/offer-template/{legalEntityId}`, `/recruitment/applications/{id}/offer-letter[s]`, `/recruitment/offer-letters/{id}[/pdf,/send,/withdraw]`, public `/offers/{token}[/pdf,/respond]`, `/documents`, `/cases`, `/positions[/org-chart]`, `/benefits/{schemes,enrolments}`, `/benefits/dependants[/{id}/{documents,verify,approve,reject}]`, `/expenses/{categories,claims}`, `/assets`, `GET /hr-analytics`, `GET /files/{id}` |
| Sign-up (SaaS) | `GET /signup/options`, `GET /signup/slug`, `POST /signup`, `POST /signup/verify`, `POST /signup/resend` |
| Billing (SaaS) | `GET /billing/status`, `GET /billing`, `POST /billing/{activate,plan,cancel,resume}`, `GET /billing/invoices/{id}/pdf` |
| Operator (`/api/platform`) | `GET /overview`, `GET/POST /tenants`, `GET /tenants/{id}`, `POST /tenants/{id}/{subscription,plan,extend-trial,suspend,reactivate,access}`, `GET /invoices`, `POST /invoices/{id}/{payments,void}`, `GET /invoices/{id}/pdf`, `GET/POST/PUT /plans`, `POST /billing/run` |
| Reports | `GET /dashboard`, `/reports/{statutory,payroll-register,variance,annual,audit}?format=csv\|xlsx\|pdf`, `GET /reports/notifications` |

## Known limitations

These are the things to resolve before go-live, or to plan for in later phases:

1. **Statutory sign-off.** The parameters were cross-checked against secondary sources in November 2026, not against primary KRA/NSSF/SHA gazette text. A practitioner must confirm them and run the two parallel payrolls required by PRD §18.
2. **Bank file layouts** (KCB, Equity, Co-op, M-PESA B2C) are representative. Confirm each against the bank's current bulk-upload specification before first use.
3. **KRA iTax P10, NSSF and SHA submissions.** Returns export as CSV, Excel or PDF. Direct submission, and the exact iTax P10 upload template, are Phase 3 (PRD §15: only where official APIs exist).
4. **Ecosys platform contracts are assumed.** These are:
   - the Ecosys Notifications request shape (`POST v1/messages`)
   - the identity provider's claim names

   Align both with the platform teams.
5. **Deployment.** See [docs/kenya-deployment-readiness.md](docs/kenya-deployment-readiness.md) for the open go-live items (statutory filing templates, bank specifications, ODPC registration, notifications provider, independent testing) and [docs/operations.md](docs/operations.md) for installing, backing up and upgrading.
6. **Attendance** is imported from CSV, or captured manually. Direct biometric or time-system integrations are Phase 3.
7. **Multi-country.** Only the Kenya calculator exists. The engine is jurisdiction-pluggable (`IPayrollCalculator`).
8. **HR modules.** Some parts of these modules are simplified or need checking before go-live:
   - **Statutory checks.** Have a practitioner confirm the Employment Act calculations (notice, severance, sick-pay tiers) and the per diem cap.
   - **Ad hoc holidays.** Add holidays gazetted ad hoc by hand under Leave → Public holidays.
   - **Integrations.** There are no job-board, e-signature or LMS integrations, and the medical scheme in the demo data is a sample.
   - **File storage.** Uploaded files are stored in PostgreSQL (`stored_files`). Move them to object storage if volumes grow.
9. **SaaS billing.** Payments are recorded by hand in the operator console; there is no M-Pesa (Daraja) or card integration yet. Invoices are not submitted to KRA eTIMS. The seeded plan prices are placeholders.
