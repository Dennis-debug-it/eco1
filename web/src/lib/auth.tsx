import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, tokenStore } from './api'
import type { Me } from './types'

type AuthState = {
  user: Me | null
  loading: boolean
  login: (tenant: string, email: string, password: string, code?: string) => Promise<void>
  adopt: (token: string, user: Me) => void
  logout: () => void
  can: (permission: string) => boolean
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<Me | null>(null)
  const [loading, setLoading] = useState(!!tokenStore.get())

  useEffect(() => {
    if (!tokenStore.get()) return
    api.get<Me>('/api/payroll/auth/me').then(setUser).catch(() => tokenStore.clear()).finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    const onUnauth = () => setUser(null)
    window.addEventListener('ecosys:unauthorised', onUnauth)
    return () => window.removeEventListener('ecosys:unauthorised', onUnauth)
  }, [])

  const login = useCallback(async (tenant: string, email: string, password: string, code?: string) => {
    const res = await api.post<{ token: string; user: Me }>('/api/payroll/auth/login', { tenant, email, password, code: code || undefined })
    tokenStore.set(res.token)
    setUser(res.user)
  }, [])
  const adopt = useCallback((token: string, u: Me) => { tokenStore.set(token); setUser(u) }, [])

  const logout = useCallback(() => { tokenStore.clear(); setUser(null) }, [])
  // Operators hold only platform.* permissions; nobody is granted everything by role.
  const can = useCallback((p: string) => !!user && user.permissions.includes(p), [user])

  const value = useMemo(() => ({ user, loading, login, adopt, logout, can }), [user, loading, login, adopt, logout, can])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth outside AuthProvider')
  return ctx
}

export const P = {
  OrganisationManage: 'organisation.manage', EmployeesRead: 'employees.read', EmployeesWrite: 'employees.write', SalaryRead: 'salary.read', SalaryWrite: 'salary.write',
  CompensationManage: 'compensation.manage', StatutoryManage: 'statutory.manage', StatutoryApprove: 'statutory.approve', PayrollPrepare: 'payroll.prepare', PayrollReview: 'payroll.review',
  PayrollApprove: 'payroll.approve', PayrollFinalise: 'payroll.finalise', PayrollReverse: 'payroll.reverse', PaymentsManage: 'payments.manage', JournalsRead: 'journals.read',
  ReportsRead: 'reports.read', AuditRead: 'audit.read', LeaveApprove: 'leave.approve', SelfService: 'self.service',
  HrPerformance: 'hr.performance', HrLearning: 'hr.learning', HrRecruitment: 'hr.recruitment', HrCases: 'hr.cases', HrDocuments: 'hr.documents',
  HrAssets: 'hr.assets', HrBenefits: 'hr.benefits', HrSeparations: 'hr.separations', ExpensesFinance: 'expenses.finance', BillingManage: 'billing.manage',
  IntegrationsManage: 'integrations.manage',
} as const

/** Operator roles. They carry only the platform permissions below, never a tenant permission. */
export const OperatorRoles: readonly string[] = ['PlatformAdministrator', 'PlatformOperations', 'PlatformFinance']

/** Operator console permissions (see PlatformPermissions on the server, which enforces them). */
export const PP = {
  TenantsRead: 'platform.tenants.read', TenantsManage: 'platform.tenants.manage', BillingRead: 'platform.billing.read', BillingManage: 'platform.billing.manage',
  PlansManage: 'platform.plans.manage', OperatorsManage: 'platform.operators.manage', ConfigManage: 'platform.config.manage', MonitorRead: 'platform.monitor.read',
  AuditRead: 'platform.audit.read',
} as const
