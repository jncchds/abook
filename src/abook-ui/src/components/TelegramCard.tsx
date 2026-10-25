import { useCallback, useEffect, useRef, useState } from 'react'
import type { TelegramAccessLevel, TelegramBinding, TelegramCode, TelegramFileFormat, TelegramState } from '../api'
import { createTelegramCode, getTelegram, revokeTelegramBinding, updateTelegramBinding } from '../api'
import ToggleField from './ToggleField'
import { copyText } from '../utils/clipboard'
import CopyField from './CopyField'

const LEVELS: { value: TelegramAccessLevel; label: string; hint: string }[] = [
  { value: 'Read', label: 'Read', hint: 'status, notices and files' },
  { value: 'Participate', label: 'Participate', hint: 'also answer questions, export, add notes, and write in the book chat and apply its suggestions' },
  { value: 'Control', label: 'Control', hint: 'also start, continue and stop runs, rewind and disconnect' },
]

const CHAT_ICONS: Record<TelegramBinding['chatType'], string> = {
  private: '👤', group: '👥', supergroup: '👥', channel: '📣',
}

function errorText(err: unknown, fallback: string): string {
  const data = (err as { response?: { data?: { message?: string } } })?.response?.data
  return data?.message ?? fallback
}

/**
 * The book's Telegram connections: make a one-time code to bind a chat or topic, then manage what the people there
 * may do. Access is the chat itself — nobody signs in on the Telegram side.
 */
