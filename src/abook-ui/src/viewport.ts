/**
 * Keeps `--app-height` equal to the visible part of the window, so the app layout (which scrolls inside
 * its own panes) always fits it exactly. On phones `100vh` is the height with the browser toolbars hidden
 * and ignores the on-screen keyboard: the page ends up taller than the screen, scrolls as a whole, the
 * header slides away and an empty band shows below the UI. The document itself is never meant to scroll
 * under the app layout, so a scroll the browser makes anyway (iOS does, to show a focused input) is undone.
 */
export function trackVisibleViewport() {
  const vv = window.visualViewport
  const root = document.documentElement
  let frame = 0
  const update = () => {
    cancelAnimationFrame(frame)
    frame = requestAnimationFrame(() => {
      root.style.setProperty('--app-height', `${Math.round(vv?.height ?? window.innerHeight)}px`)
      if (document.querySelector('.app-layout') && (window.scrollY !== 0 || window.scrollX !== 0)) window.scrollTo(0, 0)
    })
  }
  update()
  window.addEventListener('resize', update)
  window.addEventListener('scroll', update, { passive: true })
  vv?.addEventListener('resize', update)
  vv?.addEventListener('scroll', update)
}
