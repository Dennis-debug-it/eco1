import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useHrMe } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, FileLink, Loading, Modal, PageHead, ReasonDialog, Tabs, UploadButton } from '../components/ui'

type Category = { id: string; code: string; name: string; receiptRequired: boolean; dailyTaxFreeCap?: number; maxPerClaim?: number }
type Line = { categoryId: string; date: string; amount: number; days: number; description: string; receiptFileId?: string }
type Claim = { id: string; reference: string; employeeId: string; title: string; lines: Line[]; total: number; taxableAmount: number; status: string; route: string; rejectionReason?: string; submittedAt?: string; createdAt: string }
type ClaimRow = { claim: Claim; employee: string; employeeNumber: string }
const tone = (s: string) => s === 'Approved' || s === 'Paid' ? 'Active' : s === 'Rejected' ? 'Error' : s === 'Draft' ? 'neutral' : 'Pending'

export default function ExpensesPage() {
  const { can } = useAuth()
  const me = useHrMe()
  const tabs = [
    ...(me.data?.employeeId ? [{ id: 'mine' as const, label: 'My claims' }] : []),
    ...((me.data?.directReports ?? 0) > 0 ? [{ id: 'team' as const, label: `Team approvals${me.data?.pendingExpenses ? ` (${me.data.pendingExpenses})` : ''}` }] : []),
    ...(can(P.ExpensesFinance) ? [{ id: 'finance' as const, label: 'Finance approvals' }, { id: 'all' as const, label: 'All claims' }] : []),
  ]
  const [tab, setTab] = useState<'mine' | 'team' | 'finance' | 'all'>(tabs[0]?.id ?? 'mine')
  return (
    <>
      <PageHead title="Expense claims" sub="Line manager then finance approve. Payroll reimbursements are paid with the next payroll; per diem above the tax-free daily amount is taxed." />
      <Tabs value={tab} onChange={setTab} tabs={tabs} />
      {tab === 'mine' ? <MyClaims /> : <Approvals scope={tab} />}
    </>
  )
}

