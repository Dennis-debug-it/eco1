import { useQuery } from '@tanstack/react-query'
import { api, fmt } from '../../lib/api'
import { ErrorBox, Loading, PageHead, Stat } from '../../components/ui'
import type { Overview } from './shared'

export default function OperatorOverviewPage() {
  const q = useQuery({ queryKey: ['platform-overview'], queryFn: () => api.get<Overview>('/api/platform/overview') })
  return (
    <>
      <PageHead title="Platform overview" sub="The SaaS business at a glance. No payroll or employee records are shown in the operator console." />
      <ErrorBox error={q.error} />
      {q.isLoading ? <Loading /> : q.data && <OverviewStats o={q.data} />}
    </>
  )
}

function OverviewStats({ o }: { o: Overview }) {
  const statuses = Object.entries(o.byStatus).map(([k, v]) => `${v} ${k === 'Trialing' ? 'on trial' : k === 'PastDue' ? 'past due' : k.toLowerCase()}`)
  return (
    <>
      {!o.saas && <div className="alert info">SaaS mode is off on this server (Saas:Enabled=false): no sign-ups, billing or plan limits. You can still see organisations here.</div>}
      <div className="grid c4" style={{ marginBottom: 16 }}>
        <Stat label="Organisations" value={fmt.int(o.tenants)} hint={statuses.concat(o.managed ? [`${o.managed} managed`] : []).join(' · ') || 'None yet'} />
        <Stat label="Trials ending this week" value={o.trialsEndingThisWeek} hint={`${o.signupsLast30Days} sign-ups in 30 days`} />
        <Stat label="Employees on the platform" value={fmt.int(o.employeesUnderManagement)} hint="Active, on leave or suspended" />
        {o.mrrExVat != null
          ? <Stat label="Monthly recurring revenue" value={fmt.money(o.mrrExVat, o.currency)} hint={o.openBalance ? `${fmt.money(o.openBalance, o.currency)} outstanding${o.overdueBalance ? `, ${fmt.money(o.overdueBalance, o.currency)} overdue` : ''}` : 'Nothing outstanding'} />
          : <Stat label="Revenue" value="—" hint="Shown to finance and platform administrators" />}
      </div>
    </>
  )
}
