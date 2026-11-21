import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, ReasonDialog } from './ui'

export type Letter = {
  id: string; version: number; reference: string; status: 'Draft' | 'Sent' | 'Accepted' | 'Declined' | 'Withdrawn' | 'Expired'; subject: string; body: string
  signatoryName?: string; signatoryTitle?: string; basic: number; startDate: string; jobTitle: string; acceptBy: string; sentAt?: string; sentBy?: string
  linkSent: boolean; respondedAt?: string; responseName?: string; recordedByHr: boolean; declineReason?: string
  preview: { subject: string; body: string; unfilled: string[] }
}

/** Splits letter text the same way the PDF does: "## " heading, "- " bullet, blank line between paragraphs. */
export function letterBlocks(body: string): { kind: 'h' | '-' | 'p'; text: string }[] {
  const out: { kind: 'h' | '-' | 'p'; text: string }[] = []
  let para: string[] = []
  const flush = () => { if (para.length) { out.push({ kind: 'p', text: para.join(' ') }); para = [] } }
  for (const raw of body.replace(/\r/g, '').split('\n')) {
    const line = raw.trim()
    if (!line) { flush(); continue }
    if (line.startsWith('## ')) { flush(); out.push({ kind: 'h', text: line.slice(3).trim() }); continue }
    if (line.startsWith('- ') || line.startsWith('* ')) { flush(); out.push({ kind: '-', text: line.slice(2).trim() }); continue }
    para.push(line)
  }
  flush()
  return out
}

export function LetterText({ subject, body }: { subject: string; body: string }) {
  const blocks = letterBlocks(body)
  const items: React.ReactNode[] = []
  for (let i = 0; i < blocks.length; i++) {
    const b = blocks[i]
    if (b.kind === '-') {
      const run: string[] = []
      while (i < blocks.length && blocks[i].kind === '-') run.push(blocks[i++].text)
      i--
      items.push(<ul key={i} style={{ margin: '0 0 10px', paddingLeft: 20 }}>{run.map((t, k) => <li key={k} style={{ marginBottom: 3 }}>{t}</li>)}</ul>)
    } else if (b.kind === 'h') items.push(<h4 key={i} style={{ margin: '14px 0 6px' }}>{b.text}</h4>)
    else items.push(<p key={i} style={{ margin: '0 0 10px' }}>{b.text}</p>)
  }
  return <div className="letter"><p style={{ fontWeight: 700, marginTop: 0 }}>{subject}</p>{items}</div>
}

const tone: Record<Letter['status'], string> = { Draft: 'neutral', Sent: 'Pending', Accepted: 'Active', Declined: 'Error', Withdrawn: 'neutral', Expired: 'Warning' }

