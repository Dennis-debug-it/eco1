import { useQuery } from '@tanstack/react-query'
import { api } from './api'

export type PlanView = {
  id: string; code: string; name: string; description?: string; currency: string; monthlyBaseFee: number; includedEmployees: number; pricePerAdditionalEmployee: number
  maxEmployees?: number | null; maxLegalEntities?: number | null; features: string[]; isPublic: boolean; isActive: boolean; sortOrder: number
}

export type BillingStatus = {
  saas: boolean; managed: boolean; status?: 'Trialing' | 'Active' | 'PastDue' | 'Suspended' | 'Cancelled'; planCode?: string; planName?: string; features: string[]
  readOnly: boolean; trialEndsOn?: string; trialDaysLeft?: number; activated: boolean; cancelAtPeriodEnd: boolean; suspensionReason?: string
  usage?: { employees: number; maxEmployees?: number | null; legalEntities: number; maxLegalEntities?: number | null }
}

export const Feature = { HrSuite: 'hr_suite' } as const

export const featureLabel: Record<string, string> = { hr_suite: 'HR suite' }

/** Subscription status for the signed-in organisation. Operator-managed organisations and licensed installs report managed: true. */
export function useBillingStatus(enabled = true) {
  return useQuery({ queryKey: ['billing-status'], queryFn: () => api.get<BillingStatus>('/api/payroll/billing/status'), enabled, staleTime: 60_000 })
}

/** True when the organisation's plan includes the feature (always true when not billed through a plan, or while loading). */
export function usePlanFeature(feature: string, enabled = true) {
  const q = useBillingStatus(enabled)
  return !q.data || q.data.managed || q.data.features.includes(feature)
}

/** "KES 7,500 / month incl. 25 employees, then KES 200 each" */
export function priceLine(p: PlanView) {
  const money = (v: number) => new Intl.NumberFormat('en-KE', { style: 'currency', currency: p.currency, maximumFractionDigits: 0 }).format(v)
  const base = `${money(p.monthlyBaseFee)} / month`
  if (p.pricePerAdditionalEmployee <= 0) return p.monthlyBaseFee > 0 ? base : 'Custom pricing'
  return `${base} incl. ${p.includedEmployees} employees, then ${money(p.pricePerAdditionalEmployee)} each`
}

export function limitsLine(p: PlanView) {
  const emp = p.maxEmployees ? `Up to ${p.maxEmployees} employees` : 'Unlimited employees'
  const le = p.maxLegalEntities ? `${p.maxLegalEntities} ${p.maxLegalEntities === 1 ? 'company' : 'companies'}` : 'unlimited companies'
  return `${emp}, ${le}`
}
