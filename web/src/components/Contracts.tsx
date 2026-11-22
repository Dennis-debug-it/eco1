import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, isoDate } from '../lib/api'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, ReasonDialog } from './ui'
import { LetterText } from './OfferLetter'

type Kind = 'PermanentContract' | 'FixedTermContract' | 'CasualContract' | 'InternshipContract' | 'VariationLetter'
type Doc = {
  id: string; kind: Kind; reference: string; status: 'Draft' | 'Issued' | 'Signed' | 'Cancelled'; title: string; body: string; signatoryName?: string; signatoryTitle?: string
  startDate: string; endDate?: string; issuedAt?: string; issuedBy?: string; signedOn?: string; signedFileId?: string; cancelReason?: string; createdAt: string
  changes: string[]; preview: { title: string; body: string; problems: string[] }
}

export const kindLabel: Record<Kind, string> = {
  PermanentContract: 'Permanent contract', FixedTermContract: 'Fixed-term contract', CasualContract: 'Casual agreement', InternshipContract: 'Internship agreement', VariationLetter: 'Variation letter',
}
const defaultKind = (employmentType: string): Kind =>
  employmentType === 'Contract' || employmentType === 'Expatriate' ? 'FixedTermContract' : employmentType === 'Casual' ? 'CasualContract' : employmentType === 'Intern' ? 'InternshipContract' : 'PermanentContract'
const tone: Record<Doc['status'], string> = { Draft: 'neutral', Issued: 'Pending', Signed: 'Active', Cancelled: 'Cancelled' }
const hasEnd = (k: Kind) => k === 'FixedTermContract' || k === 'InternshipContract'

