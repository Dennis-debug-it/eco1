import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api, fmt } from '../lib/api'
import type { Dashboard } from '../lib/types'
import { Badge, BarChart, Empty, ErrorBox, Loading, PageHead, Stat } from '../components/ui'

export default function DashboardPage() {
  const q = useQuery({ queryKey: ['dashboard'], queryFn: () => api.get<Dashboard>('/api/payroll/dashboard') })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const d = q.data!
  const statusOrder = ['Draft', 'Prepared', 'Calculated', 'UnderReview', 'Approved', 'PaymentProcessing', 'Paid', 'Finalised', 'Rejected', 'Reversed']
  const byStatus = statusOrder.map(s => ({ s, n: d.runsByStatus.find(x => x.status === s)?.count ?? 0 })).filter(x => x.n > 0)

  return (
    <>
      <PageHead title="Dashboard" sub="Latest approved payroll and workforce overview" />
      <div className="grid c4">
        <Stat label="Total employees" value={fmt.int(d.totalEmployees)} hint={`${fmt.int(d.activeEmployees)} active`} />
        <Stat label="Current gross payroll" value={fmt.money(d.currentGross)} />
        <Stat label="Net payroll" value={fmt.money(d.currentNet)} />
        <Stat label="Statutory liabilities" value={fmt.money(d.statutoryLiabilities)} hint={`Employer contributions ${fmt.money(d.employerContributions)}`} />
      </div>

      <div className="grid c2" style={{ marginTop: 16 }}>
        <div className="card">
          <h2>Payroll processing status</h2>
          {byStatus.length === 0 ? <Empty>No payroll runs yet. <Link to="/payroll">Create the first run.</Link></Empty> : (
            <table><tbody>{byStatus.map(x => <tr key={x.s}><td><Badge status={x.s} /></td><td className="num">{x.n}</td></tr>)}</tbody></table>
          )}
          {d.openExceptions > 0 && <div className="alert warn" style={{ marginTop: 12 }}>{d.openExceptions} open validation exception(s) across unfinalised runs.</div>}
        </div>
        <div className="card">
          <h2>Pending approvals</h2>
          {d.pendingApprovals.length === 0 ? <Empty>Nothing waiting for review or approval.</Empty> : (
            <table><thead><tr><th>Run</th><th>Status</th><th className="num">Employees</th><th className="num">Net</th><th>Submitted</th></tr></thead>
              <tbody>{d.pendingApprovals.map(p => <tr key={p.runId}><td><Link to={`/payroll/${p.runId}`}>{p.reference}</Link></td><td><Badge status={p.status} /></td><td className="num">{p.employees}</td><td className="num">{fmt.num(p.net)}</td><td>{fmt.dateTime(p.submittedAt)}</td></tr>)}</tbody></table>
          )}
        </div>
        <div className="card">
          <h2>Payroll cost trend (employer cost, finalised runs)</h2>
          <BarChart data={d.trend.map(t => ({ label: t.period, value: t.employerCost }))} format={v => (v / 1000).toFixed(0) + 'k'} />
        </div>
        <div className="card">
          <h2>Cost by department (latest run)</h2>
          {d.byDepartment.length === 0 ? <Empty>No finalised payroll yet.</Empty> : (
            <table><thead><tr><th>Department</th><th className="num">Employees</th><th className="num">Gross</th><th className="num">Employer cost</th><th style={{ width: '30%' }}>Share</th></tr></thead>
              <tbody>{d.byDepartment.map(b => {
                const share = d.byDepartment.reduce((s, x) => s + x.employerCost, 0)
                return <tr key={b.name}><td>{b.name}</td><td className="num">{b.employees}</td><td className="num">{fmt.num(b.gross)}</td><td className="num">{fmt.num(b.employerCost)}</td><td><div className="bar" aria-label={fmt.pct(b.employerCost / share * 100)}><i style={{ width: `${(b.employerCost / share) * 100}%` }} /></div></td></tr>
              })}</tbody></table>
          )}
        </div>
        <div className="card">
          <h2>Cost by branch (latest run)</h2>
          {d.byBranch.length === 0 ? <Empty>No finalised payroll yet.</Empty> : (
            <table><thead><tr><th>Branch</th><th className="num">Employees</th><th className="num">Net</th><th className="num">Employer cost</th></tr></thead>
              <tbody>{d.byBranch.map(b => <tr key={b.name}><td>{b.name}</td><td className="num">{b.employees}</td><td className="num">{fmt.num(b.net)}</td><td className="num">{fmt.num(b.employerCost)}</td></tr>)}</tbody></table>
          )}
        </div>
      </div>
    </>
  )
}
