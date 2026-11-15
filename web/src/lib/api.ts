export type Problem = { title?: string; detail?: string; status?: number; code?: string }

export class ApiError extends Error {
  constructor(public status: number, public problem: Problem) {
    super(problem.detail || problem.title || `Request failed (${status})`)
  }
}

const TOKEN_KEY = 'ecosys.payroll.token'
export const tokenStore = {
  get: () => { try { return localStorage.getItem(TOKEN_KEY) } catch { return null } },
  set: (t: string) => { try { localStorage.setItem(TOKEN_KEY, t) } catch { /* ignore */ } },
  clear: () => { try { localStorage.removeItem(TOKEN_KEY) } catch { /* ignore */ } },
}

async function request<T>(method: string, url: string, body?: unknown, raw = false): Promise<T> {
  const headers: Record<string, string> = {}
  const token = tokenStore.get()
  if (token) headers.Authorization = `Bearer ${token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const res = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (res.status === 401) { tokenStore.clear(); window.dispatchEvent(new Event('ecosys:unauthorised')) }
  if (!res.ok) {
    let problem: Problem = { status: res.status }
    try { problem = await res.json() } catch { /* no body */ }
    throw new ApiError(res.status, problem)
  }
  if (raw) return res as unknown as T
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body: unknown) => request<T>('PUT', url, body),
  del: <T>(url: string) => request<T>('DELETE', url),
  download: async (url: string, body?: unknown) => {
    const res = await request<Response>(body === undefined ? 'GET' : 'POST', url, body, true)
    const blob = await res.blob()
    const cd = res.headers.get('Content-Disposition') || ''
    const m = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(cd)
    const name = m ? decodeURIComponent(m[1]) : 'download'
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob); a.download = name; a.click()
    setTimeout(() => URL.revokeObjectURL(a.href), 2000)
    return name
  },
}

export const fmt = {
  money: (v: number | null | undefined, currency = 'KES') =>
    v === null || v === undefined ? '—' : new Intl.NumberFormat('en-KE', { style: 'currency', currency, minimumFractionDigits: 2 }).format(v),
  num: (v: number | null | undefined) => (v === null || v === undefined ? '—' : new Intl.NumberFormat('en-KE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v)),
  int: (v: number | null | undefined) => (v === null || v === undefined ? '—' : new Intl.NumberFormat('en-KE').format(v)),
  date: (v: string | null | undefined) => (v ? new Date(v.length === 10 ? v + 'T00:00:00' : v).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '—'),
  dateTime: (v: string | null | undefined) => (v ? new Date(v).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—'),
  pct: (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${v.toFixed(1)}%`),
}
