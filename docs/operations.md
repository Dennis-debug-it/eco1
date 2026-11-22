# Operations runbook

How to install, run, back up, restore and upgrade a single-server Ecosys Payroll deployment for a Kenyan employer.
For what is still outstanding before go-live, see [kenya-deployment-readiness.md](kenya-deployment-readiness.md).

## 1. Server

| | Up to ~2,000 employees | Larger |
|---|---|---|
| VM | 2 vCPU, 4 GB RAM, 40 GB SSD | 4 vCPU, 8 GB RAM; consider managed PostgreSQL |
| OS | Ubuntu 24.04 LTS with Docker Engine and the Compose plugin | |
| Network | Ports 80 and 443 open to users; 22 to administrators only | |
| DNS | An A record for the portal name (e.g. `payroll.example.co.ke`) pointing at the server | |

**Where to host.** Payroll data includes salaries, KRA PINs, ID numbers and bank details. The Data Protection Act 2019
(ss.48–50) only allows a transfer outside Kenya with proof of appropriate safeguards or the data subject's consent.
Hosting in a Kenyan data centre avoids that question. Whatever you choose, record the decision in the data protection
impact assessment.

## 2. First install

```bash
git clone <repository> ecosys-payroll && cd ecosys-payroll
```

```bash
cp .env.example .env
```

Generate the secrets, paste each one into `.env`, and also store each one in the organisation's password manager:

```bash
echo "POSTGRES_PASSWORD=$(openssl rand -base64 24)"; echo "PAYROLL_ENCRYPTION_KEY=$(openssl rand -base64 32)"; echo "AUTH_SIGNING_KEY=$(openssl rand -base64 48)"; echo "BACKUP_PASSPHRASE=$(openssl rand -base64 32)"
```

Set `DOMAIN` and `ACME_EMAIL`, and leave `SEED_DEMO_DATA=false`. Then start the stack:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

The application refuses to start in Production if any of these are true. The error names each problem:
- demo data is on
- a key is missing, short or a placeholder
- MFA for privileged roles is off
- the database password is a default

Create the employer's organisation and first administrator:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml run --rm payroll provision-tenant --name "Acme Kenya" --slug acme --entity "Acme Kenya Ltd" --kra-pin P051234567A --admin-email hr.lead@acme.co.ke --admin-name "Jane Wanjiku" --nssf-number 100234567 --sha-number SHA-EMP-1234 --nita-number NITA/1234
```

The command prints a one-time password. Give it to the administrator in person or by phone. At first sign-in
(organisation code `acme`) they choose their own password and enrol an authenticator app. The organisation is created with:
- a monthly payroll group and 24 periods
- standard pay components (check the GL codes against the customer's chart of accounts)
- Employment Act leave types
- gazetted public holidays for this year and next

## 3. Keys

| Secret | If lost | If leaked |
|---|---|---|
| `PAYROLL_ENCRYPTION_KEY` | Bank account numbers, KRA PINs and MFA secrets in the database **and in every backup** become unreadable. Keep two copies off the server. | Rotate it: re-encrypt the fields with a new key during a maintenance window. Treat it as a breach (see §7). |
| `BACKUP_PASSPHRASE` | Backups can't be restored. | Re-encrypt the off-site copies with a new passphrase. |
| `AUTH_SIGNING_KEY` | Everyone signs in again. | Change it; every session ends. |
| `POSTGRES_PASSWORD` | Reset it from inside the db container. | Change it in PostgreSQL and in `.env`. |

## 4. Backups

The `backup` service runs `pg_dump` at `BACKUP_AT` (01:30 Nairobi time by default) and writes to `BACKUP_PATH`.

| What | Detail |
|---|---|
| Encryption | AES-256 with `BACKUP_PASSPHRASE` |
| Integrity | A SHA-256 checksum next to each file |
| Daily copies | Kept `BACKUP_KEEP_DAYS` (14) |
| Monthly copies | The first backup of each month, kept `BACKUP_KEEP_MONTHS` (12) |

**Copy backups off the server every day.** Use a second server, or object storage in another facility. A backup on the same disk doesn't survive the disk. For example, using rclone with a remote you have configured:

```bash
rclone sync ./backups offsite:ecosys-payroll-backups --max-age 48h
```

Take an extra backup before an upgrade, or at any other time:

```bash
docker compose run --rm -e RUN_ONCE=1 backup
```

## 5. Restore (and the monthly restore test)

Test a restore every month into a scratch database. An untested backup isn't a backup.

```bash
docker compose run --rm --entrypoint restore.sh -e RESTORE_DB=ecosys_payroll_restore backup /backups/daily/<file>.dump.enc
```

Check the restored data, for example the employee count and the last finalised run:

```bash
docker compose exec db psql -U payroll -d ecosys_payroll_restore -c "select count(*) from employees; select reference, status from payroll_runs order by created_at desc limit 3;"
```

To recover production from a backup:
1. Stop the app: `docker compose stop payroll`.
2. Restore into `ecosys_payroll`, by running the restore command with `RESTORE_DB=ecosys_payroll`.
3. Start the app again: `docker compose start payroll`.

The restored data is only readable with the same `PAYROLL_ENCRYPTION_KEY`.

## 6. Upgrades

1. Read the release notes. Database migrations run automatically at start-up.
2. Take a backup: `docker compose run --rm -e RUN_ONCE=1 backup`.
3. Pull and restart:

```bash
git pull && docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

4. Check `https://<domain>/health/ready` and sign in.

Don't upgrade between payroll approval and payment. Upgrade after the run is finalised.

## 7. Monitoring and incidents

