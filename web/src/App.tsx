import { Navigate, NavLink, Route, Routes } from 'react-router-dom'
import { P, useAuth } from './lib/auth'
import { Loading } from './components/ui'
import LoginPage from './pages/Login'
import MfaEnrol from './components/MfaEnrol'
import ChangePassword from './components/ChangePassword'
import DashboardPage from './pages/Dashboard'
import EmployeesPage from './pages/Employees'
import EmployeeDetailPage from './pages/EmployeeDetail'
import OrganisationPage from './pages/Organisation'
import CompensationPage from './pages/Compensation'
import PayrollRunsPage from './pages/PayrollRuns'
import PayrollRunDetailPage from './pages/PayrollRunDetail'
import StatutoryPage from './pages/Statutory'
import LeavePage from './pages/Leave'
import SelfServicePage from './pages/SelfService'
import ReportsPage from './pages/Reports'
import SettingsPage from './pages/Settings'
import PerformancePage from './pages/Performance'
import LearningPage from './pages/Learning'
import ExitsPage from './pages/Exits'
import RecruitmentPage from './pages/Recruitment'
import PeopleRecordsPage from './pages/PeopleRecords'
import CasesPage from './pages/Cases'
import ExpensesPage from './pages/Expenses'
import HrAnalyticsPage from './pages/HrAnalytics'
import DataProtectionPage from './pages/DataProtection'
import { useHrMe } from './lib/lookups'

export default function App() {
  const { user, loading, logout, can } = useAuth()
  const hrMe = useHrMe(!!user && !user.mustChangePassword && (!user.mfaRequired || user.mfaSatisfied))
  if (loading) return <Loading what="Signing in" />
  if (!user) return <LoginPage />
  if (user.mustChangePassword) return <ChangePassword gate />
  if (user.mfaRequired && !user.mfaSatisfied) return user.mfaEnabled
    ? <div className="login"><div className="card"><h2>Sign in again</h2><p className="small">Your session was started without a verification code. Sign out and sign in with your authenticator code.</p><button className="btn primary" onClick={logout}>Sign out</button></div></div>
    : <MfaEnrol gate />

  const employeeOnly = user.roles.length === 1 && user.roles[0] === 'Employee'
  const manager = (hrMe.data?.directReports ?? 0) > 0
  const staff = !!user.employeeId
  const count = (n?: number) => (n ? ` (${n})` : '')
  const nav: { to: string; label: string; show: boolean; section?: string }[] = [
    { to: '/', label: 'Dashboard', show: can(P.ReportsRead) },
    { to: '/self-service', label: 'My workspace', show: staff, section: 'Me' },
    { to: '/employees', label: 'Employees', show: can(P.EmployeesRead), section: 'People' },
    { to: '/organisation', label: 'Organisation', show: can(P.OrganisationManage) || can(P.EmployeesRead) },
    { to: '/people-records', label: 'People records', show: can(P.HrDocuments) || can(P.HrBenefits) || can(P.HrAssets) || can(P.EmployeesRead) },
    { to: '/recruitment', label: 'Recruitment', show: can(P.HrRecruitment) },
    { to: '/exits', label: 'Exits & final dues', show: can(P.HrSeparations) || can(P.PayrollPrepare) || can(P.PayrollApprove) },
    { to: '/leave', label: `Leave${count(hrMe.data?.pendingLeave)}`, show: can(P.LeaveApprove) || manager || staff, section: 'HR' },
    { to: '/performance', label: `Performance${count(hrMe.data?.reviewsToComplete)}`, show: can(P.HrPerformance) || manager || can(P.SalaryRead) },
    { to: '/learning', label: `Learning${count(hrMe.data?.pendingNominations)}`, show: can(P.HrLearning) || manager || staff },
    { to: '/expenses', label: `Expenses${count(hrMe.data?.pendingExpenses)}`, show: can(P.ExpensesFinance) || staff },
    { to: '/cases', label: 'Cases', show: can(P.HrCases) },
    { to: '/hr-analytics', label: 'HR analytics', show: can(P.ReportsRead) },
    { to: '/compensation', label: 'Compensation', show: can(P.CompensationManage) || can(P.SalaryRead), section: 'Payroll' },
    { to: '/payroll', label: 'Payroll processing', show: can(P.SalaryRead) },
    { to: '/statutory', label: 'Statutory compliance', show: can(P.StatutoryManage) || can(P.StatutoryApprove) || can(P.SalaryRead) },
    { to: '/reports', label: 'Reports & audit', show: can(P.ReportsRead) || can(P.AuditRead) },
    { to: '/settings', label: 'Settings & security', show: true, section: 'Administration' },
    { to: '/data-protection', label: 'Data protection', show: can(P.OrganisationManage), section: 'Administration' },
  ]

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand"><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>{user.tenantName}</small></div></div>
        <nav className="nav" aria-label="Main">
          {nav.filter(n => n.show).map((n, i, arr) => (
            <div key={n.to}>
              {n.section && (i === 0 || arr[i - 1].section !== n.section) && <div className="section">{n.section}</div>}
              <NavLink to={n.to} end={n.to === '/'} className={({ isActive }) => (isActive ? 'active' : '')}>{n.label}</NavLink>
            </div>
          ))}
        </nav>
        <div className="user">
          <b>{user.displayName}</b>
          {user.roles.join(', ')}
          <div style={{ marginTop: 8 }}><button className="btn sm" onClick={logout}>Sign out</button></div>
        </div>
      </aside>
      <main className="main">
        <Routes>
          <Route path="/" element={employeeOnly ? <Navigate to="/self-service" replace /> : <DashboardPage />} />
          <Route path="/self-service" element={<SelfServicePage />} />
          <Route path="/employees" element={<EmployeesPage />} />
          <Route path="/employees/:id" element={<EmployeeDetailPage />} />
          <Route path="/organisation" element={<OrganisationPage />} />
          <Route path="/leave" element={<LeavePage />} />
          <Route path="/compensation" element={<CompensationPage />} />
          <Route path="/payroll" element={<PayrollRunsPage />} />
          <Route path="/payroll/:id" element={<PayrollRunDetailPage />} />
          <Route path="/statutory" element={<StatutoryPage />} />
          <Route path="/reports" element={<ReportsPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/performance" element={<PerformancePage />} />
          <Route path="/learning" element={<LearningPage />} />
          <Route path="/exits" element={<ExitsPage />} />
          <Route path="/recruitment" element={<RecruitmentPage />} />
          <Route path="/people-records" element={<PeopleRecordsPage />} />
          <Route path="/cases" element={<CasesPage />} />
          <Route path="/expenses" element={<ExpensesPage />} />
          <Route path="/hr-analytics" element={<HrAnalyticsPage />} />
          <Route path="/data-protection" element={<DataProtectionPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  )
}
