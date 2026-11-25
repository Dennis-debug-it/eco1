# Statutory parameter verification record

Every statutory parameter must be independently verified against current KRA, NSSF, SHA and NITA
requirements before deployment. This file records what has been checked, against what, and what is outstanding.
Update it whenever a rule version is added.

| Rule | Version / effective | Parameters | Checked | Source type | Sign-off |
|---|---|---|---|---|---|
| PAYE bands & personal relief | v1, from 2023-07-01 | 10/25/30/32.5/35% at 24,000 / 32,333 / 500,000 / 800,000; relief 2,400 | 2026-11 | Secondary (payroll guides citing Finance Act 2023; Finance Acts 2025 and 2026 reported no change) | **Outstanding** |
| Insurance relief | PAYE v1 | 15% of premiums, max 5,000 / month | 2026-11 | Secondary | **Outstanding** |
| Allowable deductions | v2, from 2024-12-27 | Pension 30,000; PRMF 15,000; mortgage interest 30,000 | 2026-11 | Secondary (Tax Laws (Amendment) Act 2024) | **Outstanding** |
| NSSF | v4, from 2026-02-01 | 6% + 6%; LEL 9,000; UEL 108,000 | 2026-11 | Secondary sources citing the NSSF Year-4 notice to employers (Feb 2026) | **Outstanding** |
| SHIF | v2, from 2024-12-27 | 2.75% of gross, min 300, deductible | 2026-11 | Secondary | **Outstanding** |
| Affordable Housing Levy | v2, from 2024-12-27 | 1.5% + 1.5% of gross, deductible | 2026-11 | Secondary | **Outstanding** |
| NITA | v1, from 2023-01-01 | KES 50 / employee / month, employer | Not re-checked | Training Levy Order | **Outstanding** |

## Sign-off procedure

1. Confirm each parameter against the authority's primary publication. That means the gazette notice, the Act or the official circular, not blogs. Record the reference in the rule version's `LegalReference`.
2. Recalculate at least two historical months with an independently computed payroll, for example the outgoing system or a practitioner's spreadsheet. Reconcile every employee to the cent. The engine's working text on each result line shows how every figure was derived.
3. Record the reviewer's name and date in the "Sign-off" column above.

## Rounding

All amounts round half away from zero to 2 decimal places, per component. KRA does not prescribe rounding for PAYE, so confirm this matches the customer's existing payroll during the parallel run.
