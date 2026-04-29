import { useState, useEffect } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { createPlotThread, updatePlotThread, deletePlotThread, getStreamBuffer } from '../../api'
import type { PlotThread } from '../../api'
import { parsePlotThreadsStream } from '../../utils/streamParsers'

export default function PlotThreads() {
  const {
    book, plotThreads, setPlotThreads, plotThreadsStream, setPlotThreadsStream,
    isPhaseComplete, handleCompletePhase, handleReopenPhase, handleClearPhase,
    isRunning,
  } = useBookContext()

  const [editingThreadId, setEditingThreadId] = useState<number | null>(null)
  const [addingThread, setAddingThread] = useState(false)
  const [threadForm, setThreadForm] = useState<Partial<PlotThread>>({})

  // Restore in-progress stream on hard-refresh
  useEffect(() => {
    if (!book || !isRunning || plotThreadsStream) return
    getStreamBuffer(book.id, 'PlotThreadsAgent').then(r => {
      if (r.data.content) setPlotThreadsStream(r.data.content)
    }).catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [book?.id])

  if (!book) return null

  const bookId = book.id

  return (
    <div className="plotthreads-view">
      <div className="plotthreads-header">
        <h3>Plot Threads ({plotThreads.length})</h3>
        <button className="btn-sm" onClick={() => { setThreadForm({}); setAddingThread(true); setEditingThreadId(null) }}>+ Add</button>
      </div>
      <div className="phase-actions">
        {isPhaseComplete('plotthreads') ? (
          <>
            <span className="phase-status-badge phase-complete">✅ Complete</span>
            <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('plotthreads')}>↺ Reopen</button>
          </>
        ) : (
          <>
            <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
            <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('plotthreads')}>✓ Complete</button>
          </>
        )}
        <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('plotthreads', () => setPlotThreads([]))}>🗑 Clear</button>
      </div>
      {addingThread && (
        <div className="thread-edit-form">
          <label>Name<input autoFocus value={threadForm.name ?? ''} onChange={e => setThreadForm(f => ({ ...f, name: e.target.value }))} /></label>
          <label>Description<textarea rows={3} value={threadForm.description ?? ''} onChange={e => setThreadForm(f => ({ ...f, description: e.target.value }))} /></label>
          <label>Type<select value={threadForm.type ?? 'MainPlot'} onChange={e => setThreadForm(f => ({ ...f, type: e.target.value }))}>{['MainPlot','Subplot','CharacterArc','Mystery','Foreshadowing','WorldBuilding','ThematicThread'].map(t => <option key={t}>{t}</option>)}</select></label>
          <label>Status<select value={threadForm.status ?? 'Active'} onChange={e => setThreadForm(f => ({ ...f, status: e.target.value }))}>{['Active','Resolved','Dormant'].map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Introduced Ch.<input type="number" value={threadForm.introducedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, introducedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
          <label>Resolved Ch.<input type="number" value={threadForm.resolvedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
          <div className="thread-edit-actions">
            <button className="btn-sm" onClick={async () => {
              if (!threadForm.name?.trim()) return
              const r = await createPlotThread(bookId, threadForm as PlotThread)
              setPlotThreads(prev => [...prev, r.data])
              setAddingThread(false)
              setThreadForm({})
            }}>Save</button>
            <button className="btn-sm btn-ghost" onClick={() => { setAddingThread(false); setThreadForm({}) }}>Cancel</button>
          </div>
        </div>
      )}
      {plotThreadsStream && (() => {
        const preview = parsePlotThreadsStream(plotThreadsStream)
        return (
          <>
            {preview.map((t, i) => (
              <div key={i} className={`thread-card status-${(t.status as string | undefined)?.toLowerCase() ?? 'active'}`}>
                <div className="thread-card-header">
                  <strong>{t.name}</strong>
                  {t.type && <span className="thread-type-badge">{t.type as string}</span>}
                </div>
                {t.description && <p className="thread-desc">{t.description}</p>}
              </div>
            ))}
          </>
        )
      })()}
      {plotThreads.length === 0 && !addingThread && !plotThreadsStream && <p className="empty">No plot threads yet. Run the Planner or add manually.</p>}
      {plotThreads.map(t => (
        <div key={t.id} className={`thread-card status-${t.status?.toLowerCase()}`}>
          {editingThreadId === t.id ? (
            <div className="thread-edit-form">
              <label>Name<input autoFocus value={threadForm.name ?? t.name} onChange={e => setThreadForm(f => ({ ...f, name: e.target.value }))} /></label>
              <label>Description<textarea rows={3} value={threadForm.description ?? t.description ?? ''} onChange={e => setThreadForm(f => ({ ...f, description: e.target.value }))} /></label>
              <label>Type<select value={threadForm.type ?? t.type} onChange={e => setThreadForm(f => ({ ...f, type: e.target.value }))}>{['MainPlot','Subplot','CharacterArc','Mystery','Foreshadowing','WorldBuilding','ThematicThread'].map(tt => <option key={tt}>{tt}</option>)}</select></label>
              <label>Status<select value={threadForm.status ?? t.status} onChange={e => setThreadForm(f => ({ ...f, status: e.target.value }))}>{['Active','Resolved','Dormant'].map(s => <option key={s}>{s}</option>)}</select></label>
              <label>Introduced Ch.<input type="number" value={threadForm.introducedChapterNumber ?? t.introducedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, introducedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
              <label>Resolved Ch.<input type="number" value={threadForm.resolvedChapterNumber ?? t.resolvedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
              <div className="thread-edit-actions">
                <button className="btn-sm" onClick={async () => {
                  const r = await updatePlotThread(bookId, t.id, { ...t, ...threadForm } as PlotThread)
                  setPlotThreads(prev => prev.map(x => x.id === t.id ? r.data : x))
                  setEditingThreadId(null)
                  setThreadForm({})
                }}>Save</button>
                <button className="btn-sm btn-ghost" onClick={() => { setEditingThreadId(null); setThreadForm({}) }}>Cancel</button>
              </div>
            </div>
          ) : (
            <>
              <div className="thread-card-header">
                <strong>{t.name}</strong>
                <span className="thread-type-badge">{t.type}</span>
                <span className={`thread-status-badge status-${t.status?.toLowerCase()}`}>{t.status}</span>
                <div className="thread-card-actions">
                  <button className="btn-icon" onClick={() => { setThreadForm({}); setEditingThreadId(t.id); setAddingThread(false) }}>✎</button>
                  <button className="btn-icon btn-danger" onClick={async () => {
                    if (!confirm(`Delete plot thread "${t.name}"?`)) return
                    await deletePlotThread(bookId, t.id)
                    setPlotThreads(prev => prev.filter(x => x.id !== t.id))
                  }}>✕</button>
                </div>
              </div>
              {t.description && <p className="thread-desc">{t.description}</p>}
              <div className="thread-meta">
                {t.introducedChapterNumber != null && <span>Intro: Ch.{t.introducedChapterNumber}</span>}
                {t.resolvedChapterNumber != null && <span>Resolved: Ch.{t.resolvedChapterNumber}</span>}
              </div>
            </>
          )}
        </div>
      ))}
    </div>
  )
}
