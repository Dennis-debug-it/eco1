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
