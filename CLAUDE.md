# Ecosys Payroll — working notes

## Commands

```bash
dotnet build
```

```bash
dotnet test
```

Integration tests need PostgreSQL on localhost (postgres/postgres) or `PAYROLL_TEST_POSTGRES`.

```bash
dotnet run --project src/Ecosys.Payroll.Api --launch-profile http
```

The API runs on :5247 and seeds the demo tenant in Development.

```bash
cd web && npm run dev
```

The web app runs on :5173 and proxies `/api` to :5247. Type-check it with `npx tsc -b`.

```bash
dotnet ef migrations add <Name> -p src/Ecosys.Payroll.Infrastructure -s src/Ecosys.Payroll.Api -o Persistence/Migrations
```

## Rules of the codebase

- **Money is `decimal`.** Never use `double` or `float`. Round through `RoundingRule`.
- **Nothing statutory is hardcoded.** Rates, thresholds, caps and reliefs live in `StatutoryRuleVersion`. A change is a new effective-dated version, never an edit of an approved one. Seed versions live in `KenyaStatutoryRules`.
- **The engine is pure.** `Ecosys.Payroll.Engine` has no DB, HTTP or clock access. `PayrollInputAssembler` builds its inputs.
- **Tenant safety:**
  - every tenant row derives from `TenantEntity`
  - query filters handle reads
  - `SaveChangesAsync` stamps inserts and rejects cross-tenant legal-entity references
  - validate other foreign IDs from request bodies against the caller's legal entity (see `EmployeeEndpoints.ValidateReferencesAsync`)
- **Authorisation** goes in the service or endpoint via `ITenantContext.Require(Permissions.X)`. Keep salary data behind `salary.read`.
- **Approved runs are immutable.** Call `PayrollRun.EnsureEditable()` before any change. Use adjustments or reversal for corrections.
- **Sensitive fields** use `FieldEncryptor` converters. Mask them (`FieldEncryptor.Mask`) in logs, audit details and read-only views.
- **Audit** every state change or sensitive change with `AuditService.Record`, in the same unit of work.
- **New settings fields** on JSON-mapped types (`PayrollSettings`) load as `default` for existing rows. Add a backfill migration and defensive defaults.
- **Notifications** go through `NotificationService` (outbox). Never put salary figures in a message.
- **HR modules pay through `ScheduledPayItem`**, not by editing runs. Use `SystemComponents.EnsureAsync` for the component codes. The run claims items at calculate, marks them processed at finalise and releases them on reversal.
- **HR access** goes through `HrAccess`, which checks self, then line manager, then permission. HR roles do not hold `salary.read`, so hide pay figures from them (see `SeparationEndpoints`, where `duesHidden` is set).
- **SaaS.** Off unless `Saas:Enabled`. A tenant without a `Subscription` is managed: never limit or bill it.
  - Check plan limits on the server with `SubscriptionService.EnsureCanAdd*Async` wherever records that count are created or re-activated.
  - Gate a new HR-suite module by adding its path to `SubscriptionGateMiddleware.HrSuitePaths`.
  - Read-only accounts must keep reads and exports working. Only `/auth` and `/billing` accept writes.
  - Cross-tenant billing work belongs in `BillingService` (tenant filter off). Write its audit rows to the customer's tenant.
  - Call `EntitlementCache.Invalidate` after changing a subscription in a request.
  - The operator console (`/api/platform`) never returns employee or payroll data.
- **Payslips** carry balances captured at issue (`Payslip.NssfContributionsToDate`, `LeaveBalances`, from `PayslipFacts`) so reprints match. Never show the employer's total cost or medical premiums to employees.
- **Dependants and beneficiaries** change only through `DependantService`: documents, then verify, then approve by a different person. Only approved records count (medical cover, death-in-service dues).
- **Offer letters** go through `OfferLetterService`. They state the salary: drafting and sending need `salary.write`, reading needs `salary.read`, and emails to the candidate carry the link but never the figures. A sent letter is frozen; changing the offer needs the letter withdrawn.
- **Uploads** go through `FileStore`, never raw. Serve files only via `/files/{id}`, which re-checks access to the owning record.

## Layout

- `src/Ecosys.Payroll.Engine`: rules, calculator, Kenya seed rules.
- `src/Ecosys.Payroll.Domain`: entities (`Hr/HrEntities.cs` for HR), state machine, roles and permissions.
- `src/Ecosys.Payroll.Infrastructure`: `PayrollDbContext` (plus `PayrollDbContext.Hr.cs`, `PayrollDbContext.Billing.cs`), `Services/Saas/*` (subscriptions, billing cycle), services (run lifecycle, assembler, journals, reports, notifications), `Services/Hr/*` (leave, separations, performance, learning, HR operations, `HrFoundations`), documents (PDF, xlsx), migrations, seeder (`DemoHrSeeder` for HR demo data).
- `src/Ecosys.Payroll.Api`: `Endpoints/*`, `Infrastructure/ApiSupport.cs` (tenant context middleware, MFA gate, tokens, error mapping).
- `tests/Ecosys.Payroll.Tests`: `Engine/` (unit) and `Integration/` (HTTP + PostgreSQL).
- `web/src`: `pages/*`, `components/ui.tsx`, `lib/{api,auth,types,lookups}.ts`.