- **Uptime.** Point an uptime monitor at `https://<domain>/health/ready`. It returns 503 if the database is unreachable, and it reports failed notifications from the last 24 hours.
- **Logs.** Logs go to stdout in JSON (Caddy) and in console format (the app). Read them with `docker compose logs --since 1h payroll`. Keep them for at least 90 days if you ship them elsewhere. They contain no salary figures, and sensitive identifiers are masked.
- **Audit trail.** Use Reports & audit in the app. It covers sign-ins, failed sign-ins, lockouts, every payroll action, exports and personal-data access.
- **Personal data breach.** Record it at once under Administration → Data protection → Breach register. The Data Commissioner must be notified within 72 hours of becoming aware, if the breach puts people at risk (DPA s.43). The register shows the deadline and won't close the breach until both notification decisions are recorded.
- **Locked-out users.** An administrator can unlock them or reset their password under Settings → Users.

## 8. Retention and erasure

Under Administration → Data protection → Retention:
- **Former employees.** They become due for anonymisation `RETENTION_EMPLOYEE_YEARS` (default 7, minimum 5) after leaving. The minimum comes from the Employment Act s.74 and the Tax Procedures Act s.23.
- **Unsuccessful applicants.** Their applications are purged after 12 months, or 24 months with consent.

Review the list quarterly. Anonymising can't be undone, and anonymised data still exists in backups until those backups expire.

## 9. Running as a SaaS (many customers)

One server can host many employers. Each one is a tenant, isolated by the same row-level rules as before. Set `SAAS_ENABLED=true` to add:
- a public sign-up page ("Start a free trial" on the sign-in page) with email confirmation
- plans with prices, employee and company limits, and the HR suite as a plan feature
- a free trial, then monthly invoices in arrears with VAT, sent by email
- reminders, past-due notices and read-only suspension for unpaid invoices
- the operator console for platform administrators

With `SAAS_ENABLED=false` (the default) none of this appears. That is the licensed install for a single employer.

### Before taking sign-ups

1. Configure email delivery (`NOTIFICATIONS_PROVIDER=Ecosys`): sign-up confirmations, invoices and "Forgot password?" links go by email. The app refuses to start with sign-up on and no email delivery. To onboard customers only by hand, set `SAAS_SIGNUP_ENABLED=false` instead.
2. Fill in `SAAS_SELLER_NAME`, `SAAS_SELLER_TAX_PIN`, `SAAS_SELLER_ADDRESS`, `SAAS_SUPPORT_EMAIL` and `SAAS_PAYMENT_INSTRUCTIONS`. They are printed on every invoice. If you are VAT-registered, have your accountant confirm the invoice layout meets KRA requirements (including eTIMS) before the first invoice goes out.
3. Create your operator account:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml run --rm payroll create-platform-admin --email ops@yourcompany.co.ke --name "Operations"
```

   Sign in with organisation code `platform` and the one-time password. You choose a password and enrol an authenticator app, then land in the **Operator console**.

   This is the only way to get an operator account. "Start a free trial" on the sign-in page creates a *customer* organisation, and its administrator only ever sees that customer's portal. If you created your own account that way, it now shows up as a customer in the console; it does not give you the console.
4. Open **Plans & prices**. The first start creates three placeholder plans (Starter, Growth, Enterprise). **Set your real prices before announcing sign-up.** Prices exclude VAT. A price change applies to each subscriber's next invoice; issued invoices keep their prices. Enterprise is private, so only you can assign it.

### How billing works

| Stage | What happens |
|---|---|
| Sign-up | Organisation, first administrator and a trial (`SAAS_TRIAL_DAYS`) on the chosen plan. One account per employer KRA PIN. |
| During the trial | The customer adds billing details under Administration → Billing. A reminder goes out 3 days before the end. |
| Trial ends | With billing details: the subscription starts. Without them: the account becomes read-only until they are added. |
| Each month | When a period ends, an invoice is issued: base fee, plus each employee above the plan's allowance, plus VAT. Employees on the books (active, on leave or suspended) are counted on the day of issue. |
| Unpaid | After the due date (`SAAS_INVOICE_DUE_DAYS`) the account is past due but fully usable. After `SAAS_GRACE_DAYS` more it becomes read-only. |
| Paid | Record the payment in the console (Invoices → Record payment). The account reopens as soon as nothing is overdue. |
| Cancelled | Takes effect at the end of the paid period, with a final invoice. The account is then read-only. |

**Read-only** means people can still sign in, view everything, download payslips and reports, and export personal data, but cannot change anything except billing. Nothing is deleted. Employee data stays under the customer's control and the retention rules in §8.

The billing cycle runs every 6 hours. **Run billing now** in the console runs it immediately. It is safe to run repeatedly and catches up after downtime.

Payments are recorded by hand: M-Pesa or bank reconciliation is not automated yet. Check the Invoices tab (Unpaid) against your statements each week.

### Operator actions

From Customers → Manage:
- **Change plan** (any plan, including private ones). The customer's usage must fit the new limits.
- **Extend trial.**
- **Make read-only.** For problems outside the normal cycle. Only you can lift this.
- **Reactivate.**
- **Block sign-in.** The hard stop for abuse or a legal hold: nobody in the organisation can sign in.
- **New customer.** Same as `provision-tenant`, optionally on a plan with or without a trial.

Organisations created before SaaS mode, or without a plan, are **managed**: never billed or restricted. Put one on a plan with Customers → Manage → Start subscription.

Every operator and billing action is written to the customer's own audit log, so they can see what was done to their account. The console shows account and billing data only. It never shows employee or payroll records.

### More than one application server

Each server caches a customer's plan and status for up to 30 seconds, so a change made on one server reaches the others within that time. The billing cycle can run on several servers at once without double-billing (one invoice per subscription per period is enforced by the database). Running it on one server is enough: set `Saas__BillingJobEnabled=false` on the others.

