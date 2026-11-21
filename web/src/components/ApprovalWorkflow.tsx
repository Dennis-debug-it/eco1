import type { LegalEntity } from '../lib/types'

type Settings = LegalEntity['settings']
type Mode = 'solo' | 'approver' | 'reviewer'

export const approvalMode = (s: Pick<Settings, 'allowPreparerToApprove' | 'requireReviewBeforeApproval'>): Mode =>
  s.allowPreparerToApprove ? 'solo' : s.requireReviewBeforeApproval ? 'reviewer' : 'approver'

/** "Prepare → review → approve (2 approvers)" for tables and summaries. */
export function approvalSummary(s: Settings) {
  const mode = approvalMode(s)
  if (mode === 'solo') return 'One person'
  const n = Math.max(1, s.minimumApprovals || 1)
  return `${mode === 'reviewer' ? 'Prepare → review → approve' : 'Prepare → approve'}${n > 1 ? ` (${n} approvers)` : ''}`
}

const options: { id: Mode; title: string; detail: string }[] = [
  { id: 'reviewer', title: 'Preparer, reviewer, then approver', detail: 'A reviewer checks the run, then a different person approves it. Three people at least. Recommended once you have the staff.' },
  { id: 'approver', title: 'Preparer, then approver', detail: 'Someone other than the preparer approves the run. Two people at least.' },
  { id: 'solo', title: 'One person runs payroll', detail: 'The same person can prepare, approve and pay. For small employers with a single payroll officer. Also lets them approve their own adjustments and exit final dues. Every self-approval is marked in the audit trail.' },
]

/** The approval steps a payroll run goes through for this legal entity. */
export function ApprovalWorkflow({ settings, onChange }: { settings: Settings; onChange: (s: Settings) => void }) {
  const mode = approvalMode(settings)
  const pick = (m: Mode) => onChange({
    ...settings,
    allowPreparerToApprove: m === 'solo',
    requireReviewBeforeApproval: m === 'reviewer',
    minimumApprovals: m === 'solo' ? 1 : settings.minimumApprovals || 1,
  })
  return (
    <fieldset style={{ border: 0, padding: 0, margin: '0 0 12px' }}>
      <legend><h3 style={{ margin: '0 0 6px' }}>Approval workflow</h3></legend>
      <p className="small muted" style={{ marginTop: 0 }}>Who has to sign off a payroll run before it is locked and paid.</p>
      {options.map(o => (
        <label key={o.id} className="small" style={{ display: 'flex', gap: 8, alignItems: 'flex-start', marginBottom: 8, cursor: 'pointer' }}>
          <input type="radio" name="approval-mode" checked={mode === o.id} onChange={() => pick(o.id)} style={{ marginTop: 3 }} />
          <span><b>{o.title}</b><br /><span className="muted">{o.detail}</span></span>
        </label>
      ))}
      {mode === 'solo' && <div className="alert warn">No second person checks the payroll before money goes out. Keep this only while one person handles payroll, and have someone outside payroll look at the register and bank file each month.</div>}
      {mode !== 'solo' && (
        <label className="field" style={{ maxWidth: 260 }}>Approvers needed
          <select value={settings.minimumApprovals || 1} onChange={e => onChange({ ...settings, minimumApprovals: Number(e.target.value) })}>
            {[1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{n === 1 ? '1 approver' : `${n} different approvers`}</option>)}
          </select>
        </label>
      )}
    </fieldset>
  )
}
