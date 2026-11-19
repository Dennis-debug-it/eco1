# Kenya deployment readiness

A register of what stands between this codebase and a live payroll for a Kenyan employer. Each item says what was
checked, what was done, and what is still open. Update it as items close.

Last reviewed: 19 November 2026.

## Summary

| Area | Status |
|---|---|
| Statutory calculation (PAYE, NSSF, SHIF, AHL, NITA) | Built and unit-tested; the parallel-run comparison tool is ready. **Waiting for practitioner sign-off and two reconciled parallel runs.** |
| Security (sign-in, sessions, keys, headers) | Done (see A1–A6) |
| Kenyan data quality (dates, identifiers) | Done (B1–B2) |
| Approval controls | Done (C1) |
| Data Protection Act 2019 | System features done (D1–D3). **Registration, the processing agreement and the privacy notice are organisational tasks (D4–D6).** |
| Hosting, TLS, backups, provisioning | Done (E1–E5). **Hosting location to decide (E6).** |
| Statutory filing formats | **Open (F1–F3).** Exports exist, but the exact KRA, NSSF and SHA upload templates still need confirming. |
| Bank payment formats | **Open (F4).** Each bank's specification needs confirming. |
| Email and WhatsApp delivery | **Open (G1).** Only the Ecosys Notifications provider exists, so a standalone install needs an SMTP or SMS provider. |
| Independent checks | **Open (H1–H3).** Penetration test, load test and user acceptance testing. |

## Done in this release

### A. Security
- **A1. Account lockout.**
  - Five failed sign-ins lock the account for 15 minutes. Wrong passwords and wrong authenticator codes both count.
  - The user gets an email when their account locks.
  - Each client address is limited to 10 sign-in attempts a minute.
- **A2. Passwords.**
  - At least 10 characters, checked against a block-list of common and local words (company, city, Safaricom, M-PESA and similar), following NIST SP 800-63B.
  - Passwords set by an administrator must be changed at first sign-in.
- **A3. Sessions end when access changes.** Changing a password, deactivating a user, changing their roles or entities, or "Sign out everywhere" ends existing sessions within 30 seconds.
- **A4. MFA codes can't be replayed.** An authenticator code that was already used to sign in is refused.
- **A5. Production guard.** The app won't start outside Development with:
  - demo data
  - development or placeholder keys
  - MFA turned off
  - a default database password
- **A6. Browser protections.**
  - A content security policy, refusal to be framed, no MIME sniffing, and no caching of API responses.
  - HSTS, both in the app and at the Caddy proxy.
  - Forwarded headers from the proxy are trusted, so audit IP addresses are real.

### B. Kenyan data
- **B1. Nairobi dates.** Business dates follow East Africa Time on the server and in the browser. Before this fix, using UTC dates gave yesterday's date between midnight and 03:00. PDFs and exports show times in EAT.
- **B2. Identifier checks.**
  - KRA PINs are checked on save. An employee's PIN must start with A; an employer's with P (or A for a sole proprietor).
  - National ID numbers are checked.
  - Phone numbers are normalised to +254 form.
  - NSSF and SHA numbers are checked for plausibility.
  - Duplicate employee PINs are refused.

### C. Controls
- **C1. Review and multiple approvers.** The legal entity settings `RequireReviewBeforeApproval` and `MinimumApprovals` are now enforced:
  - A reviewer checks the run before approval.
  - N different approvers must approve the same submission.
  - Preparer, reviewer and approvers must all be different people.
  - Resubmitting starts a new round.

### D. Data Protection Act 2019
- **D1. Right of access (s.26).**
  - Employees download everything held about them as JSON (My workspace → My details).
  - Administrators can export it to answer a request.
  - Every export is audited.
- **D2. Retention (s.39).**
  - Former employees become due for anonymisation 7 years after leaving. The period is configurable, with a minimum of 5: Employment Act s.74(2) and Tax Procedures Act s.23 require five.
  - Anonymising keeps payroll amounts, so the accounts and statutory returns still reconcile, and removes everything else that identifies the person.
  - Unsuccessful applicants are purged after 12 months, or 24 with their consent.
- **D3. Breach register (s.43).**
  - Shows the 72-hour deadline for notifying the Data Commissioner.
  - A breach can't be closed until the decisions on notifying the Commissioner and the affected people are both recorded.

