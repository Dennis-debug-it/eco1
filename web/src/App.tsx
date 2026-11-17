import { Navigate, NavLink, Route, Routes } from 'react-router-dom'
import { P, useAuth } from './lib/auth'
import { Loading } from './components/ui'
import LoginPage from './pages/Login'
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

export default function App() {
  const { user, loading, logout, can } = useAuth()
  if (loading) return <Loading what="Signing in" />
  if (!user) return <LoginPage />

  const employeeOnly = user.roles.length === 1 && user.roles[0] === 'Employee'
  const nav: { to: string; label: string; show: boolean; section?: string }[] = [
    { to: '/', label: 'Dashboard', show: can(P.ReportsRead) },
    { to: '/self-service', label: 'My payslips & leave', show: !!user.employeeId, section: 'Employee' },
    { to: '/employees', label: 'Employees', show: can(P.EmployeesRead), section: 'Workforce' },
    { to: '/organisation', label: 'Organisation', show: can(P.OrganisationManage) || can(P.EmployeesRead) },
    { to: '/leave', label: 'Leave management', show: can(P.LeaveApprove) },
    { to: '/compensation', label: 'Compensation', show: can(P.CompensationManage) || can(P.SalaryRead), section: 'Payroll' },
    { to: '/payroll', label: 'Payroll processing', show: can(P.SalaryRead) },
    { to: '/statutory', label: 'Statutory compliance', show: can(P.StatutoryManage) || can(P.StatutoryApprove) || can(P.SalaryRead) },
    { to: '/reports', label: 'Reports & audit', show: can(P.ReportsRead) || can(P.AuditRead) },
    { to: '/settings', label: 'Settings & users', show: can(P.OrganisationManage), section: 'Administration' },
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
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  )
}
