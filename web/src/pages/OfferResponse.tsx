import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api, ApiError, fmt } from '../lib/api'
import { ErrorBox, Field, Loading } from '../components/ui'

type Offer = { company: string; candidate: string; reference: string; jobTitle: string; startDate: string; acceptBy: string; status: string; subject: string; blocks: { kind: string; text: string }[]; signatoryName?: string; signatoryTitle?: string; respondedAt?: string; responseName?: string }

/** The candidate's page, reached from the link in their offer email. No sign-in. */
export default function OfferResponsePage() {
  const { token = '' } = useParams()
  const [offer, setOffer] = useState<Offer | null>(null)
  const [err, setErr] = useState<unknown>(null)
  const [mode, setMode] = useState<'accept' | 'decline' | null>(null)
  const [name, setName] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const load = () => api.get<Offer>(`/api/payroll/offers/${encodeURIComponent(token)}`).then(setOffer).catch(setErr)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { load() }, [token])

  const respond = async (accept: boolean) => {
    setBusy(true); setErr(null)
    try { await api.post(`/api/payroll/offers/${encodeURIComponent(token)}/respond`, { accept, fullName: name, reason: reason || undefined }); setMode(null); await load(); window.scrollTo({ top: 0, behavior: 'smooth' }) }
    catch (e) { setErr(e) } finally { setBusy(false) }
  }

  if (!offer && !err) return <Loading what="Opening your offer" />
  if (!offer) return <div className="login"><div className="card"><h2>Offer not available</h2><p className="small">{err instanceof ApiError ? err.problem.detail : 'This link could not be opened.'}</p></div></div>

  const items: React.ReactNode[] = []
  for (let i = 0; i < offer.blocks.length; i++) {
    const b = offer.blocks[i]
    if (b.kind === '-') {
      const run: string[] = []
      while (i < offer.blocks.length && offer.blocks[i].kind === '-') run.push(offer.blocks[i++].text)
      i--
      items.push(<ul key={i} style={{ paddingLeft: 20, margin: '0 0 12px' }}>{run.map((t, k) => <li key={k} style={{ marginBottom: 4 }}>{t}</li>)}</ul>)
    } else if (b.kind === 'h') items.push(<h3 key={i} style={{ margin: '18px 0 6px', fontSize: 15 }}>{b.text}</h3>)
    else items.push(<p key={i} style={{ margin: '0 0 12px' }}>{b.text}</p>)
  }

  return (
    <div style={{ minHeight: '100vh', padding: '24px 16px', display: 'flex', justifyContent: 'center' }}>
      <div style={{ width: 'min(760px, 100%)' }}>
        <div className="brand" style={{ padding: '0 0 12px' }}><div className="logo" aria-hidden>{offer.company[0]}</div><div>{offer.company}<small>Offer of employment · {offer.reference}</small></div></div>
        {offer.status === 'Accepted' && <div className="alert ok">You accepted this offer on {fmt.dateTime(offer.respondedAt)}. {offer.company} will be in touch about your first day, {fmt.date(offer.startDate)}.</div>}
        {offer.status === 'Declined' && <div className="alert info">You declined this offer on {fmt.dateTime(offer.respondedAt)}. Thank you for letting us know.</div>}
        {offer.status === 'Expired' && <div className="alert warn">This offer lapsed on {fmt.date(offer.acceptBy)}. Contact {offer.company} if you are still interested.</div>}
        {offer.status === 'Sent' && <div className="alert info">Please read the letter and respond by <b>{fmt.date(offer.acceptBy)}</b>.</div>}
        <ErrorBox error={err} />
        <div className="card" style={{ padding: '24px 28px', lineHeight: 1.6 }}>
          <p style={{ fontWeight: 700, fontSize: 16, marginTop: 0 }}>{offer.subject}</p>
          {items}
          <p style={{ marginTop: 20, marginBottom: 0 }}>Yours sincerely,</p>
          <p style={{ margin: '4px 0 0' }}><b>{offer.signatoryName ?? 'Authorised signatory'}</b>{offer.signatoryTitle && <><br />{offer.signatoryTitle}</>}<br />{offer.company}</p>
        </div>
        <div className="toolbar" style={{ marginTop: 12, flexWrap: 'wrap' }}>
          <a className="btn" href={`/api/payroll/offers/${encodeURIComponent(token)}/pdf`} target="_blank" rel="noreferrer">Download PDF</a>
          <span style={{ flex: 1 }} />
          {offer.status === 'Sent' && !mode && <><button className="btn" onClick={() => setMode('decline')}>Decline</button><button className="btn primary" onClick={() => setMode('accept')}>Accept offer</button></>}
        </div>
        {mode === 'accept' && <div className="card" style={{ marginTop: 12 }}>
          <h3 style={{ marginTop: 0 }}>Accept the offer</h3>
          <p className="small">Typing your name below is your electronic signature. We record the date, time and network address with your acceptance.</p>
          <Field label="Your full name"><input value={name} onChange={e => setName(e.target.value)} autoComplete="name" placeholder={offer.candidate} /></Field>
          <label className="field inline"><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} /> I have read the letter and accept the offer of employment as {offer.jobTitle} on these terms.</label>
          <div className="toolbar"><button className="btn" onClick={() => setMode(null)}>Back</button><button className="btn primary" disabled={busy || !confirmed || name.trim().length < 3} onClick={() => respond(true)}>{busy ? 'Sending…' : 'Accept offer'}</button></div>
        </div>}
        {mode === 'decline' && <div className="card" style={{ marginTop: 12 }}>
          <h3 style={{ marginTop: 0 }}>Decline the offer</h3>
          <Field label="Reason (optional, helps us improve)"><textarea rows={3} value={reason} onChange={e => setReason(e.target.value)} /></Field>
          <div className="toolbar"><button className="btn" onClick={() => setMode(null)}>Back</button><button className="btn danger" disabled={busy} onClick={() => { if (confirm('Decline this offer? This cannot be undone.')) respond(false) }}>{busy ? 'Sending…' : 'Decline offer'}</button></div>
        </div>}
      </div>
    </div>
  )
}
