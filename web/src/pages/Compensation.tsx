import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { P, useAuth } from '../lib/auth'
import { byId, useComponents, useLegalEntities, useStructures } from '../lib/lookups'
import type { Component, Structure } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'

const treatments = [['Ordinary', 'Ordinary (after tax)'], ['RegisteredPension', 'Registered pension (tax-deductible within cap)'], ['PostRetirementMedicalFund', 'Post-retirement medical fund'], ['MortgageInterest', 'Owner-occupier mortgage interest'], ['QualifyingInsurancePremium', 'Qualifying insurance premium (15% relief)']]

export default function CompensationPage() {
  const { can } = useAuth()
  const les = useLegalEntities()
  const [le, setLe] = useState('')
  const leId = le || les.data?.[0]?.id
  const [tab, setTab] = useState<'components' | 'structures'>('components')
  return (
    <>
      <PageHead title="Compensation" sub="Earnings, deductions and reusable salary structures" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'components', label: 'Salary components' }, { id: 'structures', label: 'Salary structures' }]} />
      {les.data && les.data.length > 1 && <div className="toolbar"><select value={leId} onChange={e => setLe(e.target.value)} aria-label="Legal entity">{les.data.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></div>}
      {leId && tab === 'components' && <Components legalEntityId={leId} canEdit={can(P.CompensationManage)} />}
      {leId && tab === 'structures' && <Structures legalEntityId={leId} canEdit={can(P.CompensationManage)} />}
    </>
  )
}

