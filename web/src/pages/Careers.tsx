import { useEffect, useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { fmt } from '../lib/api'
import { Field, Loading } from '../components/ui'

/** A job as the public API returns it. Salary is never part of it. */
type PublicJob = { id: string; title: string; department?: string; location?: string; employmentType: string; description?: string; closingDate?: string; openings: number; postedAt?: string }
type Listing = { company: string; jobs: PublicJob[] }
type JobPage = { company: string; job: PublicJob; retentionMonths: number; consentRetentionMonths: number }

const base = (slug: string) => `/api/payroll/public/careers/${encodeURIComponent(slug)}`

/** Plain fetch: the careers page is public and never sends a sign-in token. */
async function getJson<T>(url: string): Promise<T | null> {
  const res = await fetch(url, { headers: { Accept: 'application/json' } })
  if (res.status === 404) return null
  if (!res.ok) throw new Error('The careers page could not be loaded. Try again in a moment.')
  return res.json() as Promise<T>
}

export default function CareersPage() {
  const { slug = '', jobId } = useParams()
  return jobId ? <JobDetail slug={slug} jobId={jobId} /> : <JobList slug={slug} />
}

function Shell({ company, children }: { company?: string; children: React.ReactNode }) {
  useEffect(() => { document.title = company ? `Careers at ${company}` : 'Careers' }, [company])
  return (
    <div className="careers">
      <header className="careers-head"><div className="careers-wrap"><span className="careers-kicker">Careers</span><h1>{company ?? 'Careers'}</h1></div></header>
      <main className="careers-wrap">{children}</main>
    </div>
  )
}

function Meta({ job }: { job: PublicJob }) {
  const bits = [job.department, job.location, job.employmentType, job.openings > 1 ? `${job.openings} openings` : null].filter(Boolean)
  return <p className="careers-meta">{bits.join(' · ')}{job.closingDate && <> · Apply by <b>{fmt.date(job.closingDate)}</b></>}</p>
}

function NothingOpen() {
  return <div className="card careers-empty"><h2>No open positions right now</h2><p className="muted">This company isn’t advertising any jobs on this page at the moment. Check back later.</p></div>
}

function JobList({ slug }: { slug: string }) {
  const q = useQuery({ queryKey: ['careers', slug], queryFn: () => getJson<Listing>(base(slug)), retry: false })
  if (q.isLoading) return <Shell><Loading what="Loading jobs" /></Shell>
  if (q.error) return <Shell><div className="alert danger">{(q.error as Error).message}</div></Shell>
  if (!q.data) return <Shell><NothingOpen /></Shell>
  return (
    <Shell company={q.data.company}>
      <p className="careers-intro">{q.data.jobs.length === 1 ? 'One open position' : `${q.data.jobs.length} open positions`}</p>
      <ul className="careers-list">
        {q.data.jobs.map(j => (
          <li key={j.id} className="card">
            <h2><Link to={`/careers/${slug}/jobs/${j.id}`}>{j.title}</Link></h2>
            <Meta job={j} />
            {j.description && <p className="careers-snippet">{j.description.length > 220 ? `${j.description.slice(0, 220).trimEnd()}…` : j.description}</p>}
            <Link className="btn" to={`/careers/${slug}/jobs/${j.id}`}>View and apply</Link>
          </li>
        ))}
      </ul>
    </Shell>
  )
}

function JobDetail({ slug, jobId }: { slug: string; jobId: string }) {
  const q = useQuery({ queryKey: ['careers', slug, jobId], queryFn: () => getJson<JobPage>(`${base(slug)}/jobs/${jobId}`), retry: false })
  if (q.isLoading) return <Shell><Loading what="Loading job" /></Shell>
  if (q.error) return <Shell><div className="alert danger">{(q.error as Error).message}</div></Shell>
  if (!q.data) return <Shell><div className="card careers-empty"><h2>This job is no longer open</h2><p className="muted">It may have closed or been filled.</p><Link className="btn" to={`/careers/${slug}`}>See open positions</Link></div></Shell>
  const { company, job } = q.data
  return (
    <Shell company={company}>
      <p><Link to={`/careers/${slug}`}>← All open positions</Link></p>
      <div className="careers-job">
        <article className="card">
          <h2 className="careers-title">{job.title}</h2>
          <Meta job={job} />
          <div className="careers-description">{job.description}</div>
        </article>
        <ApplyForm slug={slug} page={q.data} />
      </div>
    </Shell>
  )
}

function ApplyForm({ slug, page }: { slug: string; page: JobPage }) {
  const [f, setF] = useState({ firstName: '', lastName: '', email: '', phone: '', coverNote: '', privacyConsent: false, retainConsent: false, website: '' })
  const [cv, setCv] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const set = (k: keyof typeof f, v: string | boolean) => setF(x => ({ ...x, [k]: v }))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (cv && cv.size > 5 * 1024 * 1024) { setError('Your CV must be 5 MB or smaller.'); return }
    setBusy(true)
    try {
      const form = new FormData()
      Object.entries(f).forEach(([k, v]) => form.append(k, String(v)))
      if (cv) form.append('cv', cv)
      const res = await fetch(`${base(slug)}/jobs/${page.job.id}/apply`, { method: 'POST', body: form })
      if (res.ok) { setDone(true); return }
      const body = await res.json().catch(() => null) as { detail?: string } | null
      setError(res.status === 404 ? 'This job has just closed, so we can’t accept new applications.' : body?.detail ?? 'Your application could not be sent. Try again.')
    } catch {
      setError('Your application could not be sent. Check your connection and try again.')
    } finally { setBusy(false) }
  }

  if (done) return (
    <aside className="card careers-apply" aria-live="polite">
      <h2>Application sent</h2>
      <p>Thank you for applying for <b>{page.job.title}</b>. We’ve emailed a confirmation to <b>{f.email}</b>.</p>
      <p className="muted small">If your experience matches what we need, someone from {page.company} will contact you.</p>
      <Link className="btn" to={`/careers/${slug}`}>See other open positions</Link>
    </aside>
  )

  return (
    <aside className="card careers-apply">
      <h2>Apply for this job</h2>
      <form onSubmit={submit} noValidate>
        {error && <div className="alert danger" role="alert">{error}</div>}
        <div className="form-grid">
          <Field label="First name *"><input required autoComplete="given-name" value={f.firstName} onChange={e => set('firstName', e.target.value)} /></Field>
          <Field label="Last name *"><input required autoComplete="family-name" value={f.lastName} onChange={e => set('lastName', e.target.value)} /></Field>
        </div>
        <Field label="Email *"><input required type="email" autoComplete="email" value={f.email} onChange={e => set('email', e.target.value)} /></Field>
        <Field label="Phone"><input type="tel" autoComplete="tel" placeholder="07XX XXX XXX" value={f.phone} onChange={e => set('phone', e.target.value)} /></Field>
        <Field label="CV * (PDF or Word, up to 5 MB)"><input required type="file" accept=".pdf,.docx,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document" onChange={e => setCv(e.target.files?.[0] ?? null)} /></Field>
        <Field label="Why you’re a good fit (optional)"><textarea rows={4} maxLength={3000} value={f.coverNote} onChange={e => set('coverNote', e.target.value)} /></Field>
        {/* Hidden from people; bots that fill it are ignored. */}
        <div className="careers-trap" aria-hidden="true"><label>Website<input tabIndex={-1} autoComplete="off" value={f.website} onChange={e => set('website', e.target.value)} /></label></div>
        <div className="careers-privacy small">
          <p>{page.company} uses the details and CV you send only to consider you for this job. If you’re not hired, they’re deleted after {page.retentionMonths} months, as the Data Protection Act 2019 requires.</p>
          <label className="field inline"><input type="checkbox" checked={f.privacyConsent} onChange={e => set('privacyConsent', e.target.checked)} /> I agree to {page.company} using my information this way *</label>
          <label className="field inline"><input type="checkbox" checked={f.retainConsent} onChange={e => set('retainConsent', e.target.checked)} /> Keep my application for {page.consentRetentionMonths} months and consider me for other jobs (optional)</label>
        </div>
        <button className="btn primary careers-submit" type="submit" disabled={busy || !f.privacyConsent}>{busy ? 'Sending…' : 'Send application'}</button>
      </form>
    </aside>
  )
}
