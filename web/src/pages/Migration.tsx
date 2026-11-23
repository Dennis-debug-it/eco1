import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'

type Check = { key: string; label: string; done: boolean; detail: string; required: boolean }
export type MigrationStatus = { inMigration: boolean; live: boolean; startedAt?: string; startedBy?: string; goLiveSignedAt?: string; goLiveSignedBy?: string; goLiveFrom?: string; checklist: Check[]; parallelRunsToDiscard: number }
type Kind = 'employees' | 'opening-balances' | 'year-to-date'
type Problem = { row: number; column?: string; message: string }
type Preview = { kind: string; fileName: string; rows: number; readyRows: number; canImport: boolean; problems: Problem[]; summary: Record<string, number>; notes: string[]; columns: string[] }
type Column = { name: string; description: string; required: boolean; example?: string; aliases: string[] }
type Batch = { id: string; legalEntityId: string; kind: string; fileName: string; rowCount: number; status: string; createdAt: string; appliedByName?: string; undoneAt?: string; undoneByName?: string; summary: Record<string, number> }

const kinds: { id: Kind; label: string; what: string }[] = [
  { id: 'employees', label: 'Employees', what: 'Staff records, statutory numbers, one bank or M-Pesa account each, and current pay. Fixed pay components (house allowance, SACCO, insurance…) are columns named after their code or name.' },
  { id: 'opening-balances', label: 'Opening balances', what: 'For employees already loaded: leave days left (or carried over and taken), loans and advances still being recovered, and NSSF contributed before Ecosys.' },
  { id: 'year-to-date', label: 'Pay to date', what: 'Each month of this tax year the old system paid, one row per employee per month, so P9 cards cover the whole year after a mid-year switch.' },
]
const summaryWords: Record<string, string> = {
  employees: 'employees', statutoryDetails: 'with statutory numbers', bankAccounts: 'bank accounts', salaries: 'salaries', leaveBalances: 'leave balances', loans: 'loans',
  nssfOpeningBalances: 'NSSF totals', historyMonths: 'employee-months', employeesWithHistory: 'employees with pay to date',
}
const kindLabel: Record<string, string> = { Employees: 'Employees', OpeningBalances: 'Opening balances', YearToDate: 'Pay to date' }
const order = Object.keys(summaryWords)
const summaryLine = (s: Record<string, number>) => Object.entries(s).filter(([, n]) => n > 0).sort(([a], [b]) => order.indexOf(a) - order.indexOf(b)).map(([k, n]) => `${n} ${summaryWords[k] ?? k}`).join(' · ') || 'Nothing'

export default function MigrationPage() {
  const { can } = useAuth()
  return (
    <>
      <PageHead title="Data migration" sub="Move this organisation in from its previous payroll system: load the data, reconcile parallel runs, then go live." />
      <MigrationModeCard canManage={can(P.OrganisationManage)} />
      {can(P.EmployeesWrite) && <ImportCard />}
      <HistoryCard />
      {can(P.OrganisationManage) && can(P.SalaryRead) && <ExportCard />}
    </>
  )
}

