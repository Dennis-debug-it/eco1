import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import type { PlanView } from '../../lib/billing'

/** Money fields are null when the operator has no billing rights (the server leaves them out). */
export type Overview = {
  saas: boolean; tenants: number; managed: number; byStatus: Record<string, number>; mrrExVat: number | null; currency: string
  openBalance: number | null; overdueBalance: number | null; trialsEndingThisWeek: number; signupsLast30Days: number; employeesUnderManagement: number
}
export type TenantRow = {
  id: string; name: string; slug: string; isActive: boolean; createdAt: string; status: string; plan?: string; planCode?: string; trialEndsOn?: string
  employees: number; balance: number | null; lastLoginAt?: string; suspensionReason?: string; cancelAtPeriodEnd: boolean
}
export type InvoiceRow = {
  id: string; tenantId: string; tenant: string; slug: string; number: string; planName: string; periodStart: string; periodEnd: string; issuedOn: string
  dueOn: string; currency: string; total: number; amountPaid: number; balance: number; status: string; overdue: boolean
}
export type TenantDetail = {
  tenant: { id: string; name: string; slug: string; isActive: boolean; createdAt: string }
  migration?: { inMigration: boolean; migrationStartedAt?: string; goLiveSignedAt?: string; goLiveSignedBy?: string; goLiveFrom?: string }
  subscription?: { status: string; trialEndsOn?: string; activatedAt?: string; currentPeriodStart?: string; currentPeriodEnd?: string; cancelAtPeriodEnd: boolean; suspensionReason?: string; inTrialStage?: boolean; billingName?: string; billingEmail?: string; billingTaxPin?: string } | null
  /** The full plan with prices for billing roles; only id, code and name otherwise. */
  plan?: (Partial<PlanView> & { code: string; name: string }) | null
  usage: { employees: number; legalEntities: number; users: number; payrollRuns: number }
  administrators: { email: string; displayName: string; lastLoginAt?: string; mfaEnabled: boolean; emailVerified: boolean }[]
  invoices: { id: string; number: string; periodStart: string; periodEnd: string; total: number; amountPaid: number; status: string; dueOn: string; currency: string }[]
  history: { action: string; actorName?: string; occurredAt: string; details?: string }[]
}
export type PlanRow = { plan: PlanView; subscribers: number }

export const usePlans = (enabled = true) =>
  useQuery({ queryKey: ['platform-plans'], queryFn: () => api.get<PlanRow[]>('/api/platform/plans'), enabled })

export const roleLabel: Record<string, string> = {
  PlatformAdministrator: 'Platform administrator', PlatformOperations: 'Platform operations', PlatformFinance: 'Platform finance',
}
export const roleHint: Record<string, string> = {
  PlatformAdministrator: 'Everything, including prices, platform settings and operator accounts',
  PlatformOperations: 'Organisations, onboarding and system health. No revenue, invoices or prices',
  PlatformFinance: 'Subscriptions, invoices, payments and revenue. No account administration',
}

/** Refreshes every console view that shows an organisation's state. */
export function useRefreshTenants() {
  const qc = useQueryClient()
  return (id?: string) => {
    if (id) qc.invalidateQueries({ queryKey: ['platform-tenant', id] })
    qc.invalidateQueries({ queryKey: ['platform-tenants'] })
    qc.invalidateQueries({ queryKey: ['platform-overview'] })
  }
}
