import { useRegisterSW } from 'virtual:pwa-register/react'

export default function PwaUpdatePrompt() {
  const {
    needRefresh: [needRefresh, setNeedRefresh],
    updateServiceWorker,
  } = useRegisterSW()

  if (!needRefresh) return null

  return (
    <div className="pwa-update-prompt">
      <span>New version available</span>
      <button onClick={() => updateServiceWorker(true)}>Reload</button>
      <button className="btn-ghost" onClick={() => setNeedRefresh(false)}>Dismiss</button>
    </div>
  )
}
