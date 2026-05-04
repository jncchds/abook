import { useBookContext } from '../../contexts/BookContext'

export default function TokenStatsPage() {
  const { book, tokenStats, isRunning, clearTokenUsageForBook } = useBookContext()

  if (!book) return null

  const chapterLabel = (id: number | null) => {
    if (id === null) return '—'
    const ch = book.chapters?.find(c => c.id === id)
    return ch ? `Ch. ${ch.number}` : `#${id}`
  }

  return (
    <div className="view-content">
      <div className="view-header">
        <h2>🪙 Token Stats</h2>
        {tokenStats.length > 0 && !isRunning && (
          <button className="btn-sm btn-danger" onClick={clearTokenUsageForBook}>� Archive</button>
        )}
      </div>
      {tokenStats.length === 0 ? (
        <p className="empty">No token usage recorded yet.</p>
      ) : (() => {
        const totalPrompt = tokenStats.reduce((a, s) => a + s.prompt, 0)
        const totalCompletion = tokenStats.reduce((a, s) => a + s.completion, 0)
        const totalAll = totalPrompt + totalCompletion
        return (
          <div className="token-stats-list" style={{ maxHeight: 'none', overflowX: 'auto' }}>
            <table>
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Agent</th>
                  <th>Chapter</th>
                  <th>Model</th>
                  <th>Endpoint</th>
                  <th style={{ textAlign: 'right' }}>Prompt</th>
                  <th style={{ textAlign: 'right' }}>Completion</th>
                  <th style={{ textAlign: 'right' }}>Total</th>
                </tr>
              </thead>
              <tbody>
                {tokenStats.map(s => (
                  <tr key={s.id} style={s.failed ? { color: 'var(--error)', opacity: 0.85 } : undefined}>
                    <td>{s.time}</td>
                    <td title={s.failed ? 'LLM call failed — counts are partial' : undefined}>
                      {s.failed ? '❌ ' : ''}{s.role}
                    </td>
                    <td>{chapterLabel(s.chapterId)}</td>
                    <td>{s.modelName ?? '—'}</td>
                    <td style={{ fontFamily: 'monospace', fontSize: '0.8em' }}>{s.endpoint ?? '—'}</td>
                    <td style={{ textAlign: 'right' }}>{s.prompt.toLocaleString()}</td>
                    <td style={{ textAlign: 'right' }} title={s.failed ? 'partial' : undefined}>
                      {s.failed ? '~' : ''}{s.completion.toLocaleString()}
                    </td>
                    <td style={{ textAlign: 'right', fontWeight: 600 }}>
                      {s.failed ? '~' : ''}{(s.prompt + s.completion).toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr style={{ borderTop: '2px solid var(--border)', fontWeight: 700 }}>
                  <td colSpan={5}>Totals ({tokenStats.length} calls)</td>
                  <td style={{ textAlign: 'right' }}>{totalPrompt.toLocaleString()}</td>
                  <td style={{ textAlign: 'right' }}>{totalCompletion.toLocaleString()}</td>
                  <td style={{ textAlign: 'right' }}>{totalAll.toLocaleString()}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        )
      })()}
    </div>
  )
}
