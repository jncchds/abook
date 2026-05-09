import { useState, useEffect } from 'react'

const STORAGE_KEY = 'abook:notifications:enabled'

export type NotificationPermissionState = 'granted' | 'denied' | 'default' | 'unsupported'

export function useNotifications() {
  const supported = 'Notification' in window

  const [permission, setPermission] = useState<NotificationPermissionState>(() =>
    supported ? (Notification.permission as NotificationPermissionState) : 'unsupported'
  )

  const [enabled, setEnabledState] = useState(() => {
    if (!supported) return false
    return localStorage.getItem(STORAGE_KEY) === 'true'
  })

  // Keep permission state in sync (e.g. user revokes in browser settings)
  useEffect(() => {
    if (!supported) return
    setPermission(Notification.permission as NotificationPermissionState)
  }, [supported])

  const requestPermission = async (): Promise<NotificationPermission> => {
    if (!supported) return 'denied'
    const result = await Notification.requestPermission()
    setPermission(result as NotificationPermissionState)
    return result
  }

  const setEnabled = async (value: boolean) => {
    if (!supported) return
    if (value && Notification.permission === 'default') {
      const result = await requestPermission()
      if (result !== 'granted') return
    }
    localStorage.setItem(STORAGE_KEY, String(value))
    setEnabledState(value)
  }

  const notify = (title: string, options?: NotificationOptions & { onClick?: () => void }) => {
    if (!supported) return
    if (!enabled) return
    if (Notification.permission !== 'granted') return
    if (document.visibilityState !== 'hidden') return

    const { onClick, ...notifOptions } = options ?? {}
    const n = new Notification(title, {
      icon: '/pwa-192x192.png',
      ...notifOptions,
    })
    if (onClick) {
      n.onclick = () => {
        window.focus()
        onClick()
        n.close()
      }
    }
  }

  return { supported, permission, enabled, requestPermission, setEnabled, notify }
}