/** Employee → Contracts & letters: draft, issue for printing, upload the signed copy; variation letters for changes. */
export function ContractsTab({ employeeId, employmentType, hireDate, contractEndDate }: { employeeId: string; employmentType: string; hireDate: string; contractEndDate?: string }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['contracts', employeeId], queryFn: () => api.get<Doc[]>(`/api/payroll/employees/${employeeId}/contracts`) })
  const refresh = () => { qc.invalidateQueries({ queryKey: ['contracts', employeeId] }); qc.invalidateQueries({ queryKey: ['employee', employeeId] }) }
  const [newDoc, setNewDoc] = useState<'contract' | 'variation' | null>(null)
  const [upload, setUpload] = useState<Doc | null>(null)
  const [cancel, setCancel] = useState<Doc | null>(null)
  const issue = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/contracts/${id}/issue`), onSuccess: refresh })
  const docs = q.data ?? []
  const draft = docs.find(d => d.status === 'Draft')
  const pdf = (d: Doc) => api.download(`/api/payroll/contracts/${d.id}/pdf`)
  const lastFixed = docs.find(d => d.status === 'Signed' && d.kind === 'FixedTermContract' && d.endDate)

  return (
    <div className="card">
      <div className="toolbar"><h2 style={{ margin: 0 }}>Contracts & letters</h2><span style={{ flex: 1 }} />
        {!draft && <><button className="btn sm" onClick={() => setNewDoc('variation')}>Draft variation letter</button><button className="btn sm primary" onClick={() => setNewDoc('contract')}>Draft contract</button></>}
      </div>
      <p className="small muted">Print the issued document, have it signed, then upload the signed copy. It is filed in the employee's documents, where they can see it.</p>
      <ErrorBox error={q.error ?? issue.error} />
      {q.isLoading ? <Loading /> : !docs.length ? <Empty>No contracts or letters yet.</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Document</th><th>Dates</th><th>Status</th><th /></tr></thead>
          <tbody>{docs.map(d => <tr key={d.id}>
            <td>{kindLabel[d.kind]}<div className="mono small muted">{d.reference}</div></td>
            <td className="small">{d.kind === 'VariationLetter' ? `Effective ${fmt.date(d.startDate)}` : `From ${fmt.date(d.startDate)}${d.endDate ? ` to ${fmt.date(d.endDate)}` : ''}`}</td>
            <td><Badge status={tone[d.status]}>{d.status === 'Issued' ? 'Awaiting signed copy' : d.status}</Badge>
              <div className="small muted">{d.status === 'Signed' ? `Signed ${fmt.date(d.signedOn)}` : d.status === 'Issued' ? `Issued ${fmt.date(d.issuedAt)}` : d.status === 'Cancelled' ? d.cancelReason : ''}</div></td>
            <td style={{ whiteSpace: 'nowrap' }}>
              {d.status !== 'Cancelled' && <button className="btn sm" onClick={() => pdf(d)}>{d.status === 'Draft' ? 'Preview PDF' : 'Print'}</button>}{' '}
              {d.status === 'Issued' && <button className="btn sm primary" onClick={() => setUpload(d)}>Upload signed copy</button>}{' '}
              {d.status === 'Signed' && d.signedFileId && <button className="btn sm" onClick={() => api.download(`/api/payroll/files/${d.signedFileId}`)}>Signed copy</button>}{' '}
              {(d.status === 'Draft' || d.status === 'Issued') && <button className="btn sm" onClick={() => setCancel(d)}>Cancel</button>}
            </td>
          </tr>)}</tbody>
        </table></div>
      )}
      {lastFixed && !draft && <p className="small">The fixed term ends on <b>{fmt.date(lastFixed.endDate)}</b>. <a href="#" onClick={e => { e.preventDefault(); setNewDoc('contract') }}>Draft a renewal</a> or start the exit process.</p>}

      {draft && <DraftEditor key={draft.id} doc={draft} onChanged={refresh} onIssue={() => issue.mutate(draft.id)} issuing={issue.isPending} onPdf={() => pdf(draft)} />}

      {newDoc && <NewDocDialog kind={newDoc} employeeId={employeeId} defaultContract={defaultKind(employmentType)} hireDate={hireDate} contractEndDate={contractEndDate} renewalOf={lastFixed} onClose={() => setNewDoc(null)} onDone={() => { setNewDoc(null); refresh() }} />}
      {upload && <UploadSigned doc={upload} onClose={() => setUpload(null)} onDone={() => { setUpload(null); refresh() }} />}
      {cancel && <ReasonDialog title={`Cancel ${cancel.reference}`} label="Reason" required danger confirmLabel="Cancel document" onClose={() => setCancel(null)}
        onConfirm={async reason => { await api.post(`/api/payroll/contracts/${cancel.id}/cancel`, { reason }); refresh() }} />}
    </div>
  )
}

function NewDocDialog({ kind, employeeId, defaultContract, hireDate, contractEndDate, renewalOf, onClose, onDone }: { kind: 'contract' | 'variation'; employeeId: string; defaultContract: Kind; hireDate: string; contractEndDate?: string; renewalOf?: Doc; onClose: () => void; onDone: () => void }) {
  const renewalStart = renewalOf?.endDate ? isoDate(Math.round((new Date(renewalOf.endDate + 'T00:00:00').getTime() - Date.now()) / 864e5) + 1) : undefined
  const [f, setF] = useState({ kind: renewalOf ? 'FixedTermContract' as Kind : defaultContract, startDate: renewalStart ?? hireDate, endDate: renewalOf ? '' : contractEndDate ?? '', effectiveDate: '' })
  const create = useMutation({
    mutationFn: () => kind === 'variation'
      ? api.post(`/api/payroll/employees/${employeeId}/variations`, { effectiveDate: f.effectiveDate || null })
      : api.post(`/api/payroll/employees/${employeeId}/contracts`, { kind: f.kind, startDate: f.startDate || null, endDate: hasEnd(f.kind) && f.endDate ? f.endDate : null }),
    onSuccess: onDone,
  })
  return (
    <Modal title={kind === 'variation' ? 'Draft a variation letter' : renewalOf ? 'Draft a renewal' : 'Draft a contract'} onClose={onClose}>
      <ErrorBox error={create.error} />
      {kind === 'variation' ? <>
        <p className="small">The letter lists each change to job, place of work, reporting line, pay, allowances or notice since the last contract or letter issued (or, if there is none, since the previous employment and pay records). Update the employee record and pay first, then draft the letter.</p>
        <Field label="Effective date (leave blank to use the date of the latest change)"><input type="date" value={f.effectiveDate} onChange={e => setF({ ...f, effectiveDate: e.target.value })} /></Field>
      </> : <div className="form-grid">
        <Field label="Type"><select value={f.kind} onChange={e => setF({ ...f, kind: e.target.value as Kind })}>{(['PermanentContract', 'FixedTermContract', 'CasualContract', 'InternshipContract'] as Kind[]).map(k => <option key={k} value={k}>{kindLabel[k]}</option>)}</select></Field>
        <Field label="Starts"><input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value })} /></Field>
        {hasEnd(f.kind) && <Field label="Ends"><input type="date" value={f.endDate} min={f.startDate} onChange={e => setF({ ...f, endDate: e.target.value })} /></Field>}
      </div>}
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={create.isPending || (kind === 'contract' && hasEnd(f.kind) && !f.endDate)} onClick={() => create.mutate()}>Draft</button></div>
    </Modal>
  )
}

function DraftEditor({ doc, onChanged, onIssue, issuing, onPdf }: { doc: Doc; onChanged: () => void; onIssue: () => void; issuing: boolean; onPdf: () => void }) {
  const [f, setF] = useState({ title: doc.title, body: doc.body, startDate: doc.startDate, endDate: doc.endDate ?? '', signatoryName: doc.signatoryName ?? '', signatoryTitle: doc.signatoryTitle ?? '' })
  const [mode, setMode] = useState<'preview' | 'edit'>('preview')
  const dirty = f.title !== doc.title || f.body !== doc.body || f.startDate !== doc.startDate || f.endDate !== (doc.endDate ?? '') || f.signatoryName !== (doc.signatoryName ?? '') || f.signatoryTitle !== (doc.signatoryTitle ?? '')
  const save = useMutation({ mutationFn: () => api.put(`/api/payroll/contracts/${doc.id}`, { ...f, endDate: f.endDate || null, signatoryName: f.signatoryName || null, signatoryTitle: f.signatoryTitle || null }), onSuccess: () => { onChanged(); setMode('preview') } })
  const isVariation = doc.kind === 'VariationLetter'
  return (
    <div className="card" style={{ marginTop: 12 }}>
      <div className="toolbar"><h3 style={{ margin: 0 }}>Draft: {kindLabel[doc.kind]}</h3><span className="mono small muted">{doc.reference}</span></div>
      <ErrorBox error={save.error} />
      {isVariation && <div className="small" style={{ marginBottom: 8 }}><b>Changes found:</b> {doc.changes.length ? <ul style={{ margin: '4px 0', paddingLeft: 20 }}>{doc.changes.map((c, i) => <li key={i}>{c}</li>)}</ul> : <span className="muted"> none in the records. Describe the change in the text.</span>}</div>}
      {doc.preview.problems.length > 0 && <div className="alert warn">Before issuing: <ul style={{ margin: '4px 0 0', paddingLeft: 20 }}>{doc.preview.problems.map((p, i) => <li key={i}>{p}</li>)}</ul></div>}
      <div className="form-grid">
        <Field label={isVariation ? 'Effective date' : 'Starts'}><input type="date" value={f.startDate} onChange={e => setF({ ...f, startDate: e.target.value })} /></Field>
        {hasEnd(doc.kind) && <Field label="Ends"><input type="date" value={f.endDate} min={f.startDate} onChange={e => setF({ ...f, endDate: e.target.value })} /></Field>}
        <Field label="Signed for the company by"><input value={f.signatoryName} onChange={e => setF({ ...f, signatoryName: e.target.value })} /></Field>
        <Field label="Their title"><input value={f.signatoryTitle} onChange={e => setF({ ...f, signatoryTitle: e.target.value })} /></Field>
      </div>
      <div className="tabs" role="tablist" style={{ marginBottom: 8 }}>
        <button role="tab" aria-selected={mode === 'preview'} className={mode === 'preview' ? 'active' : ''} onClick={() => setMode('preview')}>Document</button>
        <button role="tab" aria-selected={mode === 'edit'} className={mode === 'edit' ? 'active' : ''} onClick={() => setMode('edit')}>Edit wording</button>
      </div>
      {mode === 'preview'
        ? <div style={{ maxHeight: 420, overflow: 'auto', border: '1px solid var(--border)', borderRadius: 6, padding: '12px 16px', background: 'var(--surface-2)' }}>{dirty ? <p className="small muted">Save to see your changes filled in.</p> : <LetterText subject={doc.preview.title} body={doc.preview.body} />}</div>
        : <>
          <Field label="Title"><input value={f.title} onChange={e => setF({ ...f, title: e.target.value })} /></Field>
          <Field label="Text"><textarea rows={18} value={f.body} onChange={e => setF({ ...f, body: e.target.value })} style={{ fontFamily: 'var(--mono, monospace)', fontSize: 12.5 }} /></Field>
          <p className="small muted">Fields in braces are filled from the employee's record when you issue. To fill a gap only for this document, type the value in place of the field.</p>
        </>}
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button className="btn sm" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>Save draft</button>
        <button className="btn sm" disabled={dirty} onClick={onPdf}>Preview PDF</button>
        <span style={{ flex: 1 }} />
        <button className="btn primary" disabled={dirty || issuing || doc.preview.problems.length > 0} title={dirty ? 'Save first' : ''} onClick={() => { if (confirm('Issue this document? The text is fixed and a copy kept for printing and signature.')) onIssue() }}>Issue for signature</button>
      </div>
    </div>
  )
}

function UploadSigned({ doc, onClose, onDone }: { doc: Doc; onClose: () => void; onDone: () => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [signedOn, setSignedOn] = useState(isoDate())
  const send = useMutation({
    mutationFn: () => { const form = new FormData(); form.append('file', file!); form.append('signedOn', signedOn); return api.upload(`/api/payroll/contracts/${doc.id}/signed`, form) },
    onSuccess: onDone,
  })
  return (
    <Modal title={`Signed copy of ${doc.reference}`} onClose={onClose}>
      <ErrorBox error={send.error} />
      <p className="small">Scan or photograph every page of the signed document. It is filed in the employee's documents as a contract, visible to them.{doc.kind === 'FixedTermContract' && ' The employee\'s contract end date is updated to match.'}</p>
      <Field label="Signed copy (PDF, JPG or PNG, up to 5 MB)"><input type="file" accept=".pdf,.jpg,.jpeg,.png" onChange={e => setFile(e.target.files?.[0] ?? null)} /></Field>
      <Field label="Date signed"><input type="date" max={isoDate()} value={signedOn} onChange={e => setSignedOn(e.target.value)} /></Field>
      <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!file || send.isPending} onClick={() => send.mutate()}>Upload</button></div>
    </Modal>
  )
}

type TemplateResponse = { template: { id: string; kind: Kind; title: string; body: string; signatoryName?: string; signatoryTitle?: string }; placeholders: { key: string; meaning: string }[]; standardTitle: string; standardBody: string }

/** People records → Contract templates. */
export function ContractTemplates() {
  const les = useLegalEntities()
  const [le, setLe] = useState('')
  const [kind, setKind] = useState<Kind>('PermanentContract')
  const entity = le || les.data?.[0]?.id || ''
  const q = useQuery({ queryKey: ['contract-template', entity, kind], queryFn: () => api.get<TemplateResponse>(`/api/payroll/contract-templates/${entity}/${kind}`), enabled: !!entity })
  return (
    <div className="card">
      <div className="toolbar">
        <h2 style={{ margin: 0 }}>Contract templates</h2><span style={{ flex: 1 }} />
        {(les.data?.length ?? 0) > 1 && <select value={entity} onChange={e => setLe(e.target.value)} aria-label="Company">{les.data!.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}</select>}
        <select value={kind} onChange={e => setKind(e.target.value as Kind)} aria-label="Template">{(Object.keys(kindLabel) as Kind[]).map(k => <option key={k} value={k}>{kindLabel[k]}</option>)}</select>
      </div>
      <p className="small muted">The starting wording for each kind of document, based on the Employment Act, 2007. Have it reviewed by your lawyer before use. Changes apply to new drafts.</p>
      <ErrorBox error={q.error} />
      {!q.data ? <Loading /> : <TemplateForm key={q.data.template.id} data={q.data} url={`/api/payroll/contract-templates/${entity}/${kind}`} queryKey={['contract-template', entity, kind]} />}
    </div>
  )
}

function TemplateForm({ data, url, queryKey }: { data: TemplateResponse; url: string; queryKey: string[] }) {
  const qc = useQueryClient()
  const t = data.template
  const [f, setF] = useState({ title: t.title, body: t.body, signatoryName: t.signatoryName ?? '', signatoryTitle: t.signatoryTitle ?? '' })
  const [preview, setPreview] = useState(false)
  const save = useMutation({ mutationFn: () => api.put(url, f), onSuccess: () => qc.invalidateQueries({ queryKey }) })
  return (
    <div className="grid" style={{ gridTemplateColumns: 'minmax(0, 2fr) minmax(220px, 1fr)', gap: 16 }}>
      <div>
        <ErrorBox error={save.error} />
        {save.isSuccess && <div className="alert ok">Saved.</div>}
        <Field label="Title"><input value={f.title} onChange={e => setF({ ...f, title: e.target.value })} /></Field>
        {preview ? <div style={{ border: '1px solid var(--border)', borderRadius: 6, padding: '12px 16px', background: 'var(--surface-2)', marginBottom: 12 }}><LetterText subject={f.title} body={f.body} /></div>
          : <Field label="Text"><textarea rows={24} value={f.body} onChange={e => setF({ ...f, body: e.target.value })} style={{ fontFamily: 'var(--mono, monospace)', fontSize: 12.5 }} /></Field>}
        <div className="form-grid">
          <Field label="Signed by (default)"><input value={f.signatoryName} onChange={e => setF({ ...f, signatoryName: e.target.value })} /></Field>
          <Field label="Signatory's title"><input value={f.signatoryTitle} onChange={e => setF({ ...f, signatoryTitle: e.target.value })} /></Field>
        </div>
        <div className="toolbar">
          <button className="btn primary" disabled={save.isPending} onClick={() => save.mutate()}>Save template</button>
          <button className="btn" onClick={() => setPreview(p => !p)}>{preview ? 'Edit text' : 'Preview layout'}</button>
          <button className="btn" onClick={() => { if (confirm('Replace the text with the standard wording?')) setF({ ...f, title: data.standardTitle, body: data.standardBody }) }}>Use standard wording</button>
        </div>
      </div>
      <div>
        <h3 style={{ marginTop: 0 }}>Fields</h3>
        <p className="small muted">Filled in from the employee's record and pay. Click to add one to the end of the text.</p>
        <ul className="small" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
          {data.placeholders.map(p => <li key={p.key} style={{ marginBottom: 4 }}><button type="button" className="btn sm" style={{ fontFamily: 'var(--mono, monospace)', padding: '1px 6px' }} onClick={() => setF(x => ({ ...x, body: `${x.body}{${p.key}}` }))}>{`{${p.key}}`}</button> <span className="muted">{p.meaning}</span></li>)}
        </ul>
        <p className="small muted" style={{ marginTop: 10 }}>Formatting: blank line = new paragraph · <span className="mono">## </span>heading · <span className="mono">- </span>bullet.</p>
      </div>
    </div>
  )
}
