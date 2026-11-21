import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import type { Component, DirectoryBank, DirectoryBranch, LegalEntity, Lookup, PayingAccount, PayrollGroup, PayrollPeriod, Structure } from './types'

export const useLegalEntities = () => useQuery({ queryKey: ['legal-entities'], queryFn: () => api.get<LegalEntity[]>('/api/payroll/organisation/legal-entities') })
export const useDepartments = (le?: string) => useQuery({ queryKey: ['departments', le], queryFn: () => api.get<Lookup[]>(`/api/payroll/organisation/departments${le ? `?legalEntityId=${le}` : ''}`) })
export const useBranches = (le?: string) => useQuery({ queryKey: ['branches', le], queryFn: () => api.get<Lookup[]>(`/api/payroll/organisation/branches${le ? `?legalEntityId=${le}` : ''}`) })
export const useCostCentres = (le?: string) => useQuery({ queryKey: ['cost-centres', le], queryFn: () => api.get<Lookup[]>(`/api/payroll/organisation/cost-centres${le ? `?legalEntityId=${le}` : ''}`) })
export const usePayrollGroups = (le?: string) => useQuery({ queryKey: ['payroll-groups', le], queryFn: () => api.get<PayrollGroup[]>(`/api/payroll/organisation/payroll-groups${le ? `?legalEntityId=${le}` : ''}`) })
export const usePeriods = (groupId?: string) => useQuery({ queryKey: ['periods', groupId], queryFn: () => api.get<PayrollPeriod[]>(`/api/payroll/organisation/payroll-groups/${groupId}/periods`), enabled: !!groupId })
export const useComponents = (le?: string) => useQuery({ queryKey: ['components', le], queryFn: () => api.get<Component[]>(`/api/payroll/compensation/components${le ? `?legalEntityId=${le}` : ''}`) })
export const useStructures = (le?: string) => useQuery({ queryKey: ['structures', le], queryFn: () => api.get<Structure[]>(`/api/payroll/compensation/salary-structures${le ? `?legalEntityId=${le}` : ''}`) })

export const useBanks = () => useQuery({ queryKey: ['banks'], queryFn: () => api.get<DirectoryBank[]>('/api/payroll/organisation/banks'), staleTime: 300_000 })
export const useBankBranches = (bankCode?: string) => useQuery({ queryKey: ['bank-branches', bankCode], queryFn: () => api.get<DirectoryBranch[]>(`/api/payroll/organisation/banks/${bankCode}/branches`), enabled: !!bankCode, staleTime: 300_000 })
export const usePayingAccounts = (le?: string, enabled = true) => useQuery({ queryKey: ['paying-accounts', le], enabled, queryFn: () => api.get<PayingAccount[]>(`/api/payroll/organisation/paying-accounts${le ? `?legalEntityId=${le}` : ''}`) })

export const byId = <T extends { id: string }>(rows: T[] | undefined) => Object.fromEntries((rows ?? []).map(r => [r.id, r])) as Record<string, T>

export type HrMe = { employeeId?: string; directReports: number; team: { id: string; employeeNumber: string; fullName: string; jobTitle?: string }[]; pendingLeave: number; pendingExpenses: number; pendingNominations: number; reviewsToComplete: number; feedbackRequested: number; myReviewOpen: boolean }
export const useHrMe = (enabled = true) => useQuery({ queryKey: ['hr-me'], enabled, queryFn: () => api.get<HrMe>('/api/payroll/hr/me'), staleTime: 30_000 })
export type EmployeeOption = { id: string; employeeNumber: string; fullName: string; jobTitle?: string }
export const useEmployeeOptions = (enabled = true) => useQuery({ queryKey: ['employee-options'], enabled, queryFn: async () => (await api.get<{ items: EmployeeOption[] }>('/api/payroll/employees?limit=500')).items })
