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

## Layout

- `src/Ecosys.Payroll.Engine`: rules, calculator, Kenya seed rules.
- `src/Ecosys.Payroll.Domain`: entities, state machine, roles and permissions.
- `src/Ecosys.Payroll.Infrastructure`: `PayrollDbContext`, services (run lifecycle, assembler, journals, reports, notifications, leave), documents (PDF, xlsx), migrations, seeder.
- `src/Ecosys.Payroll.Api`: `Endpoints/*`, `Infrastructure/ApiSupport.cs` (tenant context middleware, MFA gate, tokens, error mapping).
- `tests/Ecosys.Payroll.Tests`: `Engine/` (unit) and `Integration/` (HTTP + PostgreSQL).
- `web/src`: `pages/*`, `components/ui.tsx`, `lib/{api,auth,types,lookups}.ts`.
