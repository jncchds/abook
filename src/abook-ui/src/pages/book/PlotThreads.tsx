import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { createPlotThread, updatePlotThread, deletePlotThread } from '../../api'
import type { PlotThread } from '../../api'
import { parsePlotThreadsStream } from '../../utils/streamParsers'
import PhaseActionBar from '../../components/PhaseActionBar'
import { useRestoreStream } from '../../hooks/useRestoreStream'

export default function PlotThreads() {
  const {
    book, plotThreads, setPlotThreads, plotThreadsStream, setPlotThreadsStream,
    isRunning,
  } = useBookContext()

  const [editingThreadId, setEditingThreadId] = useState<number | null>(null)
  const [addingThread, setAddingThread] = useState(false)
  const [threadForm, setThreadForm] = useState<Partial<PlotThread>>({})

  useRestoreStream(book?.id, isRunning, plotThreadsStream, 'PlotThreadsAgent', undefined, setPlotThreadsStream)

  if (!book) return null

  const bookId = book.id

  return (
    <div className="view-content">
      <div className="view-header">
        <h2>Plot Threads ({plotThreads.length})</h2>
        <button className="btn-sm" onClick={() => { setThreadForm({}); setAddingThread(true); setEditingThreadId(null) }}>+ Add</button>
      </div>
      <PhaseActionBar phase="plotthreads" onClear={() => setPlotThreads([])} style={{ marginBottom: '1rem' }} />

      {addingThread && (
        <div className="card" style={{ maxWidth: 560, marginBottom: '1rem' }}>
          <h3>Add Plot Thread</h3>
          <label>Name<input autoFocus value={threadForm.name ?? ''} onChange={e => setThreadForm(f => ({ ...f, name: e.target.value }))} /></label>
          <label>Description<textarea rows={3} value={threadForm.description ?? ''} onChange={e => setThreadForm(f => ({ ...f, description: e.target.value }))} /></label>
          <label>Type<select value={threadForm.type ?? 'MainPlot'} onChange={e => setThreadForm(f => ({ ...f, type: e.target.value }))}>{['MainPlot','Subplot','CharacterArc','Mystery','Foreshadowing','WorldBuilding','ThematicThread'].map(t => <option key={t}>{t}</option>)}</select></label>
          <label>Status<select value={threadForm.status ?? 'Active'} onChange={e => setThreadForm(f => ({ ...f, status: e.target.value }))}>{['Active','Resolved','Dormant'].map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Introduced Ch.<input type="number" value={threadForm.introducedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, introducedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
          <label>Resolved Ch.<input type="number" value={threadForm.resolvedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
          <div className="actions">
            <button onClick={async () => {
              if (!threadForm.name?.trim()) return
              const r = await createPlotThread(bookId, threadForm as PlotThread)
              setPlotThreads(prev => [...prev, r.data])
              setAddingThread(false)
              setThreadForm({})
            }}>Save</button>
            <button className="btn-ghost" onClick={() => { setAddingThread(false); setThreadForm({}) }}>Cancel</button>
          </div>
        </div>
      )}

      {plotThreadsStream && (() => {
        const preview = parsePlotThreadsStream(plotThreadsStream)
        return (
          <div className="book-list" style={{ marginBottom: '1rem' }}>
            {preview.map((t, i) => (
              <div key={i} className="book-list-card">
                <div className="book-list-card-left">
                  <h3>{t.name}</h3>
                  <div className="blc-meta">
                    {t.type && <span className="thread-type-badge">{t.type as string}</span>}
                    {t.status && <span className={`thread-status-badge status-${(t.status as string).toLowerCase()}`}>{t.status as string}</span>}
                  </div>
                  {t.description && <p className="blc-premise">{t.description}</p>}
                  {(t.introducedChapterNumber != null || t.resolvedChapterNumber != null) && (
                    <div className="thread-meta">
                      {t.introducedChapterNumber != null && <span>Intro: Ch.{t.introducedChapterNumber}</span>}
                      {t.resolvedChapterNumber != null && <span>Resolved: Ch.{t.resolvedChapterNumber}</span>}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
        )
      })()}

      {plotThreads.length === 0 && !addingThread && !plotThreadsStream && <p className="empty">No plot threads yet. Run the Planner or add manually.</p>}

      <div className="book-list">
        {plotThreads.map(t => (
          <div key={t.id} className="book-list-card">
            <div className="book-list-card-left">
              {editingThreadId === t.id ? (
                <div className="thread-edit-form">
                  <label>Name<input autoFocus value={threadForm.name ?? t.name} onChange={e => setThreadForm(f => ({ ...f, name: e.target.value }))} /></label>
                  <label>Description<textarea rows={3} value={threadForm.description ?? t.description ?? ''} onChange={e => setThreadForm(f => ({ ...f, description: e.target.value }))} /></label>
                  <label>Type<select value={threadForm.type ?? t.type} onChange={e => setThreadForm(f => ({ ...f, type: e.target.value }))}>{['MainPlot','Subplot','CharacterArc','Mystery','Foreshadowing','WorldBuilding','ThematicThread'].map(tt => <option key={tt}>{tt}</option>)}</select></label>
                  <label>Status<select value={threadForm.status ?? t.status} onChange={e => setThreadForm(f => ({ ...f, status: e.target.value }))}>{['Active','Resolved','Dormant'].map(s => <option key={s}>{s}</option>)}</select></label>
                  <label>Introduced Ch.<input type="number" value={threadForm.introducedChapterNumber ?? t.introducedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, introducedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
                  <label>Resolved Ch.<input type="number" value={threadForm.resolvedChapterNumber ?? t.resolvedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
                  <div className="actions">
                    <button onClick={async () => {
                      const r = await updatePlotThread(bookId, t.id, { ...t, ...threadForm } as PlotThread)
                      setPlotThreads(prev => prev.map(x => x.id === t.id ? r.data : x))
                      setEditingThreadId(null)
                      setThreadForm({})
                    }}>Save</button>
                    <button className="btn-ghost" onClick={() => { setEditingThreadId(null); setThreadForm({}) }}>Cancel</button>
                  </div>
                </div>
              ) : (
                <>
                  <h3>{t.name}</h3>
                  <div className="blc-meta">
                    <span className="thread-type-badge">{t.type}</span>
                    <span className={`thread-status-badge status-${t.status?.toLowerCase()}`}>{t.status}</span>
                  </div>
                  {t.description && <p className="blc-premise">{t.description}</p>}
                  <div className="thread-meta">
                    {t.introducedChapterNumber != null && <span>Intro: Ch.{t.introducedChapterNumber}</span>}
                    {t.resolvedChapterNumber != null && <span>Resolved: Ch.{t.resolvedChapterNumber}</span>}
                  </div>
                </>
              )}
            </div>
            {editingThreadId !== t.id && (
              <div className="book-list-card-right">
                <button className="btn-icon" onClick={() => { setThreadForm({}); setEditingThreadId(t.id); setAddingThread(false) }}>✎</button>
                <button className="btn-icon btn-danger" onClick={async () => {
                  if (!confirm(`Delete plot thread "${t.name}"?`)) return
                  await deletePlotThread(bookId, t.id)
                  setPlotThreads(prev => prev.filter(x => x.id !== t.id))
                }}>✕</button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
