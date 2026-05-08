import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import {
  createPlotThread, updatePlotThread, archivePlotThread, unarchivePlotThread,
  getPlotThreadHistory, restorePlotThreadVersion,
  getPlotThreadsHistory, getPlotThreadsSnapshot, restorePlotThreadsSnapshot,
} from '../../api'
import type { PlotThread, PlotThreadVersion, PlotThreadsSnapshotMeta } from '../../api'
import { parsePlotThreadsStream } from '../../utils/streamParsers'
import PhaseActionBar from '../../components/PhaseActionBar'
import { useRestoreStream } from '../../hooks/useRestoreStream'

export default function PlotThreads() {
  const {
    book, plotThreads, setPlotThreads, plotThreadsStream, setPlotThreadsStream,
    isRunning,
  } = useBookContext()

  const [editingId, setEditingId] = useState<number | null>(null)
  const [addingThread, setAddingThread] = useState(false)
  const [form, setForm] = useState<Partial<PlotThread>>({})
  const [showArchived, setShowArchived] = useState(false)

  const [showHistory, setShowHistory] = useState(false)
  const [snapshots, setSnapshots] = useState<PlotThreadsSnapshotMeta[]>([])
  const [previewData, setPreviewData] = useState<{ id: number; threads: PlotThread[] } | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [restoring, setRestoring] = useState(false)

  const [itemHistoryId, setItemHistoryId] = useState<number | null>(null)
  const [itemVersions, setItemVersions] = useState<PlotThreadVersion[]>([])
  const [loadingItemHistory, setLoadingItemHistory] = useState(false)
  const [restoringVersion, setRestoringVersion] = useState(false)

  useRestoreStream(book?.id, isRunning, plotThreadsStream, 'PlotThreadsAgent', undefined, setPlotThreadsStream)

  if (!book) return null

  const bookId = book.id
  const activeThreads = plotThreads.filter(t => !t.isArchived)
  const archivedThreads = plotThreads.filter(t => t.isArchived)

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try { const r = await getPlotThreadsHistory(bookId); setSnapshots(r.data) }
    finally { setLoadingHistory(false) }
    setShowHistory(true); setPreviewData(null)
  }

  const handlePreview = async (snapshotId: number) => {
    if (previewData?.id === snapshotId) { setPreviewData(null); return }
    const r = await getPlotThreadsSnapshot(bookId, snapshotId)
    try { setPreviewData({ id: snapshotId, threads: JSON.parse(r.data.dataJson) as PlotThread[] }) }
    catch { setPreviewData({ id: snapshotId, threads: [] }) }
  }

  const handleRestore = async (snapshotId: number) => {
    if (!confirm('Restore this snapshot? Current plot threads will be replaced.')) return
    setRestoring(true)
    try { const r = await restorePlotThreadsSnapshot(bookId, snapshotId); setPlotThreads(r.data); setShowHistory(false) }
    finally { setRestoring(false) }
  }

  const handleOpenItemHistory = async (threadId: number) => {
    if (itemHistoryId === threadId) { setItemHistoryId(null); return }
    setLoadingItemHistory(true)
    try { const r = await getPlotThreadHistory(bookId, threadId); setItemVersions(r.data) }
    finally { setLoadingItemHistory(false) }
    setItemHistoryId(threadId)
  }

  const handleRestoreVersion = async (threadId: number, versionId: number) => {
    if (!confirm('Restore this version?')) return
    setRestoringVersion(true)
    try {
      const r = await restorePlotThreadVersion(bookId, threadId, versionId)
      setPlotThreads(prev => prev.map(t => t.id === threadId ? r.data : t))
      setItemHistoryId(null)
    } finally { setRestoringVersion(false) }
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
            <div key={s.id} className={"history-item" + (previewData?.id === s.id ? ' history-item-active' : '')}>
              <div className="history-item-meta">
                <span className="history-source-badge">{s.source}</span>
                <span className="history-reason">{s.reason || 'snapshot'}</span>
                <span className="history-date">{new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <div className="history-item-actions">
                <button className="btn-sm btn-ghost" onClick={() => handlePreview(s.id)}>{previewData?.id === s.id ? 'Hide' : 'Preview'}</button>
                <button className="btn-sm" disabled={restoring} onClick={() => handleRestore(s.id)}>Restore</button>
              </div>
              {previewData?.id === s.id && (
                <div className="history-preview">
                  {previewData.threads.map((t, i) => (
                    <div key={i} className="history-preview-card">
                      <strong>{t.name}</strong>
                      <span className={"thread-type-badge"}>{t.type}</span>
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
        <h2>Plot Threads ({activeThreads.length}{archivedThreads.length > 0 ? ' + ' + archivedThreads.length + ' archived' : ''})</h2>
        <button className="btn-sm" onClick={() => { setForm({}); setAddingThread(true); setEditingId(null) }}>+ Add</button>
      </div>
      <PhaseActionBar phase="plotthreads" onClear={() => setPlotThreads([])} onHistory={handleOpenHistory} style={{ marginBottom: '1rem' }} />

      {addingThread && (
        <div className="card" style={{ maxWidth: 560, marginBottom: '1rem' }}>
          <h3>Add Plot Thread</h3>
          <label>Name<input autoFocus value={form.name ?? ''} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} /></label>
          <label>Type<select value={form.type ?? 'Main'} onChange={e => setForm(f => ({ ...f, type: e.target.value }))}>{['Main','Subplot','Character','World'].map(t => <option key={t}>{t}</option>)}</select></label>
          <label>Description<textarea rows={3} value={form.description ?? ''} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} /></label>
          <label>Status<select value={form.status ?? 'Open'} onChange={e => setForm(f => ({ ...f, status: e.target.value }))}>{['Open','Resolved','Abandoned'].map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Introduced Chapter #<input type="number" value={form.introducedChapterNumber ?? ''} onChange={e => setForm(f => ({ ...f, introducedChapterNumber: e.target.value ? Number(e.target.value) : undefined }))} /></label>
          <div className="actions">
            <button onClick={async () => {
              if (!form.name?.trim()) return
              const r = await createPlotThread(bookId, form as PlotThread)
              setPlotThreads(prev => [...prev, r.data]); setAddingThread(false); setForm({})
            }}>Save</button>
            <button className="btn-ghost" onClick={() => { setAddingThread(false); setForm({}) }}>Cancel</button>
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
                    {t.status && <span className={"thread-status-" + (t.status as string).toLowerCase()}>{t.status as string}</span>}
                  </div>
                  {t.description && <p className="blc-premise">{t.description}</p>}
                </div>
              </div>
            ))}
          </div>
        )
      })()}

      {activeThreads.length === 0 && !addingThread && !plotThreadsStream && <p className="empty">No plot threads yet. Run the Planner or add manually.</p>}

      <div className="book-list">
        {activeThreads.map(t => (
          <div key={t.id} className="book-list-card">
            <div className="book-list-card-left">
              {editingId === t.id ? (
                <div className="char-edit-form">
                  <label>Name<input autoFocus value={form.name ?? t.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} /></label>
                  <label>Type<select value={form.type ?? t.type} onChange={e => setForm(f => ({ ...f, type: e.target.value }))}>{['Main','Subplot','Character','World'].map(tp => <option key={tp}>{tp}</option>)}</select></label>
                  <label>Description<textarea rows={3} value={form.description ?? t.description ?? ''} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} /></label>
                  <label>Status<select value={form.status ?? t.status} onChange={e => setForm(f => ({ ...f, status: e.target.value }))}>{['Open','Resolved','Abandoned'].map(s => <option key={s}>{s}</option>)}</select></label>
                  <label>Introduced Chapter #<input type="number" value={form.introducedChapterNumber ?? t.introducedChapterNumber ?? ''} onChange={e => setForm(f => ({ ...f, introducedChapterNumber: e.target.value ? Number(e.target.value) : undefined }))} /></label>
                  <label>Resolved Chapter #<input type="number" value={form.resolvedChapterNumber ?? t.resolvedChapterNumber ?? ''} onChange={e => setForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? Number(e.target.value) : undefined }))} /></label>
                  <div className="actions">
                    <button onClick={async () => {
                      const r = await updatePlotThread(bookId, t.id, { ...t, ...form } as PlotThread)
                      setPlotThreads(prev => prev.map(p => p.id === t.id ? r.data : p))
                      setEditingId(null); setForm({})
                    }}>Save</button>
                    <button className="btn-ghost" onClick={() => { setEditingId(null); setForm({}) }}>Cancel</button>
                  </div>
                </div>
              ) : (
                <>
                  <h3>{t.name}</h3>
                  <div className="blc-meta">
                    <span className="thread-type-badge">{t.type}</span>
                    <span className={"thread-status-" + t.status?.toLowerCase()}>{t.status}</span>
                    {t.introducedChapterNumber != null && <span>Ch.{t.introducedChapterNumber}</span>}
                  </div>
                  {t.description && <p className="blc-premise">{t.description}</p>}
                  {itemHistoryId === t.id && (
                    <div className="item-history-panel">
                      <div className="history-panel-header" style={{ marginTop: '0.75rem' }}>
                        <strong>📜 Version History</strong>
                        <button className="btn-sm btn-ghost" onClick={() => setItemHistoryId(null)}>✕</button>
                      </div>
                      {loadingItemHistory && <p className="empty">Loading…</p>}
                      {!loadingItemHistory && itemVersions.length === 0 && <p className="empty">No versions yet.</p>}
                      <div className="history-list">
                        {itemVersions.map(v => (
                          <div key={v.id} className="history-item">
                            <div className="history-item-meta">
                              <span className="history-source-badge">v{v.versionNumber}</span>
                              <span className="history-reason">{v.createdBy}</span>
                              <span className="history-date">{new Date(v.createdAt).toLocaleString()}</span>
                            </div>
                            <div className="history-preview-card" style={{ marginTop: '0.25rem' }}>
                              <strong>{v.name}</strong>
                              <span className="thread-type-badge">{v.type}</span>
                              <span className={"thread-status-" + v.status?.toLowerCase()}>{v.status}</span>
                              {v.description && <p>{v.description}</p>}
                            </div>
                            <div className="history-item-actions">
                              <button className="btn-sm" disabled={restoringVersion}
                                onClick={() => handleRestoreVersion(t.id, v.id)}>↩ Restore</button>
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </>
              )}
            </div>
            {editingId !== t.id && (
              <div className="book-list-card-right">
                <button className="btn-icon" title="Edit" onClick={() => { setForm({}); setEditingId(t.id); setAddingThread(false) }}>✎</button>
                <button className="btn-icon" title="Version History" onClick={() => handleOpenItemHistory(t.id)}>📜</button>
                <button className="btn-icon" title="Archive" onClick={async () => {
                  await archivePlotThread(bookId, t.id)
                  setPlotThreads(prev => prev.map(p => p.id === t.id ? { ...p, isArchived: true } : p))
                }}>🗄</button>
              </div>
            )}
          </div>
        ))}
      </div>

      {archivedThreads.length > 0 && (
        <div style={{ marginTop: '1.5rem' }}>
          <button className="btn-sm btn-ghost" onClick={() => setShowArchived(v => !v)}>
            {showArchived ? '▲ Hide archived' : '▼ Show archived (' + archivedThreads.length + ')'}
          </button>
          {showArchived && (
            <div className="book-list" style={{ marginTop: '0.5rem', opacity: 0.6 }}>
              {archivedThreads.map(t => (
                <div key={t.id} className="book-list-card">
                  <div className="book-list-card-left">
                    <h3>{t.name} <span className="history-source-badge">archived</span></h3>
                    <div className="blc-meta">
                      <span className="thread-type-badge">{t.type}</span>
                      <span className={"thread-status-" + t.status?.toLowerCase()}>{t.status}</span>
                    </div>
                    {t.description && <p className="blc-premise">{t.description}</p>}
                  </div>
                  <div className="book-list-card-right">
                    <button className="btn-sm" title="Unarchive" onClick={async () => {
                      const r = await unarchivePlotThread(bookId, t.id)
                      setPlotThreads(prev => prev.map(p => p.id === t.id ? r.data : p))
                    }}>♻ Restore</button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
