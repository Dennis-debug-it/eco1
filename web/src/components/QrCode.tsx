import { useEffect, useState } from 'react'
import QRCode from 'qrcode'

/**
 * Renders a QR code in the browser. The encoder is bundled with the app, so the content (e.g. an MFA secret)
 * never leaves the page — don't swap this for an online QR image service.
 */
export function QrCode({ value, size = 200, label }: { value: string; size?: number; label: string }) {
  const [src, setSrc] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let live = true
    setSrc(null); setFailed(false)
    // Dark on white regardless of theme: authenticator cameras need the contrast and the quiet zone.
    QRCode.toString(value, { type: 'svg', errorCorrectionLevel: 'M', margin: 2, color: { dark: '#000000', light: '#ffffff' } })
      .then(svg => { if (live) setSrc(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`) })
      .catch(() => { if (live) setFailed(true) })
    return () => { live = false }
  }, [value])
  if (failed) return null
  return (
    <div style={{ width: size, height: size, background: '#fff', borderRadius: 8, border: '1px solid var(--border)', display: 'grid', placeItems: 'center' }}>
      {src ? <img src={src} width={size - 2} height={size - 2} alt={label} style={{ display: 'block', imageRendering: 'pixelated' }} /> : <span className="small muted">Generating…</span>}
    </div>
  )
}
