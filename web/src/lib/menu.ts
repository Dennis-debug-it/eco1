import { useEffect, useRef, useState } from 'react'

const pinKey = 'ecosys.menuPinned'

function useMediaQuery(query: string) {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches)
  useEffect(() => {
    const m = window.matchMedia(query)
    const on = () => setMatches(m.matches)
    m.addEventListener('change', on)
    return () => m.removeEventListener('change', on)
  }, [query])
  return matches
}

/**
 * The main menu. On wide screens it is either docked (always shown) or auto-hidden: it slides in when the pointer
 * rests at the left edge or ☰ is pressed, and away when the pointer leaves it or a page is opened. On phones and
 * tablets it is always hidden until ☰ is pressed. The docked/auto-hide choice is remembered in this browser.
 */
export function useAutoHideMenu(pathname: string) {
  const wide = useMediaQuery('(min-width: 901px)')
  const [pinned, setPinnedState] = useState(() => {
    try { const v = localStorage.getItem(pinKey); if (v === 'true' || v === 'false') return v === 'true' } catch { /* storage unavailable */ }
    return window.innerWidth >= 1366
  })
  const [open, setOpen] = useState(false)
  /** Opened by resting at the edge (closes as soon as the pointer is back on the page), or by ☰. */
  const [byHover, setByHover] = useState(false)
  const timer = useRef<number | undefined>(undefined)
  const docked = wide && pinned

  // Opening a page puts the menu away again.
  useEffect(() => { setOpen(false) }, [pathname])
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])
  useEffect(() => () => window.clearTimeout(timer.current), [])

  const setPinned = (v: boolean) => {
    setPinnedState(v); setOpen(false)
    try { localStorage.setItem(pinKey, String(v)) } catch { /* storage unavailable */ }
  }
  return {
    wide, docked, open, pinned,
    byHover,
    toggle: () => { setByHover(false); setOpen(o => !o) },
    close: () => setOpen(false),
    setPinned,
    /** Resting at the left edge for a moment opens it, so brushing past the edge doesn't. */
    edgeEnter: () => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => { setByHover(true); setOpen(true) }, 250) },
    edgeLeave: () => window.clearTimeout(timer.current),
  }
}
