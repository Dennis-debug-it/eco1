# Ecosys Payroll

Cloud payroll for Kenyan organisations — part of the Ecosys platform but independently deployable and licensable.
It automates gross-to-net calculation, statutory deductions (PAYE, NSSF, SHIF, Affordable Housing Levy, NITA),
maker-checker approval, bank payment files, payslips, accounting journals and statutory reporting, with an
employee self-service portal.

> **Status:** Phase 1 (core payroll MVP) plus most of Phase 2 (leave, self-service, attendance, overtime, loans,
> notifications, expanded reporting). **Not production-ready** until the statutory parameters are signed off by a
> qualified payroll/tax practitioner and a parallel run has been reconciled (PRD §18). See
> [docs/statutory-verification.md](docs/statutory-verification.md).

---

## Contents

- [Architecture](#architecture)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Testing](#testing)
- [Statutory rules](#statutory-rules)
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

Set `POSTGRES_PASSWORD`, `PAYROLL_ENCRYPTION_KEY` (`openssl rand -base64 32`) and `AUTH_SIGNING_KEY` in `.env`. To try it with sample data, also set `SEED_DEMO_DATA=true`.

```bash
docker compose up -d --build
```

Open http://localhost:8080.

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
| `Payroll:SeedDemoData` | Seed the `demo` tenant (default `false`; `true` in Development) |
| `Payroll:PortalUrl` | Link used in notification messages |
| `Auth:Mode` | `Local` or `Ecosys` |
| `Auth:SigningKey` / `Issuer` / `Audience` | HS256 settings for `Local` mode |
| `Auth:Authority` / `Audience` | OIDC settings for `Ecosys` mode |
| `Auth:RequireMfaForPrivilegedRoles` | Enforce TOTP for admin, payroll admin, approver and finance roles (default `true`) |
| `Notifications:Provider` | `Log` (default) or `Ecosys` |
| `Notifications:Ecosys:BaseUrl` / `ApiKey` | Ecosys Notifications endpoint and key |
| `Cors:Origins` | Allowed browser origins when the SPA is hosted separately |

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

## Security model

- **Roles → permissions.** `salary.read` is separate from `employees.read`, so HR can manage people without seeing remuneration.
- **Legal-entity scoping.** A user can be restricted to specific legal entities.
- **Separation of duties.** The preparer or submitter of a run cannot approve it. Rule authors cannot approve their own rules, and adjustment requesters cannot approve their own adjustments.
- **TOTP MFA** for privileged roles. A password-only token for such a user can only reach `/auth/*` endpoints.
- **Audit trail.** Logins (including failures), every workflow action, sensitive data changes, exports and payslip downloads.
- **Self-service bank changes.** Bank accounts added by employees stay unverified, and are never paid, until HR verifies them.
- **CSV exports** neutralise spreadsheet formula injection.

## API overview

All endpoints are under `/api/payroll` and require a bearer token, except `/auth/login` and `/health`.

| Area | Endpoints |
|---|---|
| Auth & users | `POST /auth/login`, `GET /auth/me`, `POST /auth/mfa/{setup,enable,disable}`, `GET/POST /auth/users`, `POST /auth/users/{id}/mfa/reset` |
| Organisation | `/organisation/{legal-entities,branches,departments,cost-centres,payroll-groups}`, `POST /organisation/payroll-groups/{id}/periods/generate` |
| Employees | `GET/POST /employees`, `GET/PUT /employees/{id}`, `PUT /employees/{id}/statutory`, `POST /employees/{id}/{bank-accounts,salary,loans}` |
| Compensation | `/compensation/components`, `/compensation/salary-structures` |
| Statutory | `GET /statutory-rules`, `GET /statutory-rules/effective`, `POST /statutory-rules`, `POST /statutory-rules/{id}/{submit,approve,reject}`, `POST /statutory-rules/simulate` |
| Payroll runs | `GET/POST /runs`, `POST /runs/{id}/{prepare,calculate,validate,submit,approve,reject,finalise,reverse}`, `GET /runs/{id}/employees[/{employeeId}]`, `/runs/{id}/variable-inputs`, `/runs/{id}/attendance[/import]`, `POST /runs/{id}/payment-file`, `GET /runs/{id}/payments`, `POST /runs/{id}/payments/confirm`, `GET /runs/{id}/journal` |
| Adjustments | `GET/POST /adjustments`, `POST /adjustments/{id}/approve` |
| Self-service | `GET /employees/{id}/payslips`, `GET /payslips/{id}[/pdf]`, `/leave/{types,balances/{employeeId},requests}` |
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
5. **Approval depth.** One approval is required. The `MinimumApprovals` and `RequireReviewBeforeApproval` legal-entity settings are stored but not yet enforced (no multi-level approval chain).
6. **Attendance** is imported from CSV, or captured manually. Direct biometric or time-system integrations are Phase 3.
7. **Multi-country.** Only the Kenya calculator exists. The engine is jurisdiction-pluggable (`IPayrollCalculator`).
