import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import type { Component, LegalEntity, Lookup, PayrollGroup, PayrollPeriod, Structure } from './types'

export const useLegalEntities = () => useQuery({ queryKey: ['legal-entities'], queryFn: () => api.get<LegalEntity[]>('/api/payroll/organisation/legal-entities') })
export const useDepartments = (le?: string) => useQuery({ queryKey: ['departments', le], queryFn: () => api.get<Lookup[]>(`/api/payroll/organisation/departments${le ? `?legalEntityId=${le}` : ''}`) })
export const useBranches = (le?: string) => useQuery({ queryKey: ['branches', le], queryFn: () => api.get<Lookup[]>(`/api/payroll/organisation/branches${le ? `?legalEntityId=${le}` : ''}`) })
export const useCostCentres = (le?: string) => useQuery({ queryKey: ['cost-centres', le], queryFn: () => api.get<Lookup[]>(`/api/payroll/organisation/cost-centres${le ? `?legalEntityId=${le}` : ''}`) })
export const usePayrollGroups = (le?: string) => useQuery({ queryKey: ['payroll-groups', le], queryFn: () => api.get<PayrollGroup[]>(`/api/payroll/organisation/payroll-groups${le ? `?legalEntityId=${le}` : ''}`) })
export const usePeriods = (groupId?: string) => useQuery({ queryKey: ['periods', groupId], queryFn: () => api.get<PayrollPeriod[]>(`/api/payroll/organisation/payroll-groups/${groupId}/periods`), enabled: !!groupId })
export const useComponents = (le?: string) => useQuery({ queryKey: ['components', le], queryFn: () => api.get<Component[]>(`/api/payroll/compensation/components${le ? `?legalEntityId=${le}` : ''}`) })
export const useStructures = (le?: string) => useQuery({ queryKey: ['structures', le], queryFn: () => api.get<Structure[]>(`/api/payroll/compensation/salary-structures${le ? `?legalEntityId=${le}` : ''}`) })

export const byId = <T extends { id: string }>(rows: T[] | undefined) => Object.fromEntries((rows ?? []).map(r => [r.id, r])) as Record<string, T>