/** Offer letter panel inside the applicant dialog. */
export function OfferLetterPanel({ applicationId, offerReady, candidate }: { applicationId: string; offerReady: boolean; candidate: string }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const canWrite = can(P.SalaryWrite)
  const q = useQuery({ queryKey: ['offer-letters', applicationId], queryFn: () => api.get<Letter[]>(`/api/payroll/recruitment/applications/${applicationId}/offer-letters`), enabled: can(P.SalaryRead) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['offer-letters', applicationId] }); qc.invalidateQueries({ queryKey: ['applications'] }) }
  const draft = useMutation({ mutationFn: (restart: boolean) => api.post<Letter>(`/api/payroll/recruitment/applications/${applicationId}/offer-letter`, { restart }), onSuccess: refresh })
  const [withdraw, setWithdraw] = useState<Letter | null>(null)
  const [sending, setSending] = useState<Letter | null>(null)
  const [link, setLink] = useState<string | null>(null)

  if (!can(P.SalaryRead)) return <p className="small muted">Offer letters state the salary, so they need salary access.</p>
  if (q.isLoading) return <Loading />
  const letters = q.data ?? []
  const latest = letters[0]
  const pdf = (l: Letter) => api.download(`/api/payroll/recruitment/offer-letters/${l.id}/pdf`)
  const canDraftNew = !latest || ['Declined', 'Withdrawn', 'Expired'].includes(latest.status)

  return (
    <div className="card" style={{ marginTop: 12 }}>
      <div className="toolbar"><h3 style={{ margin: 0 }}>Offer letter</h3>{latest && <><Badge status={tone[latest.status]}>{latest.status}</Badge><span className="mono small muted">{latest.reference}</span></>}<span style={{ flex: 1 }} />
        {canWrite && canDraftNew && <button className="btn sm primary" disabled={!offerReady || draft.isPending} title={offerReady ? '' : 'Save the salary and start date first'} onClick={() => draft.mutate(false)}>{latest ? 'Draft a new letter' : 'Draft offer letter'}</button>}
      </div>
      <ErrorBox error={q.error ?? draft.error} />
      {!latest && <p className="small muted">{offerReady ? 'Draft a letter from your company template, filled in from the offer above.' : 'Save the offer (salary, start date and title) first, then draft the letter.'}</p>}
      {link && <div className="alert ok">Sent. The candidate has been emailed this private link; you can also share it by WhatsApp or SMS. It is shown only once:<div className="toolbar" style={{ marginTop: 6 }}><input readOnly value={link} style={{ flex: 1 }} aria-label="Candidate link" onFocus={e => e.target.select()} /><button className="btn sm" onClick={() => navigator.clipboard?.writeText(link)}>Copy</button></div></div>}

      {latest?.status === 'Draft' && (canWrite ? <DraftEditor letter={latest} onChanged={refresh} onReset={() => { if (confirm('Replace this draft\'s text with the company template?')) draft.mutate(true) }} onSend={() => setSending(latest)} onDiscard={() => setWithdraw(latest)} onPdf={() => pdf(latest)} />
        : <p className="small muted">A draft is being prepared.</p>)}

      {latest && latest.status !== 'Draft' && (
        <div className="small">
          {latest.status === 'Sent' && <p>Sent {fmt.dateTime(latest.sentAt)}{latest.sentBy && ` by ${latest.sentBy}`}. {latest.linkSent ? `${candidate} was emailed a link to accept or decline` : 'No link was sent: share the PDF with the candidate'}, by <b>{fmt.date(latest.acceptBy)}</b>. If they reply on paper, set the offer status above to Accepted or Declined.</p>}
          {latest.status === 'Accepted' && <p><b>Accepted</b> by {latest.responseName} on {fmt.dateTime(latest.respondedAt)}{latest.recordedByHr ? ' (recorded by HR)' : ' (online)'}. The accepted letter will be filed in the employee's documents at hire.</p>}
          {latest.status === 'Declined' && <p><b>Declined</b> on {fmt.dateTime(latest.respondedAt)}{latest.declineReason && `: "${latest.declineReason}"`}.</p>}
          {latest.status === 'Expired' && <p>The candidate did not respond by {fmt.date(latest.acceptBy)}, so the offer lapsed.</p>}
          {latest.status === 'Withdrawn' && <p>Withdrawn{latest.declineReason && `: ${latest.declineReason}`}.</p>}
          <div className="toolbar">
            {latest.status !== 'Withdrawn' && <button className="btn sm" onClick={() => pdf(latest)}>{latest.status === 'Accepted' ? 'Download accepted letter' : 'Download letter'}</button>}
            {latest.status === 'Sent' && canWrite && <button className="btn sm danger" onClick={() => setWithdraw(latest)}>Withdraw offer</button>}
          </div>
        </div>
      )}
      {letters.length > 1 && <details className="small"><summary>Earlier versions</summary>{letters.slice(1).map(l => <div key={l.id}>{l.reference} · {l.status}{l.status !== 'Draft' && l.status !== 'Withdrawn' && <> · <a href="#" onClick={e => { e.preventDefault(); pdf(l) }}>PDF</a></>}</div>)}</details>}

      {sending && <SendDialog letter={sending} candidate={candidate} onClose={() => setSending(null)} onSent={l => { setSending(null); setLink(l); refresh() }} />}
      {withdraw && <ReasonDialog title={withdraw.status === 'Draft' ? 'Discard draft' : 'Withdraw offer'} label="Reason" required danger confirmLabel={withdraw.status === 'Draft' ? 'Discard' : 'Withdraw'} onClose={() => setWithdraw(null)}
        onConfirm={async reason => { await api.post(`/api/payroll/recruitment/offer-letters/${withdraw.id}/withdraw`, { reason }); refresh() }}>
        {withdraw.status === 'Sent' && <p className="small">The candidate's link stops working. Tell them directly as well.</p>}
      </ReasonDialog>}
    </div>
  )
}

function DraftEditor({ letter, onChanged, onReset, onSend, onDiscard, onPdf }: { letter: Letter; onChanged: () => void; onReset: () => void; onSend: () => void; onDiscard: () => void; onPdf: () => void }) {
  const [f, setF] = useState({ subject: letter.subject, body: letter.body, acceptBy: letter.acceptBy, signatoryName: letter.signatoryName ?? '', signatoryTitle: letter.signatoryTitle ?? '' })
  const [mode, setMode] = useState<'edit' | 'preview'>('preview')
  const dirty = f.subject !== letter.subject || f.body !== letter.body || f.acceptBy !== letter.acceptBy || f.signatoryName !== (letter.signatoryName ?? '') || f.signatoryTitle !== (letter.signatoryTitle ?? '')
  const save = useMutation({ mutationFn: () => api.put<Letter>(`/api/payroll/recruitment/offer-letters/${letter.id}`, { ...f, signatoryName: f.signatoryName || null, signatoryTitle: f.signatoryTitle || null }), onSuccess: () => { onChanged(); setMode('preview') } })
  return (
    <>
      <ErrorBox error={save.error} />
      <p className="small muted" style={{ marginTop: 0 }}>Terms from the offer: {fmt.money(letter.basic)} a month, {letter.jobTitle}, starting {fmt.date(letter.startDate)}. To change them, edit the offer above and open the draft again.</p>
      {letter.preview.unfilled.length > 0 && <div className="alert warn">These fields can't be filled in: {letter.preview.unfilled.join(', ')}. Remove them or use a listed field before sending.</div>}
      <div className="form-grid">
        <Field label="Respond by"><input type="date" min={isoDate()} value={f.acceptBy} onChange={e => setF({ ...f, acceptBy: e.target.value })} /></Field>
        <Field label="Signed by"><input value={f.signatoryName} onChange={e => setF({ ...f, signatoryName: e.target.value })} /></Field>
        <Field label="Signatory's title"><input value={f.signatoryTitle} onChange={e => setF({ ...f, signatoryTitle: e.target.value })} /></Field>
      </div>
      <div className="tabs" role="tablist" style={{ marginBottom: 8 }}>
        <button role="tab" aria-selected={mode === 'preview'} className={mode === 'preview' ? 'active' : ''} onClick={() => setMode('preview')}>Letter</button>
        <button role="tab" aria-selected={mode === 'edit'} className={mode === 'edit' ? 'active' : ''} onClick={() => setMode('edit')}>Edit wording</button>
      </div>
      {mode === 'preview'
        ? <div style={{ maxHeight: 360, overflow: 'auto', border: '1px solid var(--border)', borderRadius: 6, padding: '12px 16px', background: 'var(--surface-2)' }}>{dirty ? <p className="small muted">Save to see your changes filled in.</p> : <LetterText subject={letter.preview.subject} body={letter.preview.body} />}</div>
        : <>
          <Field label="Subject"><input value={f.subject} onChange={e => setF({ ...f, subject: e.target.value })} /></Field>
          <Field label="Text"><textarea rows={16} value={f.body} onChange={e => setF({ ...f, body: e.target.value })} style={{ fontFamily: 'var(--mono, monospace)', fontSize: 12.5 }} /></Field>
          <p className="small muted">Blank line = new paragraph · <span className="mono">## </span>heading · <span className="mono">- </span>bullet. Fields in braces such as <span className="mono">{'{basic_salary}'}</span> are filled in when you send.</p>
        </>}
      <div className="toolbar" style={{ marginTop: 8, flexWrap: 'wrap' }}>
        <button className="btn sm" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>Save draft</button>
        <button className="btn sm" disabled={dirty} onClick={onPdf} title={dirty ? 'Save first' : ''}>Preview PDF</button>
        <button className="btn sm" onClick={onReset}>Reset to template</button>
        <button className="btn sm" onClick={onDiscard}>Discard</button>
        <span style={{ flex: 1 }} />
        <button className="btn primary" disabled={dirty || letter.preview.unfilled.length > 0} title={dirty ? 'Save first' : ''} onClick={onSend}>Send to candidate…</button>
      </div>
    </>
  )
}

function SendDialog({ letter, candidate, onClose, onSent }: { letter: Letter; candidate: string; onClose: () => void; onSent: (link: string | null) => void }) {
  const [emailLink, setEmailLink] = useState(true)
  const send = useMutation({ mutationFn: () => api.post<{ link?: string }>(`/api/payroll/recruitment/offer-letters/${letter.id}/send`, { emailLink }), onSuccess: r => onSent(r.link ?? null) })
  return (
    <Modal title={`Send offer ${letter.reference}`} onClose={onClose}>
      <ErrorBox error={send.error} />
      <p className="small">The letter is fixed once sent and a PDF copy is kept. {candidate} has until <b>{fmt.date(letter.acceptBy)}</b> to respond.</p>
      <label className="field inline"><input type="checkbox" checked={emailLink} onChange={e => setEmailLink(e.target.checked)} /> Email {candidate} a private link to read the letter and accept or decline online</label>
      <p className="small muted">The email says an offer is waiting but does not include the salary; that is only in the letter. Without a link, download the PDF and send it yourself, then record their answer in the offer status.</p>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={send.isPending} onClick={() => send.mutate()}>{send.isPending ? 'Sending…' : 'Send'}</button></div>
    </Modal>
  )
}

type TemplateResponse = { template: { id: string; subject: string; body: string; signatoryName?: string; signatoryTitle?: string; probationMonths: number; acceptWithinDays: number }; placeholders: { key: string; meaning: string }[]; standardSubject: string; standardBody: string }

/** Recruitment → Offer letter template. */
export function OfferTemplateEditor() {
  const les = useLegalEntities()
  const [le, setLe] = useState('')
  const entity = le || les.data?.[0]?.id || ''
  const q = useQuery({ queryKey: ['offer-template', entity], queryFn: () => api.get<TemplateResponse>(`/api/payroll/recruitment/offer-template/${entity}`), enabled: !!entity })
  if (!entity || q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  return (
    <div className="card">
      {(les.data?.length ?? 0) > 1 && <div className="toolbar"><select value={entity} onChange={e => setLe(e.target.value)} aria-label="Company">{les.data!.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}</select></div>}
      {q.data && <TemplateForm key={q.data.template.id} data={q.data} entity={entity} />}
    </div>
  )
}

function TemplateForm({ data, entity }: { data: TemplateResponse; entity: string }) {
  const qc = useQueryClient()
  const t = data.template
  const [f, setF] = useState({ subject: t.subject, body: t.body, signatoryName: t.signatoryName ?? '', signatoryTitle: t.signatoryTitle ?? '', probationMonths: t.probationMonths, acceptWithinDays: t.acceptWithinDays })
  const [preview, setPreview] = useState(false)
  const save = useMutation({ mutationFn: () => api.put(`/api/payroll/recruitment/offer-template/${entity}`, f), onSuccess: () => qc.invalidateQueries({ queryKey: ['offer-template', entity] }) })
  const insert = (key: string) => setF(x => ({ ...x, body: `${x.body}{${key}}` }))
  return (
    <div className="grid" style={{ gridTemplateColumns: 'minmax(0, 2fr) minmax(220px, 1fr)', gap: 16 }}>
      <div>
        <h2 style={{ marginTop: 0 }}>Offer letter template</h2>
        <p className="small muted">Every new offer letter starts from this. It is a starting point based on the Employment Act, 2007: have it reviewed by your lawyer before use.</p>
        <ErrorBox error={save.error} />
        {save.isSuccess && <div className="alert ok">Saved. New drafts use this wording; drafts already open keep theirs until you reset them.</div>}
        <Field label="Subject"><input value={f.subject} onChange={e => setF({ ...f, subject: e.target.value })} /></Field>
        {preview ? <div style={{ border: '1px solid var(--border)', borderRadius: 6, padding: '12px 16px', background: 'var(--surface-2)', marginBottom: 12 }}><LetterText subject={f.subject} body={f.body} /></div>
          : <Field label="Text"><textarea rows={22} value={f.body} onChange={e => setF({ ...f, body: e.target.value })} style={{ fontFamily: 'var(--mono, monospace)', fontSize: 12.5 }} /></Field>}
        <div className="form-grid">
          <Field label="Signed by (default)"><input value={f.signatoryName} onChange={e => setF({ ...f, signatoryName: e.target.value })} /></Field>
          <Field label="Signatory's title"><input value={f.signatoryTitle} onChange={e => setF({ ...f, signatoryTitle: e.target.value })} /></Field>
          <Field label="Probation (months)"><input type="number" min={0} max={12} value={f.probationMonths} onChange={e => setF({ ...f, probationMonths: Number(e.target.value) })} /></Field>
          <Field label="Days to respond"><input type="number" min={1} max={60} value={f.acceptWithinDays} onChange={e => setF({ ...f, acceptWithinDays: Number(e.target.value) })} /></Field>
        </div>
        <div className="toolbar">
          <button className="btn primary" disabled={save.isPending} onClick={() => save.mutate()}>Save template</button>
          <button className="btn" onClick={() => setPreview(p => !p)}>{preview ? 'Edit text' : 'Preview layout'}</button>
          <button className="btn" onClick={() => { if (confirm('Replace the text with the standard wording?')) setF({ ...f, subject: data.standardSubject, body: data.standardBody }) }}>Use standard wording</button>
        </div>
      </div>
      <div>
        <h3 style={{ marginTop: 0 }}>Fields</h3>
        <p className="small muted">Filled in from the offer and company records. Click to add one to the end of the text.</p>
        {data.placeholders.length === 0 ? <Empty>None.</Empty> : <ul className="small" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
          {data.placeholders.map(p => <li key={p.key} style={{ marginBottom: 4 }}><button type="button" className="btn sm" style={{ fontFamily: 'var(--mono, monospace)', padding: '1px 6px' }} onClick={() => insert(p.key)}>{`{${p.key}}`}</button> <span className="muted">{p.meaning}</span></li>)}
        </ul>}
        <p className="small muted" style={{ marginTop: 10 }}>Formatting: blank line = new paragraph · <span className="mono">## </span>heading · <span className="mono">- </span>bullet.</p>
      </div>
    </div>
  )
}
