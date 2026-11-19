import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api, fmt } from '../lib/api'
import { BarChart, ErrorBox, Loading, PageHead, Stat } from '../components/ui'

type Analytics = {
  headcount: number; trend: { month: string; headcount: number; hires: number; leavers: number }[]; turnoverPercent: number; leaversLast12Months: number; absenteeismPercent: number; trainingHoursPerEmployee: number; employerCostPerHead?: number
  byDepartment: { department: string; headcount: number }[]; byGender: { gender: string; count: number }[]; byEmploymentType: { type: string; count: number }[]; tenure: { band: string; count: number }[]
  ratings?: { cycle: string; distribution: { rating: number; count: number }[] }
  open: { requisitions: number; onboardingTasks: number; exits: number; cases?: number; pendingLeave: number; expenseClaims: number }
}

function Dist({ title, rows }: { title: string; rows: { label: string; value: number }[] }) {
  const total = rows.reduce((s, r) => s + r.value, 0) || 1
  return <div className="card"><h2>{title}</h2><table><tbody>{rows.map(r => <tr key={r.label}><td>{r.label}</td><td className="num">{r.value}</td><td style={{ width: '45%' }}><div className="bar" role="img" aria-label={`${Math.round(r.value / total * 100)}%`}><i style={{ width: `${r.value / total * 100}%` }} /></div></td></tr>)}</tbody></table></div>
}

export default function HrAnalyticsPage() {
  const q = useQuery({ queryKey: ['hr-analytics'], queryFn: () => api.get<Analytics>('/api/payroll/hr-analytics') })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const a = q.data!
  return (
    <>
      <PageHead title="HR analytics" sub="Workforce, turnover, absence, learning and performance at a glance." />
      <div className="grid c4">
        <Stat label="Headcount" value={fmt.int(a.headcount)} hint={`${a.trend.at(-1)?.hires ?? 0} hires · ${a.trend.at(-1)?.leavers ?? 0} leavers this month`} />
        <Stat label="Turnover (12 months)" value={`${a.turnoverPercent}%`} hint={`${a.leaversLast12Months} leavers`} />
        <Stat label="Absenteeism" value={`${a.absenteeismPercent}%`} hint="Sick and unpaid days, last 12 months" />
        <Stat label="Training hours / employee" value={a.trainingHoursPerEmployee} hint={a.employerCostPerHead ? `Employer cost per head ${fmt.money(a.employerCostPerHead)}` : 'This year'} />
      </div>
      <div className="grid c2" style={{ marginTop: 16 }}>
        <div className="card"><h2>Headcount, last 12 months</h2><BarChart data={a.trend.map(t => ({ label: t.month.slice(5), value: t.headcount }))} /></div>
        <div className="card"><h2>Open items</h2><table><tbody>
          <tr><td><Link to="/recruitment">Open requisitions</Link></td><td className="num">{a.open.requisitions}</td></tr>
          <tr><td><Link to="/recruitment">Onboarding tasks outstanding</Link></td><td className="num">{a.open.onboardingTasks}</td></tr>
          <tr><td><Link to="/exits">Exits in progress</Link></td><td className="num">{a.open.exits}</td></tr>
          <tr><td><Link to="/leave">Leave requests pending</Link></td><td className="num">{a.open.pendingLeave}</td></tr>
          <tr><td><Link to="/expenses">Expense claims awaiting approval</Link></td><td className="num">{a.open.expenseClaims}</td></tr>
          {a.open.cases != null && <tr><td><Link to="/cases">Open disciplinary/grievance cases</Link></td><td className="num">{a.open.cases}</td></tr>}
        </tbody></table></div>
        <Dist title="By department" rows={a.byDepartment.map(d => ({ label: d.department, value: d.headcount }))} />
        <Dist title="Length of service" rows={a.tenure.map(t => ({ label: t.band, value: t.count }))} />
        <Dist title="Employment type" rows={a.byEmploymentType.map(t => ({ label: t.type, value: t.count }))} />
        <Dist title="Gender" rows={a.byGender.map(g => ({ label: g.gender, value: g.count }))} />
        {a.ratings && <Dist title={`Ratings — ${a.ratings.cycle}`} rows={a.ratings.distribution.slice().reverse().map(r => ({ label: '★'.repeat(r.rating), value: r.count }))} />}
        <div className="card"><h2>Hires and leavers</h2><table><thead><tr><th>Month</th><th className="num">Hires</th><th className="num">Leavers</th><th className="num">Headcount</th></tr></thead><tbody>{a.trend.slice(-6).map(t => <tr key={t.month}><td>{t.month}</td><td className="num">{t.hires}</td><td className="num">{t.leavers}</td><td className="num">{t.headcount}</td></tr>)}</tbody></table></div>
      </div>
    </>
  )
}
