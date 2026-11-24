import { Fragment, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt } from '../lib/api'
import { useAuth } from '../lib/auth'
import { Feature, usePlanFeature } from '../lib/billing'
import { useLegalEntities } from '../lib/lookups'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHead, Tabs } from '../components/ui'

type Option = { code: string; description: string }
type Options = { permissions: Option[]; events: Option[]; maxWebhooks: number }
type ApiKey = {
  id: string; name: string; keyPrefix: string; permissions: string[]; legalEntityIds: string[]; requestsPerMinute: number; createdAt: string; createdByName?: string
  expiresAt?: string; revokedAt?: string; revokedByName?: string; lastUsedAt?: string; lastUsedIp?: string; state: 'Active' | 'Expired' | 'Revoked'
}
type Webhook = { id: string; name: string; url: string; events: string[]; isActive: boolean; disabledReason?: string; consecutiveFailures: number; lastSuccessAt?: string; lastFailureAt?: string; createdAt: string }
type Delivery = { id: string; eventType: string; status: 'Pending' | 'Delivered' | 'Failed'; attempts: number; lastStatusCode?: number; lastError?: string; createdAt: string; deliveredAt?: string; nextAttemptAt?: string; durationMs?: number; payload: string }

const stateBadge: Record<string, string> = { Active: 'Approved', Expired: 'Warning', Revoked: 'Rejected', Delivered: 'Approved', Pending: 'Warning', Failed: 'Rejected' }

