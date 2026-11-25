import { Navigate, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from './lib/api'
import { OperatorRoles, P, PP, useAuth } from './lib/auth'
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
import BillingPage from './pages/Billing'
import CareersPage from './pages/Careers'
import OrganogramPage from './pages/Organogram'
import OfferResponsePage from './pages/OfferResponse'
import OperatorOverviewPage from './pages/operator/Overview'
import OrganisationsPage from './pages/operator/Organisations'
import OrganisationProfilePage from './pages/operator/OrganisationProfile'
import SubscriptionsPage from './pages/operator/Subscriptions'
import OperatorsPage from './pages/operator/Operators'
import { roleLabel } from './pages/operator/shared'
import MigrationPage, { type MigrationStatus } from './pages/Migration'
import IntegrationsPage from './pages/Integrations'
import SignupPage, { VerifyEmailPage } from './pages/Signup'
import { ForgotPasswordPage, ResetPasswordPage } from './pages/PasswordReset'
import { useHrMe } from './lib/lookups'
import { useAutoHideMenu } from './lib/menu'
import { Feature, useBillingStatus, type BillingStatus } from './lib/billing'

export default function App() {
  const { user, loading, logout, can } = useAuth()
  const location = useLocation()
  const menu = useAutoHideMenu(location.pathname)
  const ready = !!user && !user.mustChangePassword && (!user.mfaRequired || user.mfaSatisfied)
  const hrMe = useHrMe(ready)
  const billing = useBillingStatus(ready)
  // Everyone who works on payroll should see that nothing can be paid yet, not only whoever started the migration.
  const migration = useQuery({ queryKey: ['migration'], queryFn: () => api.get<MigrationStatus>('/api/payroll/migration'), enabled: ready && !!user?.permissions.includes('employees.read') })
  // Public pages that work whether or not anyone is signed in on this browser:
  // a candidate's offer link, and the careers page (candidates never sign in).
  // A reset link must work even if someone else is still signed in on this browser.
  if (location.pathname === '/reset-password') return <ResetPasswordPage />
  if (location.pathname.startsWith('/offer/')) return <Routes><Route path="/offer/:token" element={<OfferResponsePage />} /></Routes>
  if (location.pathname.startsWith('/careers/')) return (
    <Routes>
      <Route path="/careers/:slug" element={<CareersPage />} />
      <Route path="/careers/:slug/jobs/:jobId" element={<CareersPage />} />
      <Route path="*" element={<CareersPage />} />
    </Routes>
  )
  if (loading) return <Loading what="Signing in" />
  if (!user) return (
    <Routes>
      <Route path="/signup" element={<SignupPage />} />
      <Route path="/verify-email" element={<VerifyEmailPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="*" element={<LoginPage />} />
    </Routes>
  )
  if (user.mustChangePassword) return <ChangePassword gate />
  if (user.mfaRequired && !user.mfaSatisfied) return user.mfaEnabled
    ? <div className="login"><div className="card"><h2>Sign in again</h2><p className="small">Your session was started without a verification code. Sign out and sign in with your authenticator code.</p><button className="btn primary" onClick={logout}>Sign out</button></div></div>
    : <MfaEnrol gate />

  const employeeOnly = user.roles.length === 1 && user.roles[0] === 'Employee'
  // Operators work in the platform organisation, which holds no payroll: they get the console's own menu.
  const operatorOnly = user.roles.length > 0 && user.roles.every(r => OperatorRoles.includes(r))
  const b = billing.data
  const hrSuite = !b || b.managed || b.features.includes(Feature.HrSuite)
  const manager = (hrMe.data?.directReports ?? 0) > 0
  const staff = !!user.employeeId
  const count = (n?: number) => (n ? ` (${n})` : '')
  // Menu items follow permissions; the API enforces them either way.
  const operatorNav: { to: string; label: string; show: boolean; section?: string }[] = [
    { to: '/operator', label: 'Overview', show: can(PP.TenantsRead), section: 'Platform' },
    { to: '/operator/organisations', label: 'Organisations', show: can(PP.TenantsRead), section: 'Platform' },
    { to: '/operator/subscriptions', label: 'Plans & subscriptions', show: can(PP.BillingRead), section: 'Platform' },
    { to: '/operator/operators', label: 'Operator accounts', show: can(PP.OperatorsManage), section: 'Platform' },
    { to: '/settings', label: 'Settings & security', show: true, section: 'Me' },
  ]
  const nav: { to: string; label: string; show: boolean; section?: string }[] = operatorOnly ? operatorNav : [
    { to: '/', label: 'Dashboard', show: can(P.ReportsRead) },
    { to: '/self-service', label: 'My workspace', show: staff, section: 'Me' },
    { to: '/employees', label: 'Employees', show: can(P.EmployeesRead), section: 'People' },
    { to: '/organisation', label: 'Organisation', show: can(P.OrganisationManage) || can(P.EmployeesRead) },
    { to: '/organogram', label: 'Organogram', show: can(P.EmployeesRead) },
    { to: '/people-records', label: 'People records', show: can(P.HrDocuments) || (hrSuite && (can(P.HrBenefits) || can(P.HrAssets) || can(P.EmployeesRead))) },
    { to: '/recruitment', label: 'Recruitment', show: can(P.HrRecruitment) && hrSuite },
    { to: '/exits', label: 'Exits & final dues', show: can(P.HrSeparations) || can(P.PayrollPrepare) || can(P.PayrollApprove) },
    { to: '/leave', label: `Leave${count(hrMe.data?.pendingLeave)}`, show: can(P.LeaveApprove) || manager || staff, section: 'HR' },
    { to: '/performance', label: `Performance${count(hrMe.data?.reviewsToComplete)}`, show: (can(P.HrPerformance) || manager || can(P.SalaryRead)) && hrSuite },
    { to: '/learning', label: `Learning${count(hrMe.data?.pendingNominations)}`, show: (can(P.HrLearning) || manager || staff) && hrSuite },
    { to: '/expenses', label: `Expenses${count(hrMe.data?.pendingExpenses)}`, show: can(P.ExpensesFinance) || staff },
    { to: '/cases', label: 'Cases', show: can(P.HrCases) && hrSuite },
    { to: '/hr-analytics', label: 'HR analytics', show: can(P.ReportsRead) },
    { to: '/compensation', label: 'Compensation', show: can(P.CompensationManage) || can(P.SalaryRead), section: 'Payroll' },
    { to: '/payroll', label: 'Payroll processing', show: can(P.SalaryRead) },
    { to: '/statutory', label: 'Statutory compliance', show: can(P.StatutoryManage) || can(P.StatutoryApprove) || can(P.SalaryRead) },
    { to: '/reports', label: 'Reports & audit', show: can(P.ReportsRead) || can(P.AuditRead) },
    { to: '/settings', label: 'Settings & security', show: true, section: 'Administration' },
    { to: '/data-protection', label: 'Data protection', show: can(P.OrganisationManage), section: 'Administration' },
    { to: '/integrations', label: 'Integrations & API', show: can(P.IntegrationsManage), section: 'Administration' },
    { to: '/migration', label: 'Data migration', show: can(P.EmployeesWrite) || can(P.OrganisationManage), section: 'Administration' },
    { to: '/billing', label: 'Billing', show: !!b?.saas && !b.managed && can(P.BillingManage), section: 'Administration' },
  ]

  return (
    <div className={`shell${menu.docked ? '' : ' menu-hidden'}${menu.open ? ' menu-open' : ''}`}>
      {!menu.docked && menu.wide && !menu.open && <div className="menu-edge" aria-hidden onMouseEnter={menu.edgeEnter} onMouseLeave={menu.edgeLeave} />}
      {!menu.docked && menu.open && <div className="menu-backdrop" aria-hidden onClick={menu.close} onMouseEnter={() => { if (menu.byHover) menu.close() }} />}
      <aside className="sidebar" id="main-menu" inert={!menu.docked && !menu.open} onMouseLeave={() => { if (menu.wide && !menu.docked) menu.close() }}>
        <div className="brand"><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>{user.tenantName}</small></div></div>
        <nav className="nav" aria-label="Main">
          {nav.filter(n => n.show).map((n, i, arr) => (
            <div key={n.to}>
              {n.section && (i === 0 || arr[i - 1].section !== n.section) && <div className="section">{n.section}</div>}
              <NavLink to={n.to} end={n.to === '/' || n.to === '/operator'} className={({ isActive }) => (isActive ? 'active' : '')}>{n.label}</NavLink>
            </div>
          ))}
        </nav>
        <div className="user">
          <b>{user.displayName}</b>
          {user.roles.map(r => roleLabel[r] ?? r).join(', ')}
          <div className="actions" style={{ marginTop: 8 }}>
            <button className="btn sm" onClick={logout}>Sign out</button>
            {menu.wide && <button className="btn sm" onClick={() => menu.setPinned(!menu.pinned)} title={menu.pinned ? 'Hide the menu until you point at the left edge or press ☰' : 'Show the menu all the time'}>{menu.pinned ? 'Auto-hide menu' : 'Keep menu open'}</button>}
          </div>
        </div>
      </aside>
      <main className="main">
        {!menu.docked && <header className="topbar">
          <button className="menu-btn" aria-label="Menu" aria-expanded={menu.open} aria-controls="main-menu" onClick={menu.toggle}><span aria-hidden>☰</span></button>
          <div className="brand"><div className="logo" aria-hidden>E</div><div>Ecosys Payroll<small>{user.tenantName}</small></div></div>
        </header>}
        {b && <SubscriptionBanner status={b} admin={can(P.BillingManage)} />}
        {migration.data?.inMigration && <div className="alert info" role="status">Migration mode: payroll can be calculated and compared with the old system, but not approved, paid or filed until go-live is signed. <NavLink to="/migration">Data migration</NavLink></div>}
        <Routes>
          <Route path="/" element={employeeOnly ? <Navigate to="/self-service" replace /> : operatorOnly ? <Navigate to="/operator" replace /> : <DashboardPage />} />
          <Route path="/billing" element={<BillingPage />} />
          <Route path="/organogram" element={<OrganogramPage />} />
          <Route path="/operator" element={<OperatorOverviewPage />} />
          <Route path="/operator/organisations" element={<OrganisationsPage />} />
          <Route path="/operator/organisations/:id" element={<OrganisationProfilePage />} />
          <Route path="/operator/subscriptions" element={<SubscriptionsPage />} />
          <Route path="/operator/operators" element={<OperatorsPage />} />
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
          <Route path="/migration" element={<MigrationPage />} />
          <Route path="/integrations" element={<IntegrationsPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  )
}

/** Trial countdown, overdue and read-only notices. Everyone sees read-only (it explains blocked changes); only billing admins see trial and payment nags. */
function SubscriptionBanner({ status: b, admin }: { status: BillingStatus; admin: boolean }) {
  if (!b.saas || b.managed) return null
  const link = admin ? <> <NavLink to="/billing">Go to billing</NavLink></> : null
  if (b.readOnly) return <div className="alert danger" role="status">This organisation's account is read-only{b.status === 'Cancelled' ? ' because the subscription was cancelled' : b.suspensionReason ? `: ${b.suspensionReason.toLowerCase()}` : ''}. You can view and export records but not make changes.{admin ? link : ' Ask your administrator to reactivate it.'}</div>
  if (!admin) return null
  if (b.status === 'PastDue') return <div className="alert warn" role="status">An invoice is overdue. Pay it to avoid the account becoming read-only.{link}</div>
  if (b.status === 'Trialing' && !b.activated && (b.trialDaysLeft ?? 99) <= 7) return <div className="alert info" role="status">Your free trial ends in {b.trialDaysLeft} day{b.trialDaysLeft === 1 ? '' : 's'}. Add billing details to keep going.{link}</div>
  if (b.cancelAtPeriodEnd) return <div className="alert warn" role="status">Your subscription is set to end at the close of this billing period.{link}</div>
  return null
}
