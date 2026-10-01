import { useEffect, useState } from 'react'
import { fetchImageObjectUrl } from '../api'

/**
 * A blob URL for an API image (`path` relative to /api), loaded with the access token — a plain `<img src>`
 * cannot send it. The image responses are immutable and cacheable, so the browser cache still applies.
 */
export function useAuthedImage(path: string | null): string | null {
  const [loaded, setLoaded] = useState<{ path: string; url: string } | null>(null)

  useEffect(() => {
    if (!path) return
    const controller = new AbortController()
    let url: string | null = null
    fetchImageObjectUrl(path, controller.signal)
      .then(u => { url = u; setLoaded({ path, url: u }) })
      .catch(() => { /* aborted or missing: leave the image empty */ })
    return () => {
      controller.abort()
      if (url) URL.revokeObjectURL(url)
    }
  }, [path])

  // Never show the previous image's URL for a new path.
  return loaded && loaded.path === path ? loaded.url : null
}
