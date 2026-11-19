import { useEffect, useState, type ReactNode } from 'react'
import { api, ApiError, fmt } from '../lib/api'

export function PageHead({ title, sub, children }: { title: string; sub?: ReactNode; children?: ReactNode }) {
  return (
    <div className="page-head">
      <div><h1>{title}</h1>{sub && <div className="sub">{sub}</div>}</div>
      {children && <div className="actions">{children}</div>}
    </div>
  )
}

export function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return <div className="card stat"><div className="label">{label}</div><div className="value">{value}</div>{hint && <div className="hint">{hint}</div>}</div>
}

const statusTone: Record<string, string> = {
  Draft: 'neutral', Prepared: 'neutral', Calculated: 'info', UnderReview: 'warn', Approved: 'ok', PaymentProcessing: 'info', Paid: 'ok', Finalised: 'ok', Reversed: 'danger', Rejected: 'danger',
  Pending: 'warn', PendingApproval: 'warn', Active: 'ok', Open: 'info', Closed: 'neutral', Confirmed: 'ok', Failed: 'danger', FileGenerated: 'info', Sent: 'info', Cancelled: 'neutral',
  Error: 'danger', Warning: 'warn', Posted: 'ok', Settled: 'neutral', Terminated: 'danger', Resigned: 'neutral', OnLeave: 'info', Suspended: 'warn', Probation: 'info',
}
const statusLabel: Record<string, string> = { UnderReview: 'Under review', PaymentProcessing: 'Payment processing', PendingApproval: 'Pending approval', FileGenerated: 'File generated', OnLeave: 'On leave' }

export function Badge({ status, children }: { status: string; children?: ReactNode }) {
  return <span className={`badge ${statusTone[status] ?? 'neutral'}`}>{children ?? statusLabel[status] ?? status}</span>
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null
  const msg = error instanceof ApiError ? `${error.problem.title ?? 'Error'}: ${error.problem.detail ?? ''}` : error instanceof Error ? error.message : String(error)
  return <div className="alert danger" role="alert">{msg}</div>
}

export function Loading({ what = 'Loading' }: { what?: string }) {
  return <div className="empty" aria-live="polite">{what}…</div>
}

export function Empty({ children }: { children: ReactNode }) { return <div className="empty">{children}</div> }

export function Money({ v, currency }: { v: number | null | undefined; currency?: string }) {
  return <span className="mono">{currency ? fmt.money(v, currency) : fmt.num(v)}</span>
}

export function Modal({ title, onClose, children, width }: { title: string; onClose: () => void; children: ReactNode; width?: number }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="modal-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title} style={width ? { width: `min(${width}px, 100%)` } : undefined}>
        <h2>{title}</h2>
        {children}
      </div>
    </div>
  )
}

/** Confirm-with-reason dialog used for approvals, rejections and reversals. */
export function ReasonDialog({ title, label = 'Comment', required, confirmLabel = 'Confirm', danger, onConfirm, onClose, children }:
  { title: string; label?: string; required?: boolean; confirmLabel?: string; danger?: boolean; onConfirm: (text: string) => Promise<unknown>; onClose: () => void; children?: ReactNode }) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)
  const submit = async () => {
    setBusy(true); setErr(null)
    try { await onConfirm(text); onClose() } catch (e) { setErr(e) } finally { setBusy(false) }
  }
  return (
    <Modal title={title} onClose={onClose}>
      {children}
      <ErrorBox error={err} />
      <label className="field">{label}{required && ' *'}<textarea rows={3} value={text} onChange={e => setText(e.target.value)} /></label>
      <div className="actions" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button className={`btn ${danger ? 'danger' : 'primary'}`} onClick={submit} disabled={busy || (required && !text.trim())}>{busy ? 'Working…' : confirmLabel}</button>
      </div>
    </Modal>
  )
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return <label className="field">{label}{children}</label>
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string }[]; value: T; onChange: (t: T) => void }) {
  return <div className="tabs" role="tablist">{tabs.map(t => <button key={t.id} role="tab" aria-selected={t.id === value} className={t.id === value ? 'active' : ''} onClick={() => onChange(t.id)}>{t.label}</button>)}</div>
}

/** Minimal inline bar chart (SVG) — no library, readable in both themes, values labelled so colour isn't the only cue. */
export function BarChart({ data, format = fmt.int, height = 160 }: { data: { label: string; value: number }[]; format?: (v: number) => string; height?: number }) {
  if (data.length === 0) return <Empty>No data</Empty>
  const max = Math.max(...data.map(d => d.value), 1)
  const w = 600, pad = 28, bw = (w - pad * 2) / data.length
  return (
    <svg className="chart" viewBox={`0 0 ${w} ${height}`} role="img" aria-label="Bar chart">
      {data.map((d, i) => {
        const h = Math.max(2, (d.value / max) * (height - 50))
        const x = pad + i * bw + bw * 0.15
        return (
          <g key={d.label}>
            <rect x={x} y={height - 30 - h} width={bw * 0.7} height={h} rx={3} fill="var(--primary)" opacity={0.85} />
            <text x={x + bw * 0.35} y={height - 34 - h} textAnchor="middle">{format(d.value)}</text>
            <text x={x + bw * 0.35} y={height - 12} textAnchor="middle">{d.label}</text>
          </g>
        )
      })}
    </svg>
  )
}

/** Picks a file and uploads it as multipart field `file` (plus optional extra fields). */
export function UploadButton({ label = 'Upload', url, extra, onDone, accept = '.pdf,.png,.jpg,.jpeg,.docx,.xlsx,.csv' }: { label?: string; url: string; extra?: Record<string, string>; onDone?: () => void; accept?: string }) {
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)
  return (
    <span>
      <label className="btn sm">{busy ? 'Uploading…' : label}<input type="file" className="sr-only" accept={accept} disabled={busy} onChange={async e => {
        const f = e.target.files?.[0]; e.target.value = ''
        if (!f) return
        const form = new FormData(); form.append('file', f)
        for (const [k, v] of Object.entries(extra ?? {})) form.append(k, v)
        setBusy(true); setErr(null)
        try { await api.upload(url, form); onDone?.() } catch (ex) { setErr(ex) } finally { setBusy(false) }
      }} /></label>
      {!!err && <span className="small" style={{ color: 'var(--danger)', marginLeft: 6 }}>{err instanceof ApiError ? err.problem.detail ?? err.message : 'Upload failed'}</span>}
    </span>
  )
}

export function FileLink({ id, label = 'View' }: { id?: string | null; label?: string }) {
  if (!id) return null
  return <button className="btn sm" onClick={() => api.download(`/api/payroll/files/${id}`)}>{label}</button>
}

export const Stars = ({ value }: { value?: number | null }) => value == null ? <span className="muted">—</span> : <span aria-label={`${value} of 5`}>{'★'.repeat(Math.round(value))}<span className="muted">{'★'.repeat(5 - Math.round(value))}</span> <span className="small">{Number(value).toFixed(value % 1 ? 2 : 0)}</span></span>
