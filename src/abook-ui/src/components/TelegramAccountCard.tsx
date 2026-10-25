import { useCallback, useEffect, useState } from 'react'
import type { TelegramAccountState } from '../api'
import { createTelegramAccountCode, getTelegramAccount, unlinkTelegramAccount } from '../api'
import { copyText } from '../utils/clipboard'
import CopyField from './CopyField'

function errorText(err: unknown, fallback: string): string {
  const data = (err as { response?: { data?: { message?: string } } })?.response?.data
  return data?.message ?? fallback
}

/**
 * Links the signed-in user to a Telegram account. Linked, the private chat with the bot manages the user's books
 * (/newbook, /books, /book), and the user has full rights on their books in any connected chat.
 */
export default function TelegramAccountCard() {
  const [state, setState] = useState<TelegramAccountState | null>(null)
  const [link, setLink] = useState<{ link: string | null; command: string; expiresAt: string } | null>(null)
  const [copied, setCopied] = useState<string | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(() => {
    getTelegramAccount()
      .then(r => {
        setState(r.data)
        if (r.data.linked) setLink(null)
      })
      .catch(err => setError(errorText(err, 'Could not load the Telegram account.')))
  }, [])

  useEffect(() => { load() }, [load])

  // While a link is out, look for the account being linked.
  useEffect(() => {
    if (!link) return
    const timer = setInterval(load, 4000)
    const expiry = setTimeout(() => setLink(null), Math.max(0, Date.parse(link.expiresAt) - Date.now()))
    return () => { clearInterval(timer); clearTimeout(expiry) }
  }, [link, load])

  const makeLink = async () => {
    setError('')
    try { setLink((await createTelegramAccountCode()).data) } catch (err) { setError(errorText(err, 'Could not make a link.')) }
  }

  const unlink = async () => {
    if (!confirm('Unlink your Telegram account? Book management in the private chat with the bot stops; chats connected with a code keep working.')) return
    try { await unlinkTelegramAccount(); load() } catch (err) { setError(errorText(err, 'Could not unlink.')) }
  }

  const copy = async (key: string, text: string) => {
    if (await copyText(text)) {
      setCopied(key)
      setTimeout(() => setCopied(c => (c === key ? null : c)), 1500)
    }
  }

  if (!state) return <div className="card settings-form">{error ? <p className="error-msg">{error}</p> : <p className="hint">Loading…</p>}</div>
  if (!state.enabled) {
    return (
      <div className="card settings-form">
        <p className="hint">The Telegram bot is off on this server (<code>TELEGRAM_BOT_TOKEN</code> is not set).</p>
      </div>
    )
  }

  return (
    <div className="card settings-form telegram-card">
      <p className="hint">
        Link your Telegram account to create and manage your books in the private chat with the bot
        {state.botUsername && <> (<a href={`https://t.me/${state.botUsername}`} target="_blank" rel="noreferrer">@{state.botUsername}</a>)</>}:
        {' '}<code>/newbook</code>, <code>/books</code>. Send <code>/follow ID</code> (shown on each book's card) in a chat or group
        to get a book's updates there, and <code>/book</code> in such a chat for its card. In chats connected to your books you also get full rights.
      </p>
      {state.problem && <p className="error-msg" role="alert">{state.problem}</p>}
      {error && <p className="error-msg" role="alert">{error}</p>}

      {state.linked ? (
        <>
          <p>✅ Linked to <b>{state.linked.username ?? 'a Telegram account'}</b>
            {state.linked.linkedAt && <span className="hint"> since {new Date(state.linked.linkedAt).toLocaleDateString()}</span>}</p>
          <div className="actions">
            <button type="button" className="delete-btn btn-sm" onClick={() => void unlink()}>Unlink</button>
          </div>
        </>
      ) : link ? (
        <div className="telegram-code">
          <p>One-time link — works once, expires {new Date(link.expiresAt).toLocaleTimeString()}. Open it on the phone or computer with your Telegram:</p>
          {link.link && <CopyField label="Link" value={link.link} open={link.link} copied={copied === 'link'} onCopy={() => void copy('link', link.link!)} />}
          <CopyField label="Or send this to the bot" value={link.command} copied={copied === 'command'} onCopy={() => void copy('command', link.command)} />
        </div>
      ) : (
        <div className="actions">
          <button type="button" onClick={() => void makeLink()} disabled={!state.ready}>🔗 Link Telegram account</button>
        </div>
      )}
    </div>
  )
}
