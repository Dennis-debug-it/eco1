import { useState } from 'react'
import { api, fmt } from '../lib/api'
import { ErrorBox, Modal } from './ui'

type Check = { format: string; journals: number; lines: number; debits: number; credits: number; ready: boolean; problems: { journal: string; account?: string; message: string }[]; warnings: string[] }

export const journalFormats: [string, string][] = [
  ['quickbooks-online', 'QuickBooks Online (CSV)'], ['quickbooks-desktop', 'QuickBooks Desktop (IIF)'],
  ['sage-pastel', 'Sage Pastel Partner / Xpress (batch import)'], ['sage-evolution', 'Sage 200 Evolution (CSV)'], ['csv', 'Plain CSV'],
]

/**
 * Exports journals as an import file for the accounting system: one run's journals, or a company's journals between two
 * dates. Checked first; problems are listed instead of a file the accounting system would reject.
 */
export default function JournalExport({ query }: { query: string }) {
  const [format, setFormat] = useState(() => { try { return localStorage.getItem('ecosys.journalFormat') ?? 'quickbooks-online' } catch { return 'quickbooks-online' } })
  const [check, setCheck] = useState<Check | null>(null)
  const [err, setErr] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const go = async () => {
    setErr(null); setBusy(true)
    try { localStorage.setItem('ecosys.journalFormat', format) } catch { /* private mode */ }
    try {
      const c = await api.get<Check>(`/api/payroll/accounting/journals/check?format=${format}&${query}`)
      setCheck(c)
      if (c.ready) await api.download(`/api/payroll/accounting/journals/export?format=${format}&${query}`)
    } catch (e) { setErr(e) } finally { setBusy(false) }
  }
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 0 }}>
        <select value={format} onChange={e => setFormat(e.target.value)} aria-label="Accounting system">{journalFormats.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select>
        <button className="btn" onClick={go} disabled={busy}>{busy ? 'Preparing…' : 'Export journal'}</button>
      </div>
      <ErrorBox error={err} />
      {check && <Modal title={check.ready ? 'Journal exported' : 'Fix these before exporting'} onClose={() => setCheck(null)} width={720}>
        <p className="small">{check.journals} journal(s) · {check.lines} lines · debits {fmt.money(check.debits)} · credits {fmt.money(check.credits)}</p>
        {!check.ready && <div className="table-wrap"><table><thead><tr><th>Journal</th><th>Problem</th></tr></thead>
          <tbody>{check.problems.map((p, i) => <tr key={i}><td className="mono">{p.journal || '—'}</td><td>{p.message}</td></tr>)}</tbody></table></div>}
        {check.warnings.map((w, i) => <div key={i} className="alert warn">{w}</div>)}
        <div className="actions"><button className="btn" onClick={() => setCheck(null)}>Close</button></div>
      </Modal>}
    </>
  )
}