function Components({ legalEntityId, canEdit }: { legalEntityId: string; canEdit: boolean }) {
  const q = useComponents(legalEntityId); const qc = useQueryClient()
  const [edit, setEdit] = useState<Partial<Component> | null>(null)
  const m = useMutation({ mutationFn: (c: Partial<Component>) => c.id ? api.put(`/api/payroll/compensation/components/${c.id}`, c) : api.post('/api/payroll/compensation/components', { ...c, legalEntityId }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['components'] }); setEdit(null) } })
  if (q.isLoading) return <Loading />
  const render = (type: 'Earning' | 'Deduction') => (
    <div className="card"><h2>{type === 'Earning' ? 'Earnings' : 'Deductions'}</h2>
      <table><thead><tr><th>Code</th><th>Name</th><th>Method</th><th className="num">Default</th>{type === 'Earning' ? <><th>Taxable</th><th>Pensionable</th></> : <><th>Tax treatment</th><th className="num">Employer match</th></>}<th>GL</th><th>Active</th><th /></tr></thead>
        <tbody>{q.data!.filter(c => c.type === type).map(c => <tr key={c.id}><td className="mono">{c.code} {c.isBasic && <Badge status="Active">Basic</Badge>}</td><td>{c.name}</td><td>{c.method}</td><td className="num">{c.defaultRate != null ? `${(c.defaultRate * 100).toFixed(2)}%` : c.defaultAmount != null ? fmt.num(c.defaultAmount) : '—'}</td>
          {type === 'Earning' ? <><td>{c.taxable ? 'Yes' : <Badge status="Warning">No</Badge>}</td><td>{c.pensionable ? 'Yes' : 'No'}</td></> : <><td>{treatments.find(t => t[0] === c.deductionTreatment)?.[1].split(' (')[0]}</td><td className="num">{c.employerMatchRate ? `${(c.employerMatchRate * 100).toFixed(0)}%` : '—'}</td></>}
          <td className="mono">{c.glAccountCode}</td><td>{c.isActive ? 'Yes' : 'No'}</td><td>{canEdit && <button className="btn sm" onClick={() => setEdit(c)}>Edit</button>}</td></tr>)}</tbody></table></div>)
  return (
    <>
      {canEdit && <div className="actions" style={{ marginBottom: 12 }}><button className="btn primary" onClick={() => setEdit({ type: 'Earning', method: 'FixedAmount', taxable: true, pensionable: false, isBasic: false, deductionTreatment: 'Ordinary', employerMatchRate: 0, recurrence: 'EveryPeriod', showOnPayslip: true, sortOrder: 0, isActive: true })}>New component</button></div>}
      {render('Earning')}{render('Deduction')}
      {edit && <Modal title={edit.id ? `Edit ${edit.code}` : 'New salary component'} onClose={() => setEdit(null)} width={700}>
        <ErrorBox error={m.error} />
        <div className="form-grid">
          <Field label="Code *"><input value={edit.code ?? ''} disabled={!!edit.id} onChange={e => setEdit({ ...edit, code: e.target.value.toUpperCase() })} /></Field>
          <Field label="Name *"><input value={edit.name ?? ''} onChange={e => setEdit({ ...edit, name: e.target.value })} /></Field>
          <Field label="Type"><select value={edit.type} disabled={!!edit.id} onChange={e => setEdit({ ...edit, type: e.target.value as Component['type'] })}><option>Earning</option><option>Deduction</option></select></Field>
          <Field label="Calculation"><select value={edit.method} onChange={e => setEdit({ ...edit, method: e.target.value })}>{[['FixedAmount', 'Fixed amount'], ['PercentOfBasic', '% of basic'], ['PercentOfGross', '% of gross'], ['Variable', 'Variable (entered per run)']].map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></Field>
          <Field label="Default amount"><input type="number" step="0.01" value={edit.defaultAmount ?? ''} onChange={e => setEdit({ ...edit, defaultAmount: e.target.value === '' ? undefined : Number(e.target.value) })} /></Field>
          <Field label="Default rate (0.15 = 15%)"><input type="number" step="0.0001" value={edit.defaultRate ?? ''} onChange={e => setEdit({ ...edit, defaultRate: e.target.value === '' ? undefined : Number(e.target.value) })} /></Field>
          <Field label="GL expense / liability account"><input value={edit.glAccountCode ?? ''} onChange={e => setEdit({ ...edit, glAccountCode: e.target.value })} /></Field>
          <Field label="Sort order"><input type="number" value={edit.sortOrder ?? 0} onChange={e => setEdit({ ...edit, sortOrder: Number(e.target.value) })} /></Field>
        </div>
        {edit.type === 'Earning' ? <>
          <label className="field inline"><input type="checkbox" checked={!!edit.taxable} onChange={e => setEdit({ ...edit, taxable: e.target.checked })} /> Taxable (included in PAYE computation)</label>
          <label className="field inline"><input type="checkbox" checked={!!edit.pensionable} onChange={e => setEdit({ ...edit, pensionable: e.target.checked })} /> Pensionable (NSSF / pension base)</label>
          <label className="field inline"><input type="checkbox" checked={!!edit.isBasic} onChange={e => setEdit({ ...edit, isBasic: e.target.checked })} /> This is the basic-salary component</label>
        </> : <>
          <Field label="Statutory treatment"><select value={edit.deductionTreatment} onChange={e => setEdit({ ...edit, deductionTreatment: e.target.value })}>{treatments.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></Field>
          <Field label="Employer matching rate (1 = 100% of employee amount)"><input type="number" step="0.01" value={edit.employerMatchRate ?? 0} onChange={e => setEdit({ ...edit, employerMatchRate: Number(e.target.value) })} /></Field>
        </>}
        <label className="field inline"><input type="checkbox" checked={!!edit.isActive} onChange={e => setEdit({ ...edit, isActive: e.target.checked })} /> Active</label>
        <div className="actions" style={{ justifyContent: 'flex-end' }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={m.isPending || !edit.code || !edit.name} onClick={() => m.mutate(edit)}>Save</button></div>
      </Modal>}
    </>
  )
}

function Structures({ legalEntityId, canEdit }: { legalEntityId: string; canEdit: boolean }) {
  const q = useStructures(legalEntityId); const comps = byId(useComponents(legalEntityId).data); const qc = useQueryClient()
  const [edit, setEdit] = useState<Partial<Structure> | null>(null)
  const m = useMutation({ mutationFn: (s: Partial<Structure>) => s.id ? api.put(`/api/payroll/compensation/salary-structures/${s.id}`, s) : api.post('/api/payroll/compensation/salary-structures', { ...s, legalEntityId }), onSuccess: () => { qc.invalidateQueries({ queryKey: ['structures'] }); setEdit(null) } })
  if (q.isLoading) return <Loading />
  const lineText = (l: Structure['lines'][number]) => l.method === 'FixedAmount' ? fmt.num(l.amount) : l.method === 'Variable' ? 'per run' : `${((l.rate ?? 0) * 100).toFixed(2)}% ${l.method === 'PercentOfBasic' ? 'of basic' : 'of gross'}`
  return (
    <>
      {canEdit && <div className="actions" style={{ marginBottom: 12 }}><button className="btn primary" onClick={() => setEdit({ isActive: true, lines: [] })}>New structure</button></div>}
      {!q.data?.length ? <div className="card"><Empty>No salary structures.</Empty></div> : <div className="grid c2">{q.data.map(s => (
        <div className="card" key={s.id}><div className="page-head" style={{ marginBottom: 6 }}><h2>{s.name} <span className="muted mono small">{s.code}</span></h2>{canEdit && <button className="btn sm" onClick={() => setEdit(s)}>Edit</button>}</div>
          {s.description && <p className="muted small">{s.description}</p>}
          <table><tbody>{s.lines.map((l, i) => <tr key={i}><td>{comps[l.salaryComponentId]?.name ?? '?'} <span className="muted small">{comps[l.salaryComponentId]?.type}</span></td><td className="num">{lineText(l)}</td></tr>)}</tbody></table>
          <p className="muted small" style={{ marginTop: 8 }}>Basic salary is set per employee; the structure adds these components on top.</p></div>))}</div>}
      {edit && <StructureModal edit={edit} setEdit={setEdit} comps={Object.values(comps)} onSave={() => m.mutate(edit)} busy={m.isPending} error={m.error} />}
    </>
  )
}

function StructureModal({ edit, setEdit, comps, onSave, busy, error }: { edit: Partial<Structure>; setEdit: (s: Partial<Structure> | null) => void; comps: Component[]; onSave: () => void; busy: boolean; error: unknown }) {
  const lines = edit.lines ?? []
  const setLine = (i: number, patch: Partial<Structure['lines'][number]>) => setEdit({ ...edit, lines: lines.map((l, k) => (k === i ? { ...l, ...patch } : l)) })
  return (
    <Modal title={edit.id ? `Edit ${edit.code}` : 'New salary structure'} onClose={() => setEdit(null)} width={760}>
      <ErrorBox error={error} />
      <div className="form-grid">
        <Field label="Code *"><input value={edit.code ?? ''} disabled={!!edit.id} onChange={e => setEdit({ ...edit, code: e.target.value.toUpperCase() })} /></Field>
        <Field label="Name *"><input value={edit.name ?? ''} onChange={e => setEdit({ ...edit, name: e.target.value })} /></Field>
        <Field label="Description"><input value={edit.description ?? ''} onChange={e => setEdit({ ...edit, description: e.target.value })} /></Field>
      </div>
      <h3>Components</h3>
      <table><thead><tr><th>Component</th><th>Method</th><th>Amount</th><th>Rate</th><th /></tr></thead>
        <tbody>{lines.map((l, i) => <tr key={i}>
          <td><select value={l.salaryComponentId} onChange={e => setLine(i, { salaryComponentId: e.target.value })}>{comps.filter(c => !c.isBasic).map(c => <option key={c.id} value={c.id}>{c.name} ({c.type})</option>)}</select></td>
          <td><select value={l.method} onChange={e => setLine(i, { method: e.target.value })}>{['FixedAmount', 'PercentOfBasic', 'PercentOfGross', 'Variable'].map(x => <option key={x}>{x}</option>)}</select></td>
          <td><input type="number" step="0.01" style={{ width: 110 }} value={l.amount ?? ''} onChange={e => setLine(i, { amount: e.target.value === '' ? undefined : Number(e.target.value) })} disabled={l.method !== 'FixedAmount'} /></td>
          <td><input type="number" step="0.0001" style={{ width: 90 }} value={l.rate ?? ''} onChange={e => setLine(i, { rate: e.target.value === '' ? undefined : Number(e.target.value) })} disabled={!l.method.startsWith('Percent')} /></td>
          <td><button className="btn sm danger" onClick={() => setEdit({ ...edit, lines: lines.filter((_, k) => k !== i) })}>Remove</button></td></tr>)}</tbody></table>
      <button className="btn sm" style={{ marginTop: 8 }} onClick={() => setEdit({ ...edit, lines: [...lines, { salaryComponentId: comps.find(c => !c.isBasic)?.id ?? '', method: 'FixedAmount' }] })}>Add component</button>
      <div className="actions" style={{ justifyContent: 'flex-end', marginTop: 12 }}><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={busy || !edit.code || !edit.name} onClick={onSave}>Save</button></div>
    </Modal>
  )
}