export default function IntegrationsPage() {
  const inPlan = usePlanFeature(Feature.Api)
  const [tab, setTab] = useState<'keys' | 'webhooks' | 'guide'>('keys')
  const options = useQuery({ queryKey: ['integration-options'], queryFn: () => api.get<Options>('/api/payroll/integrations/options') })
  return (
    <>
      <PageHead title="Integrations & API" sub="Connect time and attendance, HR, ERP and accounting systems: API keys for programs, and webhooks that tell them when things change." />
      {!inPlan && <div className="alert warn" role="status">The API and webhooks aren't part of your plan. An administrator can upgrade under Billing; existing keys stop working until then.</div>}
      <Tabs tabs={[{ id: 'keys', label: 'API keys' }, { id: 'webhooks', label: 'Webhooks' }, { id: 'guide', label: 'Developer guide' }]} value={tab} onChange={setTab} />
      {!options.data ? <div className="card">{options.error ? <ErrorBox error={options.error} /> : <Loading />}</div>
        : tab === 'keys' ? <Keys options={options.data} /> : tab === 'webhooks' ? <Webhooks options={options.data} /> : <Guide />}
    </>
  )
}

/** A secret shown once, with a copy button. */
function OnceSecret({ label, value, note }: { label: string; value: string; note: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="alert ok" role="status">
      <b>{label}</b> {note}
      <div className="toolbar" style={{ marginTop: 8, marginBottom: 0 }}>
        <code style={{ flex: 1, padding: '6px 8px', background: 'var(--surface)', color: 'var(--text)', border: '1px solid var(--border)', borderRadius: 6, wordBreak: 'break-all', userSelect: 'all' }}>{value}</code>
        <button className="btn sm" onClick={() => navigator.clipboard?.writeText(value).then(() => setCopied(true))}>{copied ? 'Copied' : 'Copy'}</button>
      </div>
    </div>
  )
}

function Keys({ options }: { options: Options }) {
  const qc = useQueryClient()
  const les = useLegalEntities()
  const q = useQuery({ queryKey: ['api-keys'], queryFn: () => api.get<ApiKey[]>('/api/payroll/integrations/keys') })
  const revoke = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/integrations/keys/${id}/revoke`), onSuccess: () => qc.invalidateQueries({ queryKey: ['api-keys'] }) })
  const [adding, setAdding] = useState(false)
  const [created, setCreated] = useState<{ name: string; key: string } | null>(null)
  const company = (id: string) => les.data?.find(l => l.id === id)?.name ?? id.slice(0, 8)
  return (
    <div className="card">
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <h2 style={{ margin: 0 }}>API keys</h2>
        <button className="btn primary" onClick={() => { setCreated(null); setAdding(true) }}>New API key</button>
      </div>
      <p className="small muted">Each key is for one system. It can do only what you tick, for the companies you choose, and never approve, finalise or reverse payroll. Revoke a key as soon as the system no longer needs it.</p>
      {created && <OnceSecret label={`Key for ${created.name}:`} value={created.key} note="Copy it into the other system now. It won't be shown again; if it's lost, revoke it and make a new one." />}
      <ErrorBox error={q.error ?? revoke.error} />
      {!q.data ? <Loading /> : q.data.length === 0 ? <Empty>No API keys yet.</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Name</th><th>Key</th><th>May</th><th>Companies</th><th>Last used</th><th>Status</th><th /></tr></thead>
          <tbody>{q.data.map(k => (
            <tr key={k.id}>
              <td><b>{k.name}</b><div className="small muted">by {k.createdByName ?? '—'}, {fmt.date(k.createdAt)}{k.expiresAt ? ` · expires ${fmt.date(k.expiresAt)}` : ''}</div></td>
              <td><code>{k.keyPrefix}…</code><div className="small muted">{k.requestsPerMinute}/min</div></td>
              <td className="small" style={{ whiteSpace: 'normal', minWidth: 180 }}>{k.permissions.join(', ')}</td>
              <td className="small" style={{ whiteSpace: 'normal' }}>{k.legalEntityIds.length === 0 ? 'All' : k.legalEntityIds.map(company).join(', ')}</td>
              <td className="small">{k.lastUsedAt ? <>{fmt.date(k.lastUsedAt)}<div className="muted">{k.lastUsedIp}</div></> : 'Never'}</td>
              <td><Badge status={stateBadge[k.state]}>{k.state}</Badge>{k.revokedByName && <div className="small muted">by {k.revokedByName}</div>}</td>
              <td className="actions">{k.state === 'Active' && <button className="btn sm danger" disabled={revoke.isPending} onClick={() => { if (confirm(`Revoke "${k.name}"? The system using it stops working straight away.`)) revoke.mutate(k.id) }}>Revoke</button>}</td>
            </tr>))}</tbody>
        </table></div>)}
      {adding && <NewKey options={options} onClose={() => setAdding(false)} onCreated={(name, key) => { setCreated({ name, key }); setAdding(false); qc.invalidateQueries({ queryKey: ['api-keys'] }) }} />}
    </div>
  )
}

function NewKey({ options, onClose, onCreated }: { options: Options; onClose: () => void; onCreated: (name: string, key: string) => void }) {
  const { can } = useAuth()
  const les = useLegalEntities()
  const [name, setName] = useState('')
  const [perms, setPerms] = useState<string[]>(['employees.read'])
  const [companies, setCompanies] = useState<string[]>([])
  const [expires, setExpires] = useState('')
  const [rpm, setRpm] = useState(120)
  const m = useMutation({
    mutationFn: () => api.post<{ key: string }>('/api/payroll/integrations/keys', { name, permissions: perms, legalEntityIds: companies, expiresOn: expires || null, requestsPerMinute: rpm }),
    onSuccess: r => onCreated(name, r.key),
  })
  const toggle = (list: string[], v: string) => list.includes(v) ? list.filter(x => x !== v) : [...list, v]
  return (
    <Modal title="New API key" onClose={onClose} width={560}>
      <Field label="Name (the system that will use it)"><input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. ZKTeco clock, Odoo ERP" autoFocus /></Field>
      <fieldset style={{ border: 0, padding: 0, margin: '8px 0' }}>
        <legend className="small" style={{ fontWeight: 600 }}>What it may do</legend>
        {options.permissions.map(p => (
          <label key={p.code} className="small" style={{ display: 'block', margin: '4px 0', opacity: can(p.code) ? 1 : .5 }}>
            <input type="checkbox" disabled={!can(p.code)} checked={perms.includes(p.code)} onChange={() => setPerms(toggle(perms, p.code))} /> <b>{p.code}</b>: {p.description}{!can(p.code) && ' (you don\'t have this yourself)'}
          </label>))}
      </fieldset>
      <fieldset style={{ border: 0, padding: 0, margin: '8px 0' }}>
        <legend className="small" style={{ fontWeight: 600 }}>Companies</legend>
        <label className="small" style={{ display: 'block', margin: '4px 0' }}><input type="checkbox" checked={companies.length === 0} onChange={() => setCompanies([])} /> All companies, including ones added later</label>
        {(les.data ?? []).map(l => (
          <label key={l.id} className="small" style={{ display: 'block', margin: '4px 0' }}><input type="checkbox" checked={companies.includes(l.id)} onChange={() => setCompanies(toggle(companies, l.id))} /> {l.name}</label>))}
      </fieldset>
      <div className="grid c2">
        <Field label="Expires (optional)"><input type="date" value={expires} onChange={e => setExpires(e.target.value)} /></Field>
        <Field label="Requests per minute"><input type="number" min={10} max={1200} value={rpm} onChange={e => setRpm(Number(e.target.value))} /></Field>
      </div>
      <ErrorBox error={m.error} />
      <div className="actions" style={{ justifyContent: 'flex-end', marginTop: 12 }}>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn primary" disabled={!name.trim() || perms.length === 0 || m.isPending} onClick={() => m.mutate()}>Create key</button>
      </div>
    </Modal>
  )
}

function Webhooks({ options }: { options: Options }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['webhooks'], queryFn: () => api.get<Webhook[]>('/api/payroll/integrations/webhooks') })
  const refresh = () => qc.invalidateQueries({ queryKey: ['webhooks'] })
  const [editing, setEditing] = useState<Webhook | 'new' | null>(null)
  const [secret, setSecret] = useState<{ name: string; secret: string } | null>(null)
  const [log, setLog] = useState<Webhook | null>(null)
  const toggle = useMutation({ mutationFn: (w: Webhook) => api.put(`/api/payroll/integrations/webhooks/${w.id}`, { ...w, isActive: !w.isActive }), onSuccess: refresh })
  const rotate = useMutation({ mutationFn: (w: Webhook) => api.post<{ secret: string }>(`/api/payroll/integrations/webhooks/${w.id}/rotate-secret`), onSuccess: (r, w) => setSecret({ name: w.name, secret: r.secret }) })
  const test = useMutation({ mutationFn: (w: Webhook) => api.post(`/api/payroll/integrations/webhooks/${w.id}/test`), onSuccess: (_, w) => setLog(w) })
  const remove = useMutation({ mutationFn: (w: Webhook) => api.del(`/api/payroll/integrations/webhooks/${w.id}`), onSuccess: refresh })
  const eventLabel = (codes: string[]) => codes.length === 0 ? 'All events' : codes.join(', ')
  return (
    <div className="card">
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <h2 style={{ margin: 0 }}>Webhooks</h2>
        <button className="btn primary" disabled={(q.data?.length ?? 0) >= options.maxWebhooks} onClick={() => { setSecret(null); setEditing('new') }}>Add webhook</button>
      </div>
      <p className="small muted">Ecosys posts a signed message to these HTTPS addresses when something happens. Messages name the record and link to it in the API; they never carry pay figures. Failed deliveries are retried for about eleven hours, and a webhook that keeps failing is switched off and administrators are emailed.</p>
      {secret && <OnceSecret label={`Signing secret for ${secret.name}:`} value={secret.secret} note="The receiving system uses it to check each message came from Ecosys. It won't be shown again." />}
      <ErrorBox error={q.error ?? toggle.error ?? rotate.error ?? test.error ?? remove.error} />
      {!q.data ? <Loading /> : q.data.length === 0 ? <Empty>No webhooks yet.</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>Name</th><th>Address</th><th>Events</th><th>Last delivered</th><th>Status</th><th /></tr></thead>
          <tbody>{q.data.map(w => (
            <tr key={w.id}>
              <td><b>{w.name}</b></td>
              <td className="small" style={{ whiteSpace: 'normal', wordBreak: 'break-all', maxWidth: 260 }}>{w.url}</td>
              <td className="small" style={{ whiteSpace: 'normal', minWidth: 160 }}>{eventLabel(w.events)}</td>
              <td className="small">{w.lastSuccessAt ? fmt.date(w.lastSuccessAt) : '—'}{w.lastFailureAt && <div className="muted">last failure {fmt.date(w.lastFailureAt)}</div>}</td>
              <td>{w.isActive ? <Badge status="Approved">On</Badge> : <Badge status="Rejected">Off</Badge>}{w.disabledReason && <div className="small muted" style={{ whiteSpace: 'normal', maxWidth: 240 }}>{w.disabledReason}</div>}</td>
              <td className="actions">
                <button className="btn sm" onClick={() => setLog(w)}>Deliveries</button>
                <button className="btn sm" disabled={!w.isActive || test.isPending} onClick={() => test.mutate(w)}>Send test</button>
                <button className="btn sm" onClick={() => { setSecret(null); setEditing(w) }}>Edit</button>
                <button className="btn sm" disabled={toggle.isPending} onClick={() => toggle.mutate(w)}>{w.isActive ? 'Switch off' : 'Switch on'}</button>
                <button className="btn sm" onClick={() => { if (confirm('Make a new signing secret? The receiving system must be updated with it straight away.')) rotate.mutate(w) }}>New secret</button>
                <button className="btn sm danger" onClick={() => { if (confirm(`Remove "${w.name}"? Nothing more is sent to it.`)) remove.mutate(w) }}>Remove</button>
              </td>
            </tr>))}</tbody>
        </table></div>)}
      {editing && <WebhookForm options={options} webhook={editing === 'new' ? null : editing} onClose={() => setEditing(null)}
        onSaved={s => { setEditing(null); if (s) setSecret(s); refresh() }} />}
      {log && <DeliveryLog webhook={log} onClose={() => setLog(null)} />}
    </div>
  )
}

function WebhookForm({ options, webhook, onClose, onSaved }: { options: Options; webhook: Webhook | null; onClose: () => void; onSaved: (secret?: { name: string; secret: string }) => void }) {
  const [name, setName] = useState(webhook?.name ?? '')
  const [url, setUrl] = useState(webhook?.url ?? 'https://')
  const [events, setEvents] = useState<string[]>(webhook?.events ?? [])
  const m = useMutation({
    mutationFn: async () => {
      if (webhook) { await api.put(`/api/payroll/integrations/webhooks/${webhook.id}`, { name, url, events, isActive: webhook.isActive }); return undefined }
      const r = await api.post<{ secret: string }>('/api/payroll/integrations/webhooks', { name, url, events })
      return { name, secret: r.secret }
    },
    onSuccess: onSaved,
  })
  return (
    <Modal title={webhook ? `Edit ${webhook.name}` : 'Add webhook'} onClose={onClose} width={560}>
      <Field label="Name"><input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. HR system sync" autoFocus /></Field>
      <Field label="HTTPS address that receives the messages"><input value={url} onChange={e => setUrl(e.target.value)} placeholder="https://erp.example.co.ke/hooks/ecosys" /></Field>
      <fieldset style={{ border: 0, padding: 0, margin: '8px 0' }}>
        <legend className="small" style={{ fontWeight: 600 }}>Events (none ticked sends all of them, including ones added later)</legend>
        {options.events.map(e => (
          <label key={e.code} className="small" style={{ display: 'block', margin: '4px 0' }}>
            <input type="checkbox" checked={events.includes(e.code)} onChange={() => setEvents(events.includes(e.code) ? events.filter(x => x !== e.code) : [...events, e.code])} /> <b>{e.code}</b>: {e.description}
          </label>))}
      </fieldset>
      <ErrorBox error={m.error} />
      <div className="actions" style={{ justifyContent: 'flex-end', marginTop: 12 }}>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn primary" disabled={!name.trim() || !url.trim() || m.isPending} onClick={() => m.mutate()}>{webhook ? 'Save' : 'Add webhook'}</button>
      </div>
    </Modal>
  )
}

function DeliveryLog({ webhook, onClose }: { webhook: Webhook; onClose: () => void }) {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['deliveries', webhook.id], queryFn: () => api.get<Delivery[]>(`/api/payroll/integrations/webhooks/${webhook.id}/deliveries`), refetchInterval: 5000 })
  const resend = useMutation({ mutationFn: (id: string) => api.post(`/api/payroll/integrations/deliveries/${id}/redeliver`), onSuccess: () => qc.invalidateQueries({ queryKey: ['deliveries', webhook.id] }) })
  const [open, setOpen] = useState<string | null>(null)
  return (
    <Modal title={`Deliveries to ${webhook.name}`} onClose={onClose} width={860}>
      <p className="small muted">The latest 50, newest first. This list refreshes every few seconds.</p>
      <ErrorBox error={q.error ?? resend.error} />
      {!q.data ? <Loading /> : q.data.length === 0 ? <Empty>Nothing sent yet.</Empty> : (
        <div className="table-wrap"><table>
          <thead><tr><th>When</th><th>Event</th><th>Status</th><th>Tries</th><th>Answer</th><th /></tr></thead>
          <tbody>{q.data.map(d => (<Fragment key={d.id}>
            <tr>
              <td className="small">{fmt.date(d.createdAt)} {new Date(d.createdAt).toLocaleTimeString('en-KE', { hour: '2-digit', minute: '2-digit' })}</td>
              <td className="small"><code>{d.eventType}</code></td>
              <td><Badge status={stateBadge[d.status]}>{d.status}</Badge>{d.status === 'Pending' && d.nextAttemptAt && d.attempts > 0 && <div className="small muted">retry {new Date(d.nextAttemptAt).toLocaleTimeString('en-KE', { hour: '2-digit', minute: '2-digit' })}</div>}</td>
              <td className="small">{d.attempts}</td>
              <td className="small" style={{ whiteSpace: 'normal', maxWidth: 260 }}>{d.lastStatusCode ?? ''} {d.lastError ?? (d.durationMs != null ? `${d.durationMs} ms` : '')}</td>
              <td className="actions">
                <button className="btn sm" onClick={() => setOpen(open === d.id ? null : d.id)}>{open === d.id ? 'Hide' : 'Body'}</button>
                {d.status !== 'Pending' && webhook.isActive && <button className="btn sm" disabled={resend.isPending} onClick={() => resend.mutate(d.id)}>Send again</button>}
              </td>
            </tr>
            {open === d.id && <tr><td colSpan={6}><pre className="small" style={{ margin: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{JSON.stringify(JSON.parse(d.payload), null, 2)}</pre></td></tr>}
          </Fragment>))}</tbody>
        </table></div>)}
    </Modal>
  )
}

const nodeVerify = `// Node.js (Express): check every webhook before trusting it
const crypto = require('crypto')
app.post('/hooks/ecosys', express.raw({ type: 'application/json' }), (req, res) => {
  const header = req.get('Ecosys-Signature') || ''          // t=1732435200,v1=5f2b…
  const parts = Object.fromEntries(header.split(',').map(p => p.split('=')))
  const body = req.body.toString('utf8')
  const expected = crypto.createHmac('sha256', process.env.ECOSYS_WEBHOOK_SECRET).update(\`\${parts.t}.\${body}\`).digest('hex')
  const fresh = Math.abs(Date.now() / 1000 - Number(parts.t)) < 300
  if (!fresh || !parts.v1 || parts.v1.length !== expected.length || !crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1))) return res.sendStatus(401)
  const event = JSON.parse(body)   // { id, type, sequence, occurredAt, companyId, data: { object, id, url } }
  // Ignore an event id you have already handled, then fetch data.url with your API key.
  res.sendStatus(200)
})`

const pythonVerify = `# Python (Flask)
import hmac, hashlib, time, os
@app.post("/hooks/ecosys")
def ecosys():
    parts = dict(p.split("=", 1) for p in request.headers.get("Ecosys-Signature", "").split(","))
    body = request.get_data(as_text=True)
    expected = hmac.new(os.environ["ECOSYS_WEBHOOK_SECRET"].encode(), f"{parts['t']}.{body}".encode(), hashlib.sha256).hexdigest()
    if abs(time.time() - int(parts["t"])) > 300 or not hmac.compare_digest(expected, parts.get("v1", "")):
        return "", 401
    event = request.get_json()
    return "", 200`

function Guide() {
  const base = `${window.location.origin}/api/v1`
  const curl = `curl ${base}/employees?limit=50 \\\n  -H "Authorization: Bearer eco_live_…"`
  const attendance = `curl -X PUT ${base}/payroll-runs/{runId}/attendance \\\n  -H "Authorization: Bearer eco_live_…" -H "Content-Type: application/json" \\\n  -H "Idempotency-Key: clock-2026-11-batch-1" \\\n  -d '[{"employeeNumber":"EMP001","daysWorked":21,"absentDays":1,"overtimeHours":6,"restDayOvertimeHours":0}]'`
  const pre = { margin: '6px 0 14px', padding: 10, background: 'var(--surface-2)', border: '1px solid var(--border)', borderRadius: 6, overflowX: 'auto' as const, fontSize: 12 }
  return (
    <div className="card">
      <h2>Using the API</h2>
      <p className="small">The full description, which tools like Postman and code generators can read, is at <a href="/api/v1/openapi.json" target="_blank" rel="noreferrer"><code>{base}/openapi.json</code></a>.</p>
      <h3>Read</h3>
      <pre style={pre}>{curl}</pre>
      <h3>Send attendance from a clock-in system</h3>
      <p className="small">Find the run that is still open (<code>GET /payroll-runs?status=Draft</code>, or Prepared or Calculated), then send each employee's days. The <code>Idempotency-Key</code> makes a retry after a timeout safe.</p>
      <pre style={pre}>{attendance}</pre>
      <h3>What a key can reach</h3>
      <ul className="small">
        <li>Companies, departments, branches, cost centres, payroll periods and pay components</li>
        <li>Employees: list (with <code>updatedSince</code> for syncing), read, add and change</li>
        <li>Payroll runs: list, read, results per employee (with salary.read), attendance and one-off pay items</li>
        <li>Leave requests (dates, type and status), journals and accounting files, the events feed and the audit log</li>
      </ul>
      <p className="small muted">Approving, finalising, reversing and paying payroll stay in Ecosys, with a person who signs in with MFA.</p>
      <h3>Check webhook signatures</h3>
      <pre style={pre}>{nodeVerify}</pre>
      <pre style={pre}>{pythonVerify}</pre>
    </div>
  )
}