export function MyClaims() {
  const qc = useQueryClient()
  const cats = useQuery({ queryKey: ['expense-categories'], queryFn: () => api.get<Category[]>('/api/payroll/expenses/categories') })
  const q = useQuery({ queryKey: ['claims', 'mine'], queryFn: () => api.get<ClaimRow[]>('/api/payroll/expenses/claims?scope=mine') })
  const [show, setShow] = useState(false)
  const refresh = () => qc.invalidateQueries({ queryKey: ['claims'] })
  const submit = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/expenses/claims/${id}/submit`), onSuccess: refresh })
  const cat = (id: string) => cats.data?.find(c => c.id === id)
  return (
    <div className="card">
      <div className="toolbar"><h2 style={{ margin: 0 }}>My claims</h2><span style={{ flex: 1 }} /><button className="btn primary" onClick={() => setShow(true)}>New claim</button></div>
      <ErrorBox error={q.error ?? submit.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>No claims yet.</Empty> : q.data.map(({ claim: c }) => (
        <div key={c.id} className="card" style={{ marginTop: 10 }}>
          <div className="toolbar"><b>{c.title}</b><span className="mono small muted">{c.reference}</span><Badge status={tone(c.status)}>{c.status.replace(/([A-Z])/g, ' $1').trim()}</Badge><span style={{ flex: 1 }} /><b className="mono">{fmt.num(c.total)}</b>
            {(c.status === 'Draft' || c.status === 'Rejected') && <button className="btn sm primary" onClick={() => submit.mutate(c.id)}>Submit</button>}</div>
          {c.rejectionReason && <div className="alert danger">Returned: {c.rejectionReason}</div>}
          <table><tbody>{c.lines.map((l, i) => <tr key={i}><td>{fmt.date(l.date)}</td><td>{cat(l.categoryId)?.name}</td><td style={{ whiteSpace: 'normal' }}>{l.description}{l.days > 1 && ` (${l.days} days)`}</td><td className="num">{fmt.num(l.amount)}</td>
            <td>{l.receiptFileId ? <FileLink id={l.receiptFileId} label="Receipt" /> : (c.status === 'Draft' || c.status === 'Rejected') ? <><UploadButton label="Attach receipt" url={`/api/payroll/expenses/claims/${c.id}/lines/${i}/receipt`} onDone={refresh} />{cat(l.categoryId)?.receiptRequired && <Badge status="Warning">Required</Badge>}</> : ''}</td></tr>)}</tbody></table>
          {c.taxableAmount > 0 && <p className="small muted">{fmt.num(c.taxableAmount)} exceeds the tax-free per diem and will be taxed through payroll.</p>}
        </div>))}
      {show && <NewClaim categories={cats.data ?? []} onClose={() => { setShow(false); refresh() }} />}
    </div>
  )
}

function NewClaim({ categories, onClose }: { categories: Category[]; onClose: () => void }) {
  const today = new Date().toISOString().slice(0, 10)
  const [title, setTitle] = useState('')
  const [route, setRoute] = useState('Payroll')
  const [lines, setLines] = useState<Line[]>([{ categoryId: categories[0]?.id ?? '', date: today, amount: 0, days: 1, description: '' }])
  const set = (i: number, p: Partial<Line>) => setLines(lines.map((l, k) => (k === i ? { ...l, ...p } : l)))
  const m = useMutation({ mutationFn: () => api.post('/api/payroll/expenses/claims', { title, route, lines }), onSuccess: onClose })
  const total = lines.reduce((s, l) => s + (l.amount || 0), 0)
  return (
    <Modal title="New expense claim" onClose={onClose} width={820}>
      <ErrorBox error={m.error} />
      <div className="form-grid"><Field label="Purpose / trip"><input value={title} onChange={e => setTitle(e.target.value)} /></Field><Field label="Reimburse via"><select value={route} onChange={e => setRoute(e.target.value)}><option value="Payroll">Next payroll</option><option value="Direct">Direct payment by finance</option></select></Field></div>
      <table><thead><tr><th>Category</th><th>Date</th><th>Description</th><th className="num">Days</th><th className="num">Amount</th><th /></tr></thead>
        <tbody>{lines.map((l, i) => { const c = categories.find(x => x.id === l.categoryId); return <tr key={i}>
          <td><select value={l.categoryId} onChange={e => set(i, { categoryId: e.target.value })} aria-label="Category">{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>{c?.dailyTaxFreeCap && <div className="muted small">tax-free {fmt.int(c.dailyTaxFreeCap)}/day</div>}</td>
          <td><input type="date" value={l.date} onChange={e => set(i, { date: e.target.value })} aria-label="Date" /></td><td><input value={l.description} onChange={e => set(i, { description: e.target.value })} aria-label="Description" /></td>
          <td><input type="number" min={1} value={l.days} onChange={e => set(i, { days: Number(e.target.value) })} style={{ width: 60 }} aria-label="Days" /></td><td><input type="number" value={l.amount || ''} onChange={e => set(i, { amount: Number(e.target.value) })} style={{ width: 110 }} aria-label="Amount" /></td>
          <td>{lines.length > 1 && <button className="btn sm" onClick={() => setLines(lines.filter((_, k) => k !== i))}>✕</button>}</td></tr> })}</tbody>
        <tfoot><tr><td colSpan={4}>Total</td><td className="num">{fmt.num(total)}</td><td /></tr></tfoot></table>
      <button className="btn sm" style={{ marginTop: 8 }} onClick={() => setLines([...lines, { categoryId: categories[0]?.id ?? '', date: today, amount: 0, days: 1, description: '' }])}>Add line</button>
      <p className="small muted">Save the claim, attach receipts where required, then submit.</p>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!title || lines.some(l => !l.description || !l.amount) || m.isPending} onClick={() => m.mutate()}>Save claim</button></div>
    </Modal>
  )
}

function Approvals({ scope }: { scope: 'team' | 'finance' | 'all' }) {
  const qc = useQueryClient()
  const cats = useQuery({ queryKey: ['expense-categories'], queryFn: () => api.get<Category[]>('/api/payroll/expenses/categories') })
  const q = useQuery({ queryKey: ['claims', scope], queryFn: () => api.get<ClaimRow[]>(`/api/payroll/expenses/claims?scope=${scope}`) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['claims'] }); qc.invalidateQueries({ queryKey: ['hr-me'] }) }
  const approve = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/expenses/claims/${id}/approve`), onSuccess: refresh })
  const paid = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/expenses/claims/${id}/paid`), onSuccess: refresh })
  const [reject, setReject] = useState<string | null>(null)
  return (
    <div className="card">
      <ErrorBox error={q.error ?? approve.error ?? paid.error} />
      {q.isLoading ? <Loading /> : !q.data?.length ? <Empty>Nothing awaiting approval.</Empty> : <div className="table-wrap"><table><thead><tr><th>Claim</th><th>Employee</th><th>Lines</th><th className="num">Total</th><th className="num">Taxable</th><th>Route</th><th>Status</th><th /></tr></thead>
        <tbody>{q.data.map(({ claim: c, employee, employeeNumber }) => <tr key={c.id}><td>{c.title}<div className="mono small muted">{c.reference}</div></td><td>{employeeNumber} {employee}</td>
          <td className="small" style={{ whiteSpace: 'normal' }}>{c.lines.map((l, i) => <div key={i}>{cats.data?.find(x => x.id === l.categoryId)?.name}: {fmt.num(l.amount)} {l.receiptFileId && <FileLink id={l.receiptFileId} label="Receipt" />}</div>)}</td>
          <td className="num">{fmt.num(c.total)}</td><td className="num">{c.taxableAmount ? fmt.num(c.taxableAmount) : ''}</td><td>{c.route}</td><td><Badge status={tone(c.status)}>{c.status.replace(/([A-Z])/g, ' $1').trim()}</Badge></td>
          <td className="actions">{(c.status === 'Submitted' || c.status === 'ManagerApproved') && <><button className="btn sm primary" onClick={() => approve.mutate(c.id)}>Approve</button><button className="btn sm danger" onClick={() => setReject(c.id)}>Return</button></>}
            {c.status === 'Approved' && c.route === 'Direct' && scope !== 'team' && <button className="btn sm" onClick={() => paid.mutate(c.id)}>Mark paid</button>}</td></tr>)}</tbody></table></div>}
      {reject && <ReasonDialog title="Return claim" label="Reason" required danger confirmLabel="Return" onClose={() => setReject(null)} onConfirm={t => api.post(`/api/payroll/expenses/claims/${reject}/reject`, { text: t }).then(refresh)} />}
    </div>
  )
}