export default function TelegramCard({ bookId }: { bookId: number }) {
  const [state, setState] = useState<TelegramState | null>(null)
  const [level, setLevel] = useState<TelegramAccessLevel>('Participate')
  const [code, setCode] = useState<TelegramCode | null>(null)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState<string | null>(null)

  // The bindings that existed when the code was made: a new one means the code was used.
  const codeBaseline = useRef<Set<number> | null>(null)

  const load = useCallback(() => {
    getTelegram(bookId)
      .then(r => {
        setState(r.data)
        const baseline = codeBaseline.current
        if (baseline && r.data.bindings.some(b => !baseline.has(b.id))) {
          codeBaseline.current = null
          setCode(null)
        }
      })
      .catch(err => setError(errorText(err, 'Could not load the Telegram settings.')))
  }, [bookId])

  useEffect(() => { load() }, [load])

  // While a code is out, look for the new binding every few seconds.
  useEffect(() => {
    if (!code) return
    const timer = setInterval(load, 4000)
    const expires = Date.parse(code.expiresAt) - Date.now()
    const expiry = setTimeout(() => { codeBaseline.current = null; setCode(null) }, Math.max(0, expires))
    return () => { clearInterval(timer); clearTimeout(expiry) }
  }, [code, load])

  const makeCode = async () => {
    setError('')
    try {
      const r = await createTelegramCode(bookId, level)
      codeBaseline.current = new Set((state?.bindings ?? []).map(b => b.id))
      setCode(r.data)
      setCopied(null)
    } catch (err) {
      setError(errorText(err, 'Could not make a code.'))
    }
  }

  const update = async (binding: TelegramBinding, patch: Parameters<typeof updateTelegramBinding>[2]) => {
    setError('')
    try {
      const r = await updateTelegramBinding(bookId, binding.id, patch)
      setState(s => s && { ...s, bindings: s.bindings.map(b => (b.id === binding.id ? r.data : b)) })
    } catch (err) {
      setError(errorText(err, 'Could not save the change.'))
    }
  }

  const revoke = async (binding: TelegramBinding) => {
    if (!confirm(`Disconnect “${binding.chatTitle}”? The chat gets a last notice and stops following the book.`)) return
    setError('')
    try {
      await revokeTelegramBinding(bookId, binding.id)
      setState(s => s && { ...s, bindings: s.bindings.filter(b => b.id !== binding.id) })
    } catch (err) {
      setError(errorText(err, 'Could not disconnect the chat.'))
    }
  }

  /** Copies and marks which field was copied for a moment. */
  const copy = async (key: string, text: string) => {
    if (await copyText(text)) {
      setCopied(key)
      setTimeout(() => setCopied(c => (c === key ? null : c)), 1500)
    }
  }

  if (!state) {
    return <div className="card settings-form">{error ? <p className="error-msg">{error}</p> : <p className="hint">Loading…</p>}</div>
  }

  if (!state.enabled) {
    return (
      <div className="card settings-form">
        <p className="hint">
          The Telegram bot is off. To turn it on, set <code>TELEGRAM_BOT_TOKEN</code> on the server (a token from @BotFather),
          and <code>ABOOK_HOSTNAME</code> if the server is reachable over HTTPS (otherwise the bot polls Telegram).
        </p>
      </div>
    )
  }

  return (
    <div className="card settings-form telegram-card">
      <p className="hint">
        Connect a Telegram chat or topic to this book. <b>Everyone who can post there</b> gets the access you choose — there are
        no abook accounts on the Telegram side. Chapter files arrive after each chapter edit.
        {state.botUsername && <> Bot: <a href={`https://t.me/${state.botUsername}`} target="_blank" rel="noreferrer">@{state.botUsername}</a>.</>}
      </p>
      {state.problem && <p className="error-msg" role="alert">{state.problem}</p>}
      {error && <p className="error-msg" role="alert">{error}</p>}

      <div className="telegram-connect">
        <label>
          Access for the new chat
          <select value={level} onChange={e => setLevel(e.target.value as TelegramAccessLevel)}>
            {LEVELS.map(l => <option key={l.value} value={l.value}>{l.label} — {l.hint}</option>)}
          </select>
        </label>
        {level === 'Control' && (
          <p className="hint telegram-warning">
            ⚠️ With Control, anyone in the chat can start runs — they use this book's LLM settings and spend its tokens.
            In groups you can limit that to chat admins once connected.
          </p>
        )}
        <div className="actions">
          <button type="button" onClick={() => void makeCode()} disabled={!state.ready}>🔗 Connect a chat</button>
          <button type="button" className="btn-secondary" onClick={load}>Refresh</button>
        </div>
        {!state.ready && <p className="hint">The bot is starting…</p>}
      </div>

      {code && (
        <div className="telegram-code">
          <p>One-time code — works once, expires {new Date(code.expiresAt).toLocaleTimeString()}. Use it one of these ways:</p>
          {code.privateLink && (
            <CopyField label="Private chat" value={code.privateLink} copied={copied === 'private'} onCopy={() => void copy('private', code.privateLink!)}
              open={code.privateLink}
              hint="Opens a private chat with the bot and connects it to this book (a private chat follows one book; use groups or their topics for more)." />
          )}
          {code.groupLink && (
            <CopyField label="Group" value={code.groupLink} copied={copied === 'group'} onCopy={() => void copy('group', code.groupLink!)}
              open={code.groupLink} hint="Adds the bot to a group you pick and binds it (or its General topic)." />
          )}
          <CopyField label="Topic or channel" value={code.bindCommand} copied={copied === 'command'} onCopy={() => void copy('command', code.bindCommand)}
            hint="Send this in a forum topic, or post it in a channel where the bot is an admin." />
          <CopyField label="Code" value={code.code} copied={copied === 'code'} onCopy={() => void copy('code', code.code)} />
          <p className="hint">The bot needs to see all messages in groups (privacy mode off in @BotFather) to take book-chat messages.</p>
        </div>
      )}

      {state.bindings.length === 0
        ? <p className="hint">No chat is connected yet.</p>
        : (
          <div className="telegram-bindings">
            {state.bindings.map(b => (
              <div key={b.id} className="telegram-binding">
                <div className="telegram-binding-head">
                  <span>{CHAT_ICONS[b.chatType]} <b>{b.chatTitle}</b>{b.isTopic ? ' (topic)' : ''}</span>
                  <span className="hint">since {new Date(b.createdAt).toLocaleDateString()}</span>
                </div>
                <div className="telegram-binding-fields">
                  <label>
                    Access
                    <select value={b.level} disabled={b.chatType === 'channel'}
                      onChange={e => void update(b, { level: e.target.value as TelegramAccessLevel })}>
                      {LEVELS.map(l => <option key={l.value} value={l.value}>{l.label}</option>)}
                    </select>
                  </label>
                  <label>
                    Files
                    <select value={b.fileFormat} onChange={e => void update(b, { fileFormat: e.target.value as TelegramFileFormat })}>
                      <option value="epub">EPUB</option>
                      <option value="fb2">FB2</option>
                      <option value="html">HTML</option>
                    </select>
                  </label>
                </div>
                {b.level === 'Control' && b.chatType !== 'private' && b.chatType !== 'channel' && (
                  <ToggleField checked={b.controlAdminsOnly} onChange={v => void update(b, { controlAdminsOnly: v })}
                    label="Only chat admins can start and stop runs"
                    hint="Checked against the members' Telegram admin rights." />
                )}
                {b.chatType === 'private' && (
                  <ToggleField checked={b.liveDrafts} onChange={v => void update(b, { liveDrafts: v })}
                    label="Live drafts"
                    hint="Show the chapter as it is being written, as a draft with a stop button (Control)." />
                )}
                <div className="actions">
                  <button type="button" className="delete-btn btn-sm" onClick={() => void revoke(b)}>Disconnect</button>
                </div>
              </div>
            ))}
          </div>
        )}
    </div>
  )
}
