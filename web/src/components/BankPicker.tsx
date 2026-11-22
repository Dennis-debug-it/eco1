import { useBankBranches, useBanks } from '../lib/lookups'
import { Field } from './ui'

/**
 * Bank and branch chosen from the bank directory. Branch codes are free text only for banks whose branch list
 * hasn't been loaded; the server checks both either way.
 */
export function BankPicker({ bankCode, branchCode, onChange }: { bankCode?: string; branchCode?: string; onChange: (bankCode: string, branchCode: string) => void }) {
  const banks = useBanks()
  const branches = useBankBranches(bankCode)
  const listed = (branches.data?.length ?? 0) > 0
  return (
    <>
      <Field label="Bank *">
        <select value={bankCode ?? ''} onChange={e => onChange(e.target.value, '')}>
          <option value="">Choose…</option>
          {banks.data?.map(b => <option key={b.code} value={b.code}>{b.name} ({b.code})</option>)}
        </select>
      </Field>
      <Field label={listed ? 'Branch *' : 'Branch code'}>
        {listed
          ? <select value={branchCode ?? ''} onChange={e => onChange(bankCode!, e.target.value)}>
              <option value="">Choose…</option>
              {branches.data!.map(b => <option key={b.code} value={b.code}>{b.name} ({b.code})</option>)}
            </select>
          : <input value={branchCode ?? ''} disabled={!bankCode} inputMode="numeric" onChange={e => onChange(bankCode!, e.target.value)} />}
        {bankCode && !listed && !branches.isLoading && <span className="muted small">This bank's branch list isn't loaded — type the code the bank gave.</span>}
      </Field>
    </>
  )
}
