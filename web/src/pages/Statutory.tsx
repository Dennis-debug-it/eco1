import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import type { ResultLine, StatutoryRule } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, ReasonDialog, Tabs } from '../components/ui'

export default function StatutoryPage() {
  const { can } = useAuth()
  const [tab, setTab] = useState<'rules' | 'simulate'>('rules')
  return (
    <>
      <PageHead title="Statutory compliance" sub="Effective-dated, approval-controlled statutory rules for Kenya (KRA PAYE, NSSF, SHIF, Affordable Housing Levy, NITA)" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'rules', label: 'Rule versions' }, { id: 'simulate', label: 'Pay simulator' }]} />
      {tab === 'rules' && <Rules canManage={can(P.StatutoryManage)} canApprove={can(P.StatutoryApprove)} />}
      {tab === 'simulate' && <Simulator />}
    </>
  )
}

function Rules({ canManage, canApprove }: { canManage: boolean; canApprove: boolean }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['statutory-rules'], queryFn: () => api.get<StatutoryRule[]>('/api/payroll/statutory-rules?jurisdiction=KE') })
  const [view, setView] = useState<StatutoryRule | null>(null)
  const [decide, setDecide] = useState<{ rule: StatutoryRule; action: 'approve' | 'reject' } | null>(null)
  const [draft, setDraft] = useState<StatutoryRule | null>(null)
  const submit = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/statutory-rules/${id}/submit`), onSuccess: () => qc.invalidateQueries({ queryKey: ['statutory-rules'] }) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const groups = Object.entries((q.data ?? []).reduce<Record<string, StatutoryRule[]>>((acc, r) => { (acc[r.ruleCode] ??= []).push(r); return acc }, {}))
  const today = isoDate()
  const isCurrent = (r: StatutoryRule) => r.approvalStatus === 'Approved' && r.effectiveFrom <= today && (!r.effectiveTo || r.effectiveTo >= today)
  return (
    <>
      <div className="alert info">Platform rules are seeded from published legislation and must be independently verified against KRA, NSSF, SHA and NITA before production use. A tenant-specific approved version of a rule code supersedes the platform version. Approved versions are immutable: changes are new versions, approved by someone other than the author.</div>
      <ErrorBox error={submit.error} />
      {groups.map(([code, versions]) => (
        <div className="card" key={code}>
          <div className="page-head" style={{ marginBottom: 6 }}><h2>{versions[0].name} <span className="muted mono small">{code}</span> <span className="muted small">· {versions[0].authority}</span></h2>
            {canManage && <button className="btn sm" onClick={() => setDraft({ ...versions[versions.length - 1], id: '', tenantId: undefined, approvalStatus: 'Draft', effectiveFrom: today, effectiveTo: undefined, legalReference: '' })}>New version from latest</button>}</div>
          <table><thead><tr><th>Version</th><th>Scope</th><th>Effective</th><th>Method</th><th>Employee</th><th>Employer</th><th>Key parameters</th><th>Status</th><th>Reference</th><th /></tr></thead>
            <tbody>{versions.map(r => <tr key={r.id} style={isCurrent(r) ? { fontWeight: 600 } : undefined}>
              <td>v{r.version} {isCurrent(r) && <Badge status="Active">In force</Badge>}</td><td>{r.tenantId ? 'Tenant' : 'Platform'}</td><td>{fmt.date(r.effectiveFrom)} – {r.effectiveTo ? fmt.date(r.effectiveTo) : 'open'}</td><td>{r.method}</td>
              <td className="num">{r.employeeRate ? `${(r.employeeRate * 100).toFixed(2)}%` : '—'}</td><td className="num">{r.employerRate ? `${(r.employerRate * 100).toFixed(2)}%` : '—'}</td>
              <td style={{ whiteSpace: 'normal' }} className="small">{summary(r)}</td><td><Badge status={r.approvalStatus} /></td><td className="small muted" style={{ whiteSpace: 'normal', maxWidth: 260 }}>{r.legalReference}</td>
              <td className="actions"><button className="btn sm" onClick={() => setView(r)}>Details</button>
                {canManage && r.approvalStatus === 'Draft' && r.tenantId && <button className="btn sm" onClick={() => submit.mutate(r.id)}>Submit</button>}
                {canApprove && r.approvalStatus === 'PendingApproval' && <><button className="btn sm primary" onClick={() => setDecide({ rule: r, action: 'approve' })}>Approve</button><button className="btn sm danger" onClick={() => setDecide({ rule: r, action: 'reject' })}>Reject</button></>}</td></tr>)}</tbody></table>
        </div>))}
      {view && <Modal title={`${view.name} v${view.version}`} onClose={() => setView(null)} width={700}>
        <dl className="kv"><dt>Rule code</dt><dd className="mono">{view.ruleCode}</dd><dt>Authority</dt><dd>{view.authority}</dd><dt>Method</dt><dd>{view.method} on {view.base}</dd><dt>Effective</dt><dd>{fmt.date(view.effectiveFrom)} – {view.effectiveTo ? fmt.date(view.effectiveTo) : 'open'}</dd><dt>Status</dt><dd><Badge status={view.approvalStatus} /> {view.approvedAt && `on ${fmt.dateTime(view.approvedAt)}`}</dd><dt>Approval note</dt><dd>{view.approvalNote ?? '—'}</dd><dt>Legal reference</dt><dd>{view.legalReference ?? '—'}</dd></dl>
        {view.parameters.bands && <><h3 style={{ marginTop: 12 }}>Tax bands (monthly)</h3><table><thead><tr><th>From</th><th>To</th><th className="num">Rate</th></tr></thead><tbody>{view.parameters.bands.map((b, i) => <tr key={i}><td className="num">{fmt.num(b.lowerBound)}</td><td className="num">{b.upperBound != null ? fmt.num(b.upperBound) : '∞'}</td><td className="num">{(b.rate * 100).toFixed(1)}%</td></tr>)}</tbody></table></>}
        <h3 style={{ marginTop: 12 }}>Parameters</h3><pre className="mono small" style={{ whiteSpace: 'pre-wrap', background: 'var(--surface-2)', padding: 10, borderRadius: 6 }}>{JSON.stringify({ ...view.parameters, bands: undefined }, null, 2)}</pre>
      </Modal>}
      {decide && <ReasonDialog title={`${decide.action === 'approve' ? 'Approve' : 'Reject'} ${decide.rule.name} v${decide.rule.version}`} label="Note" required={decide.action === 'reject'} confirmLabel={decide.action === 'approve' ? 'Approve' : 'Reject'} danger={decide.action === 'reject'} onClose={() => setDecide(null)}
        onConfirm={t => api.post(`/api/payroll/statutory-rules/${decide.rule.id}/${decide.action}`, { note: t }).then(() => qc.invalidateQueries({ queryKey: ['statutory-rules'] }))}>
        <p className="small">Effective {fmt.date(decide.rule.effectiveFrom)}. Approving closes any open-ended predecessor the day before. Payrolls already processed keep the versions they used.</p></ReasonDialog>}
      {draft && <DraftModal draft={draft} onClose={() => { setDraft(null); qc.invalidateQueries({ queryKey: ['statutory-rules'] }) }} />}
    </>
  )
}

function summary(r: StatutoryRule): string {
  const p = r.parameters as Record<string, unknown>
  const parts: string[] = []
  if (r.parameters.bands?.length) parts.push(`${r.parameters.bands.length} bands, relief ${fmt.num(p.personalRelief as number)}`)
  if (p.lowerEarningsLimit != null) parts.push(`LEL ${fmt.int(p.lowerEarningsLimit as number)} / UEL ${fmt.int(p.upperEarningsLimit as number)}`)
  if (r.minimumAmount) parts.push(`min ${fmt.num(r.minimumAmount)}`)
  if (p.pensionDeductionCap != null) parts.push(`pension cap ${fmt.int(p.pensionDeductionCap as number)}, PRMF ${fmt.int(p.postRetirementMedicalCap as number)}, mortgage ${fmt.int(p.mortgageInterestCap as number)}`)
  if (p.employerFixedAmount) parts.push(`employer ${fmt.num(p.employerFixedAmount as number)} / employee / month`)
  if (p.deductibleFromTaxablePay) parts.push('deductible from taxable pay')
  return parts.join(' · ')
}

function DraftModal({ draft, onClose }: { draft: StatutoryRule; onClose: () => void }) {
  const [json, setJson] = useState(JSON.stringify({ ruleCode: draft.ruleCode, jurisdiction: draft.jurisdiction, authority: draft.authority, name: draft.name, method: draft.method, base: draft.base, effectiveFrom: draft.effectiveFrom, effectiveTo: null, employeeRate: draft.employeeRate, employerRate: draft.employerRate, minimumAmount: draft.minimumAmount ?? null, maximumAmount: draft.maximumAmount ?? null, parameters: draft.parameters, legalReference: '' }, null, 2))
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/statutory-rules', JSON.parse(json)), onSuccess: onClose })
  return (
    <Modal title={`New draft version of ${draft.ruleCode}`} onClose={onClose} width={760}>
      <ErrorBox error={m.error} />
      <p className="small muted">Edit the parameters below (rates as decimals, e.g. 0.06). The draft is created for your tenant, must be submitted, and then approved by a different user before it takes effect.</p>
      <textarea className="mono" style={{ width: '100%', minHeight: 360, fontSize: 12, padding: 10, border: '1px solid var(--border)', borderRadius: 6, background: 'var(--surface)', color: 'var(--text)' }} value={json} onChange={e => setJson(e.target.value)} aria-label="Rule definition JSON" />
      <div className="actions" style={{ justifyContent: 'flex-end', marginTop: 10 }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={m.isPending} onClick={() => m.mutate()}>Create draft</button></div>
    </Modal>
  )
}

function Simulator() {
  const [basic, setBasic] = useState(100000); const [allow, setAllow] = useState(0); const [pension, setPension] = useState(0); const [insurance, setInsurance] = useState(0); const [asOf, setAsOf] = useState(isoDate())
  const m = useMutation({ mutationFn: () => api.post<{ grossPay: number; taxablePay: number; grossTax: number; totalReliefs: number; paye: number; netPay: number; totalEmployerCost: number; lines: ResultLine[] }>('/api/payroll/statutory-rules/simulate', {
    jurisdiction: 'KE', asOf, basicSalary: basic, otherEarnings: allow ? [{ code: 'ALLOW', name: 'Allowances', amount: allow, taxable: true, pensionable: false }] : [],
    deductions: [...(pension ? [{ code: 'PENSION', name: 'Pension', amount: pension, treatment: 'RegisteredPension' }] : []), ...(insurance ? [{ code: 'INS', name: 'Insurance premium', amount: insurance, treatment: 'QualifyingInsurancePremium' }] : [])],
  }) })
  const d = m.data
  return (
    <div className="grid c2">
      <div className="card"><h2>Inputs</h2>
        <Field label="Basic salary (pensionable)"><input type="number" value={basic} onChange={e => setBasic(Number(e.target.value))} /></Field>
        <Field label="Taxable allowances"><input type="number" value={allow} onChange={e => setAllow(Number(e.target.value))} /></Field>
        <Field label="Registered pension contribution"><input type="number" value={pension} onChange={e => setPension(Number(e.target.value))} /></Field>
        <Field label="Qualifying insurance premium"><input type="number" value={insurance} onChange={e => setInsurance(Number(e.target.value))} /></Field>
        <Field label="Rules as of"><input type="date" value={asOf} onChange={e => setAsOf(e.target.value)} /></Field>
        <button className="btn primary" onClick={() => m.mutate()} disabled={m.isPending}>Calculate</button>
        <p className="muted small" style={{ marginTop: 10 }}>Uses the approved rules effective on the chosen date — handy for parallel-run checks and "what if" questions.</p></div>
      <div className="card"><h2>Result</h2><ErrorBox error={m.error} />
        {!d ? <Empty>Enter figures and calculate.</Empty> : <>
          <table><tbody>{d.lines.map((l, i) => <tr key={i}><td style={{ whiteSpace: 'normal' }}><span className="muted small">{l.type}</span> {l.name}{l.working && <div className="muted small">{l.working}</div>}</td><td className="num">{fmt.num(l.amount)}</td></tr>)}</tbody>
            <tfoot><tr><td>Gross</td><td className="num">{fmt.num(d.grossPay)}</td></tr><tr><td>Taxable pay</td><td className="num">{fmt.num(d.taxablePay)}</td></tr><tr><td>PAYE</td><td className="num">{fmt.num(d.paye)}</td></tr><tr><td>Net pay</td><td className="num">{fmt.num(d.netPay)}</td></tr><tr><td>Employer cost</td><td className="num">{fmt.num(d.totalEmployerCost)}</td></tr></tfoot></table></>}</div>
    </div>
  )
}
