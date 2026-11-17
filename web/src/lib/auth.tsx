import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, tokenStore } from './api'
import type { Me } from './types'

type AuthState = {
  user: Me | null
  loading: boolean
  login: (tenant: string, email: string, password: string) => Promise<void>
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

  const login = useCallback(async (tenant: string, email: string, password: string) => {
    const res = await api.post<{ token: string; user: Me }>('/api/payroll/auth/login', { tenant, email, password })
    tokenStore.set(res.token)
    setUser(res.user)
  }, [])

  const logout = useCallback(() => { tokenStore.clear(); setUser(null) }, [])
  const can = useCallback((p: string) => !!user && (user.roles.includes('PlatformAdministrator') || user.permissions.includes(p)), [user])

  const value = useMemo(() => ({ user, loading, login, logout, can }), [user, loading, login, logout, can])
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
} as const
