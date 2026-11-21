import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, tokenStore } from '../lib/api'
import type { LegalEntity } from '../lib/types'
import { ErrorBox, Field, Loading, Modal, UploadButton } from './ui'

type Settings = { layout: 'Standard' | 'HeaderImage'; brandColour?: string | null; tagline?: string | null; website?: string | null; postalAddress?: string | null; footerText?: string | null; showRegistrationDetails: boolean; hasLogo: boolean; hasHeader: boolean }

/** Loads an image that needs the sign-in token, as an object URL. */
function useAuthImage(url: string | null, version: number) {
  const [src, setSrc] = useState<string | null>(null)
  useEffect(() => {
    if (!url) { setSrc(null); return }
    let revoke: string | null = null
    let live = true
    fetch(url, { headers: { Authorization: `Bearer ${tokenStore.get() ?? ''}` } })
      .then(r => (r.ok ? r.blob() : null))
      .then(b => { if (live && b) { revoke = URL.createObjectURL(b); setSrc(revoke) } })
      .catch(() => setSrc(null))
    return () => { live = false; if (revoke) URL.revokeObjectURL(revoke) }
  }, [url, version])
  return src
}

/** Company letterhead: logo or header artwork, brand colour and details, for letters and payslips. */
export default function LetterheadDialog({ entity, onClose }: { entity: LegalEntity; onClose: () => void }) {
  const qc = useQueryClient()
  const base = `/api/payroll/organisation/legal-entities/${entity.id}/letterhead`
  const q = useQuery({ queryKey: ['letterhead', entity.id], queryFn: () => api.get<Settings>(base) })
  const [version, setVersion] = useState(0)
  const [f, setF] = useState<Settings | null>(null)
  useEffect(() => { if (q.data) setF(x => x ? { ...x, hasLogo: q.data.hasLogo, hasHeader: q.data.hasHeader, layout: q.data.hasHeader && !x.hasHeader ? 'HeaderImage' : q.data.hasHeader ? x.layout : 'Standard' } : q.data) }, [q.data])
  const logo = useAuthImage(f?.hasLogo ? `${base}/logo` : null, version)
  const header = useAuthImage(f?.hasHeader ? `${base}/header` : null, version)
  const refresh = () => { setVersion(v => v + 1); qc.invalidateQueries({ queryKey: ['letterhead', entity.id] }) }
  const save = useMutation({
    mutationFn: () => api.put(base, { layout: f!.layout, brandColour: f!.brandColour || null, tagline: f!.tagline || null, website: f!.website || null, postalAddress: f!.postalAddress || null, footerText: f!.footerText || null, showRegistrationDetails: f!.showRegistrationDetails }),
    onSuccess: refresh,
  })
  const remove = useMutation({ mutationFn: (kind: 'logo' | 'header') => api.del(`${base}/${kind}`), onSuccess: refresh })
  const dirty = !!q.data && !!f && (['layout', 'brandColour', 'tagline', 'website', 'postalAddress', 'footerText', 'showRegistrationDetails'] as const).some(k => (f[k] ?? '') !== (q.data[k] ?? ''))

  if (!f) return <Modal title="Letterhead" onClose={onClose}>{q.error ? <ErrorBox error={q.error} /> : <Loading />}</Modal>
  const set = <K extends keyof Settings>(k: K, v: Settings[K]) => setF({ ...f, [k]: v })
  const colour = f.brandColour || undefined
  const details = [[entity.address, f.postalAddress].filter(Boolean).join('  ·  '), [entity.phone, entity.email, f.website].filter(Boolean).join('  ·  '), f.showRegistrationDetails ? `PIN ${entity.taxPin}${entity.registrationNumber ? `   Reg. ${entity.registrationNumber}` : ''}` : ''].filter(Boolean)

  return (
    <Modal title={`Letterhead: ${entity.name}`} onClose={onClose} width={880}>
      <p className="small muted" style={{ marginTop: 0 }}>Used on offer letters, certificates of service, final dues statements and payslips for this company. Letters already sent keep the letterhead they went out with.</p>
      <ErrorBox error={save.error ?? remove.error} />

      {/* Live preview, roughly as printed. */}
      <div aria-label="Preview" style={{ background: '#fff', color: '#16202b', border: '1px solid var(--border)', borderRadius: 6, padding: '18px 24px 12px', marginBottom: 14 }}>
        {f.layout === 'HeaderImage' && header ? <img src={header} alt="Header image" style={{ width: '100%', maxHeight: 120, objectFit: 'contain', display: 'block' }} />
          : <div style={{ display: 'flex', alignItems: 'flex-start', gap: 16, justifyContent: logo ? 'space-between' : 'flex-start' }}>
            {logo && <img src={logo} alt="Logo" style={{ maxHeight: 64, maxWidth: 170, objectFit: 'contain' }} />}
            <div style={{ textAlign: logo ? 'right' : 'left', minWidth: 0 }}>
              <div style={{ fontWeight: 700, fontSize: 18, color: colour }}>{entity.name}</div>
              {f.tagline && <div style={{ fontSize: 11 }}>{f.tagline}</div>}
              {details.map((d, i) => <div key={i} style={{ fontSize: 10, color: '#3d4a57' }}>{d}</div>)}
            </div>
          </div>}
        <div style={{ borderTop: `2px solid ${colour ?? '#000'}`, marginTop: 10 }} />
        <div style={{ height: 40, fontSize: 11, color: '#5b6b7c', paddingTop: 10 }}>Letter text…</div>
        {f.footerText && <div style={{ borderTop: colour ? `1px solid ${colour}` : undefined, fontSize: 9, textAlign: 'center', paddingTop: 4, color: '#3d4a57' }}>{f.footerText}</div>}
      </div>

      <div role="radiogroup" aria-label="Layout" className="toolbar" style={{ flexWrap: 'wrap', gap: 16 }}>
        <label className="small"><input type="radio" checked={f.layout === 'Standard'} onChange={() => set('layout', 'Standard')} /> Logo and company details</label>
        <label className="small" title={f.hasHeader ? '' : 'Upload a header image first'}><input type="radio" checked={f.layout === 'HeaderImage'} disabled={!f.hasHeader} onChange={() => set('layout', 'HeaderImage')} /> Our own header image</label>
      </div>

      <div className="grid c2" style={{ gap: 12 }}>
        <div className="card" style={{ margin: 0 }}>
          <h3 style={{ marginTop: 0 }}>Logo</h3>
          <p className="small muted">PNG (a transparent background looks best) or JPG, under 1 MB. Shown at the top left, up to about 2 cm tall.</p>
          <div className="toolbar">
            <UploadButton label={f.hasLogo ? 'Replace logo' : 'Upload logo'} url={`${base}/logo`} accept=".png,.jpg,.jpeg" onDone={refresh} />
            {f.hasLogo && <button className="btn sm" disabled={remove.isPending} onClick={() => remove.mutate('logo')}>Remove</button>}
          </div>
        </div>
        <div className="card" style={{ margin: 0 }}>
          <h3 style={{ marginTop: 0 }}>Header image</h3>
          <p className="small muted">Already have designed letterhead artwork? Upload the top strip as a wide image (at least 1000 pixels wide, 2.5 times wider than tall). It replaces the logo and details.</p>
          <div className="toolbar">
            <UploadButton label={f.hasHeader ? 'Replace header' : 'Upload header'} url={`${base}/header`} accept=".png,.jpg,.jpeg" onDone={refresh} />
            {f.hasHeader && <button className="btn sm" disabled={remove.isPending} onClick={() => remove.mutate('header')}>Remove</button>}
          </div>
        </div>
      </div>

      <div className="form-grid" style={{ marginTop: 12 }}>
        <Field label="Brand colour">
          <div className="toolbar" style={{ margin: 0 }}>
            <input type="color" value={f.brandColour || '#000000'} onChange={e => set('brandColour', e.target.value)} aria-label="Brand colour" style={{ width: 44, height: 34, padding: 2 }} />
            <input value={f.brandColour ?? ''} onChange={e => set('brandColour', e.target.value)} placeholder="None" aria-label="Brand colour code" style={{ width: 110 }} />
            {f.brandColour && <button type="button" className="btn sm" onClick={() => set('brandColour', null)}>None</button>}
          </div>
        </Field>
        <Field label="Tagline"><input value={f.tagline ?? ''} onChange={e => set('tagline', e.target.value)} maxLength={120} /></Field>
        <Field label="Website"><input value={f.website ?? ''} onChange={e => set('website', e.target.value)} placeholder="www.example.co.ke" maxLength={100} /></Field>
        <Field label="Postal address"><input value={f.postalAddress ?? ''} onChange={e => set('postalAddress', e.target.value)} placeholder="P.O. Box 12345-00100, Nairobi" maxLength={120} /></Field>
      </div>
      <Field label="Footer (small print on every letter)"><input value={f.footerText ?? ''} onChange={e => set('footerText', e.target.value)} placeholder="Registered office … · Directors: …" maxLength={250} /></Field>
      <label className="field inline"><input type="checkbox" checked={f.showRegistrationDetails} onChange={e => set('showRegistrationDetails', e.target.checked)} /> Show KRA PIN and registration number</label>
      <p className="small muted">Address, phone and email come from the company's details. Light brand colours are used for the rules only, so text stays readable.</p>

      <div className="actions" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" disabled={dirty} title={dirty ? 'Save first' : ''} onClick={() => api.download(`${base}/preview`)}>Preview as PDF</button>
        <button className="btn" onClick={onClose}>Close</button>
        <button className="btn primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>{save.isPending ? 'Saving…' : 'Save'}</button>
      </div>
    </Modal>
  )
}