### E. Running it
- **E1. HTTPS.** Caddy handles TLS with automatic Let's Encrypt certificates (`docker-compose.prod.yml`). The app is only reachable through the proxy.
- **E2. Backups.**
  - Nightly, AES-256 encrypted, with checksums. 14 daily and 12 monthly copies are kept.
  - A restore script is included, tested here by a round trip on the demo database.
- **E3. Readiness check.** `/health/ready` checks the database and reports failed notifications.
- **E4. Provisioning.** `provision-tenant` creates a real organisation:
  - its legal entity, payroll group and periods
  - Kenyan leave types and public holidays
  - standard pay components
  - a first administrator with a one-time password

  Tested end to end in Production mode.
- **E5. Runbook.** [operations.md](operations.md) covers install, keys, backups, restore tests, upgrades, monitoring and incidents.

## Open items

### Statutory and filing
- **S1. Statutory sign-off (PRD §18).**
  - A qualified payroll or tax practitioner must confirm every parameter in [statutory-verification.md](statutory-verification.md) against primary sources: Finance Acts, the NSSF Act 2013 Year 4 notice, the Social Health Insurance Act 2023 and regulations, the Affordable Housing Act 2024, and the NITA levy order.
  - Two parallel payrolls must then be reconciled to the shilling against the outgoing system.
  - *Owner: customer's payroll practitioner. Tool: each payroll run's **Parallel run** tab. Upload the outgoing system's register (CSV) and every employee is compared figure by figure, at a tolerance from exact to KES 1. Export the result to Excel or PDF for the sign-off pack. Each comparison is audited.*
- **F1. KRA iTax P10.** The PAYE return exports as CSV, Excel and PDF, but not yet in the column layout of KRA's current P10 Excel template. *Next step: get the current template from iTax, map the columns, and confirm one upload in iTax's validation step.*
- **F2. NSSF return.** Confirm the employer portal's upload format and map to it.
- **F3. SHA return.** Confirm the SHA employer portal's contribution upload format and map to it.

### Payments
- **F4. Bank files.** The KCB, Equity, Co-op and M-PESA B2C layouts are representative. Get each bank's bulk-payment specification from the customer's relationship manager and test a file in the bank's validation environment. Add NCBA, Stanbic, Absa and I&M as customers need them.

### Data protection (organisational)
- **D4. ODPC registration.** Register with the Office of the Data Protection Commissioner, under the Data Protection (Registration of Data Controllers and Data Processors) Regulations 2021:
  - the employer, as data controller
  - the hosting provider (Ecosys, if it hosts), as data processor

  Renew every 24 months.
- **D5. Data processing agreement.** Needed between Ecosys and each customer, if Ecosys hosts.
- **D6. Privacy notice (s.29) and DPIA (s.31).**
  - Employees must be told what is collected and why. The system doesn't yet show or record a privacy notice acknowledgement; it could be added.
  - A data protection impact assessment is advisable, given the volume of financial and identity data.

### Infrastructure
- **E6. Hosting location.** Kenyan data centre, or documented cross-border safeguards (DPA ss.48–50).
- **G1. Notifications for standalone installs.** Add an SMTP email provider, and optionally an SMS or WhatsApp provider such as Africa's Talking. These carry account-lock alerts, approval requests and payslip notices.
- **G2. Off-site backup copy.** Automate syncing the backup folder to a second location (see operations.md §4).

### Assurance
- **H1. Penetration test** by an independent tester before go-live.
- **H2. Load test.** Calculate, approve and generate payslips at the customer's headcount, and measure the timings.
- **H3. User acceptance testing.** Payroll, HR and finance users run a full month in a staging copy, including payment files, journals and statutory exports.

## Sources used for this review

| Subject | Source |
|---|---|
| Data protection | Kenya Data Protection Act 2019: s.26 (rights), s.29 (duty to notify), s.31 (DPIA), s.39 (retention), s.43 (breach notification within 72 hours), ss.48–50 (cross-border transfers) |
| Record keeping | Employment Act 2007 s.74 (records kept 5 years after employment ends); Tax Procedures Act 2015 s.23 (tax records kept 5 years) |
| Leave minimums | Employment Act 2007 s.28 (annual leave), s.29 (maternity and paternity leave), s.30 (sick leave) |
| Holidays | Public Holidays Act (Cap. 110) |
| Passwords | NIST SP 800-63B |
| One-time codes | RFC 6238 §5.2 |

The statutory rates are recorded separately, in [statutory-verification.md](statutory-verification.md).

These references were checked against secondary summaries, not the gazetted text. Legal review should confirm them.