function MigrationModeCard({ canManage }: { canManage: boolean }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['migration'], queryFn: () => api.get<MigrationStatus>('/api/payroll/migration') })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['migration'] }); qc.invalidateQueries({ queryKey: ['runs'] }) }
  const start = useMutation({ mutationFn: () => api.post('/api/payroll/migration/start'), onSuccess: refresh })
  const cancel = useMutation({ mutationFn: () => api.post('/api/payroll/migration/cancel'), onSuccess: refresh })
  const [goLive, setGoLive] = useState(false)
  if (!q.data) return <div className="card">{q.error ? <ErrorBox error={q.error} /> : <Loading />}</div>
  const m = q.data
  return (
    <div className="card">
      <div className="toolbar" style={{ justifyContent: 'space-between', marginBottom: 8 }}>
        <h2 style={{ margin: 0 }}>Migration mode {m.inMigration ? <Badge status="Warning">On</Badge> : m.live ? <Badge status="Approved">Live</Badge> : <Badge status="Draft">Off</Badge>}</h2>
        {canManage && <div className="actions">
          {!m.inMigration && !m.live && <button className="btn primary" disabled={start.isPending} onClick={() => start.mutate()}>Start migration mode</button>}
          {m.inMigration && <button className="btn primary" onClick={() => setGoLive(true)}>Sign go-live…</button>}
          {m.inMigration && <button className="btn" disabled={cancel.isPending} onClick={() => { if (confirm('Leave migration mode without going live? Nothing is deleted.')) cancel.mutate() }}>Leave without going live</button>}
        </div>}
      </div>
      <ErrorBox error={start.error ?? cancel.error} />
      {m.live ? <p className="small">Live on Ecosys since {fmt.date(m.goLiveSignedAt!)}, signed by {m.goLiveSignedBy}. Ecosys pays from the period starting {fmt.date(m.goLiveFrom!)}.</p>
        : m.inMigration ? <p className="small">Since {fmt.date(m.startedAt!)} ({m.startedBy}). Payroll can be calculated and reconciled against the old system's register, but nothing can be approved, paid, finalised or filed until go-live is signed.</p>
        : <p className="small">Turn this on before loading data. It stops any payroll being approved, paid or filed from Ecosys while the old system is still paying, so a parallel run can never be paid by mistake.</p>}
      {(m.inMigration || !m.live) && <ul className="checklist" aria-label="Go-live checklist">
        {m.checklist.map(c => <li key={c.key} className={c.done ? 'done' : ''}>
          <span aria-hidden className="tick">{c.done ? '✓' : c.required ? '!' : '–'}</span>
          <span><b>{c.label}</b>{!c.required && <span className="muted"> (if needed)</span>}<br /><span className="small muted">{c.detail}</span></span>
        </li>)}
      </ul>}
      {goLive && <GoLiveDialog status={m} onClose={() => setGoLive(false)} onDone={refresh} />}
    </div>
  )
}

