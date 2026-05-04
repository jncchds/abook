import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { createPlotThread, updatePlotThread, deletePlotThread, getPlotThreadsHistory, getPlotThreadsSnapshot, restorePlotThreadsSnapshot } from '../../api'
import type { PlotThread, PlotThreadsSnapshotMeta } from '../../api'
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
  const [showHistory, setShowHistory] = useState(false)
  const [snapshots, setSnapshots] = useState<PlotThreadsSnapshotMeta[]>([])
  const [previewData, setPreviewData] = useState<{ id: number; threads: PlotThread[] } | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [restoring, setRestoring] = useState(false)

  useRestoreStream(book?.id, isRunning, plotThreadsStream, 'PlotThreadsAgent', undefined, setPlotThreadsStream)

  if (!book) return null

  const bookId = book.id

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try {
      const r = await getPlotThreadsHistory(bookId)
      setSnapshots(r.data)
    } finally {
      setLoadingHistory(false)
    }
    setShowHistory(true)
    setPreviewData(null)
  }

  const handlePreview = async (snapshotId: number) => {
    if (previewData?.id === snapshotId) { setPreviewData(null); return }
    const r = await getPlotThreadsSnapshot(bookId, snapshotId)
    try {
      const threads = JSON.parse(r.data.dataJson) as PlotThread[]
      setPreviewData({ id: snapshotId, threads })
    } catch { setPreviewData({ id: snapshotId, threads: [] }) }
  }

  const handleRestore = async (snapshotId: number) => {
    if (!confirm('Restore this snapshot? Current plot threads will be replaced.')) return
    setRestoring(true)
    try {
      const r = await restorePlotThreadsSnapshot(bookId, snapshotId)
      setPlotThreads(r.data)
      setShowHistory(false)
    } finally {
      setRestoring(false)
    }
  }

  if (showHistory) {
    return (
      <div className="view-content">
        <div className="history-panel-header">
          <h3>📜 Plot Threads History</h3>
          <button className="btn-sm btn-ghost" onClick={() => setShowHistory(false)}>✕ Close</button>
        </div>
        {loadingHistory && <p className="empty">Loading…</p>}
        {!loadingHistory && snapshots.length === 0 && <p className="empty">No snapshots yet.</p>}
        <div className="history-list">
          {snapshots.map(s => (
            <div key={s.id} className={`history-item ${previewData?.id === s.id ? 'history-item-active' : ''}`}>
              <div className="history-item-meta">
                <span className="history-source-badge">{s.source}</span>
                <span className="history-reason">{s.reason || 'snapshot'}</span>
                <span className="history-date">{new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <div className="history-item-actions">
                <button className="btn-sm btn-ghost" onClick={() => handlePreview(s.id)}>
                  {previewData?.id === s.id ? 'Hide' : 'Preview'}
                </button>
                <button className="btn-sm" disabled={restoring} onClick={() => handleRestore(s.id)}>Restore</button>
              </div>
              {previewData?.id === s.id && (
                <div className="history-preview">
                  {previewData.threads.map((t, i) => (
                    <div key={i} className="history-preview-card">
                      <strong>{t.name}</strong>
                      <span className="thread-type-badge">{t.type}</span>
                      <span className={`thread-status-badge status-${t.status?.toLowerCase()}`}>{t.status}</span>
                      {t.description && <p>{t.description}</p>}
                    </div>
                  ))}
                  {previewData.threads.length === 0 && <p className="empty">Empty snapshot.</p>}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="view-content">
      <div className="view-header">
        <h2>Plot Threads ({plotThreads.length})</h2>
        <button className="btn-sm" onClick={() => { setThreadForm({}); setAddingThread(true); setEditingThreadId(null) }}>+ Add</button>
      </div>
      <PhaseActionBar phase="plotthreads" onClear={() => setPlotThreads([])} onHistory={handleOpenHistory} style={{ marginBottom: '1rem' }} />

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