function GoLiveDialog({ status, onClose, onDone }: { status: MigrationStatus; onClose: () => void; onDone: () => void }) {
  const open = status.checklist.filter(c => c.required && !c.done)
  const [from, setFrom] = useState(() => { const d = isoDate(); return `${d.slice(0, 8)}01` })
  const [accept, setAccept] = useState(false)
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/migration/go-live', { firstPeriodStart: from, acceptOpenItems: accept }), onSuccess: () => { onDone(); onClose() } })
  return (
    <Modal title="Sign go-live" onClose={onClose}>
      <p className="small">From here Ecosys pays this organisation's salaries and files its returns. The old system should already have paid and closed the month before.</p>
      <Field label="First pay period Ecosys pays starts on"><input type="date" value={from} onChange={e => setFrom(e.target.value)} /></Field>
      {status.parallelRunsToDiscard > 0 && <div className="alert info">{status.parallelRunsToDiscard} parallel run(s) for earlier periods will be discarded: the old system paid those months.</div>}
      {open.length > 0 && <div className="alert warn">
        Still open: {open.map(c => c.label).join('; ')}.
        <label className="field inline" style={{ marginTop: 8, color: 'inherit' }}><input type="checkbox" checked={accept} onChange={e => setAccept(e.target.checked)} /> I've checked these and want to go live anyway</label>
      </div>}
      <ErrorBox error={m.error} />
      <div className="actions">
        <button className="btn primary" disabled={!from || (open.length > 0 && !accept) || m.isPending} onClick={() => m.mutate()}>Sign go-live</button>
        <button className="btn" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}

function ImportCard() {
  const qc = useQueryClient()
  const entities = useLegalEntities()
  const [kind, setKind] = useState<Kind>('employees')
  const [le, setLe] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [done, setDone] = useState<Preview | null>(null)
  const [showColumns, setShowColumns] = useState(false)
  useEffect(() => { if (!le && entities.data?.length) setLe(entities.data[0].id) }, [entities.data, le])
  useEffect(() => { setPreview(null); setDone(null) }, [kind, le])
  // A new file clears the last result; clearing the box after an import keeps the confirmation on screen.
  useEffect(() => { setPreview(null); if (file) setDone(null) }, [file])
  const form = () => { const f = new FormData(); f.append('legalEntityId', le); f.append('file', file!); return f }
  const check = useMutation({ mutationFn: () => api.upload<Preview>(`/api/payroll/imports/${kind}/preview`, form()), onSuccess: setPreview })
  const run = useMutation({
    mutationFn: () => api.upload<{ preview: Preview }>(`/api/payroll/imports/${kind}`, form()),
    onSuccess: r => { setDone(r.preview); setPreview(null); setFile(null); qc.invalidateQueries({ queryKey: ['imports'] }); qc.invalidateQueries({ queryKey: ['migration'] }); qc.invalidateQueries({ queryKey: ['employees'] }) },
    // The server re-checks the file; if something changed since the dry run, show its problems.
    onError: e => { if (e instanceof ApiError && e.problem.code === 'IMPORT_PROBLEMS') setPreview((e.problem as unknown as { preview: Preview }).preview) },
  })
  const meta = kinds.find(k => k.id === kind)!
  return (
    <div className="card">
      <h2>Import</h2>
      <Tabs value={kind} onChange={setKind} tabs={kinds.map(k => ({ id: k.id, label: k.label }))} />
      <p className="small">{meta.what} Every row is checked first; nothing is saved unless the whole file is clean, and an import can be undone until payroll uses it.</p>
      <div className="toolbar">
        {(entities.data?.length ?? 0) > 1 && <select value={le} onChange={e => setLe(e.target.value)} aria-label="Company">{entities.data!.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select>}
        <button className="btn sm" onClick={() => api.download(`/api/payroll/imports/${kind}/template.csv${le ? `?legalEntityId=${le}` : ''}`)}>Download template</button>
        <button className="btn sm" aria-expanded={showColumns} onClick={() => setShowColumns(v => !v)}>{showColumns ? 'Hide columns' : 'Columns it reads'}</button>
      </div>
      {showColumns && <ColumnsTable kind={kind} />}
      <div className="toolbar">
        <input type="file" accept=".csv,.xlsx,.txt" aria-label="File to import" onChange={e => setFile(e.target.files?.[0] ?? null)} key={done ? 'reset' : 'file'} />
        <button className="btn" disabled={!file || !le || check.isPending} onClick={() => check.mutate()}>{check.isPending ? 'Checking…' : 'Check file'}</button>
      </div>
      <ErrorBox error={check.error ?? (run.error instanceof ApiError && run.error.problem.code === 'IMPORT_PROBLEMS' ? null : run.error)} />
      {done && <div className="alert ok" role="status">Imported {done.fileName}: {summaryLine(done.summary)}. It's in the history below if you need to undo it.</div>}
      {preview && <PreviewResult p={preview} importing={run.isPending} onImport={() => run.mutate()} />}
    </div>
  )
}

function PreviewResult({ p, importing, onImport }: { p: Preview; importing: boolean; onImport: () => void }) {
  const fileProblems = p.problems.filter(x => x.row <= 1)
  const rowProblems = p.problems.filter(x => x.row > 1)
  return (
    <div>
      {p.canImport
        ? <div className="alert ok" role="status">All {p.rows} row(s) are ready: {summaryLine(p.summary)}.</div>
        : <div className="alert warn" role="status">{p.problems.length} problem(s) in {p.fileName}. Nothing will be imported until they're fixed. {p.readyRows > 0 && `${p.readyRows} of ${p.rows} rows are clean.`}</div>}
      {fileProblems.map((x, i) => <div key={i} className="alert danger">{x.message}</div>)}
      {p.notes.length > 0 && <ul className="small">{p.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>}
      {rowProblems.length > 0 && <div className="table-wrap"><table>
        <thead><tr><th className="num">Row</th><th>Column</th><th>Problem</th></tr></thead>
        <tbody>{rowProblems.slice(0, 500).map((x, i) => <tr key={i}><td className="num">{x.row}</td><td>{x.column ?? '—'}</td><td>{x.message}</td></tr>)}</tbody>
      </table>{rowProblems.length > 500 && <p className="small muted">Showing the first 500 of {rowProblems.length}.</p>}</div>}
      {p.canImport && <div className="actions" style={{ marginTop: 8 }}><button className="btn primary" disabled={importing} onClick={onImport}>{importing ? 'Importing…' : `Import ${p.rows} row(s)`}</button></div>}
    </div>
  )
}

function ColumnsTable({ kind }: { kind: Kind }) {
  const q = useQuery({ queryKey: ['import-columns', kind], queryFn: () => api.get<Column[]>(`/api/payroll/imports/${kind}/columns`), staleTime: Infinity })
  if (!q.data) return <Loading />
  return (
    <div className="table-wrap" style={{ marginBottom: 12 }}><table>
      <thead><tr><th>Column</th><th>What goes in it</th><th>Also read from headers like</th></tr></thead>
      <tbody>{q.data.map(c => <tr key={c.name}><td className="mono">{c.name}{c.required && ' *'}</td><td>{c.description}</td><td className="small muted">{c.aliases.slice(0, 6).join(', ')}{c.aliases.length > 6 ? '…' : ''}</td></tr>)}</tbody>
    </table><p className="small muted">* required. Headers are matched without regard to case, spaces or punctuation; columns it doesn't know are listed and left out.</p></div>
  )
}

function HistoryCard() {
  const qc = useQueryClient()
  const { can } = useAuth()
  const q = useQuery({ queryKey: ['imports'], queryFn: () => api.get<Batch[]>('/api/payroll/imports') })
  const undo = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/imports/${id}/undo`), onSuccess: () => { qc.invalidateQueries({ queryKey: ['imports'] }); qc.invalidateQueries({ queryKey: ['migration'] }) } })
  return (
    <div className="card">
      <h2>Import history</h2>
      <p className="small muted">Undo removes everything an import added and puts back what it changed. Imports are undone newest first, and only until payroll has used them.</p>
      <ErrorBox error={undo.error} />
      {!q.data ? (q.error ? <ErrorBox error={q.error} /> : <Loading />) : q.data.length === 0 ? <Empty>No imports yet.</Empty> : <div className="table-wrap"><table>
        <thead><tr><th>When</th><th>Import</th><th>File</th><th>What it did</th><th>By</th><th>Status</th><th /></tr></thead>
        <tbody>{q.data.map(b => <tr key={b.id}>
          <td>{fmt.dateTime(b.createdAt)}</td><td>{kindLabel[b.kind] ?? b.kind}</td><td>{b.fileName}</td><td className="small">{summaryLine(b.summary)}</td><td>{b.appliedByName}</td>
          <td><Badge status={b.status} />{b.undoneAt && <div className="small muted">{fmt.date(b.undoneAt)}, {b.undoneByName}</div>}</td>
          <td className="actions">{b.status === 'Applied' && can(P.EmployeesWrite) && <button className="btn sm" disabled={undo.isPending} onClick={() => { if (confirm(`Undo ${b.fileName}? Everything it added is removed.`)) undo.mutate(b.id) }}>Undo</button>}</td>
        </tr>)}</tbody>
      </table></div>}
    </div>
  )
}

function ExportCard() {
  const [payslips, setPayslips] = useState(true)
  const m = useMutation({ mutationFn: () => api.download(`/api/payroll/export/company.zip?payslips=${payslips}`) })
  return (
    <div className="card">
      <h2>Export everything</h2>
      <p className="small">Every record this organisation keeps in Ecosys as a zip: one spreadsheet-ready CSV per table, uploaded documents, and every issued payslip as a PDF. Use it to roll back to the old system after go-live, to move to another provider, or to answer a data request. It holds salaries, account numbers and KRA PINs in full, so keep it as securely as the payroll. Passwords and sign-in secrets are never included.</p>
      <label className="field inline"><input type="checkbox" checked={payslips} onChange={e => setPayslips(e.target.checked)} /> Include payslip PDFs</label>
      <ErrorBox error={m.error} />
      <div className="actions"><button className="btn" disabled={m.isPending} onClick={() => m.mutate()}>{m.isPending ? 'Preparing…' : 'Download export (.zip)'}</button></div>
    </div>
  )
}
