import { useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import axios from 'axios'
import { useBookContext } from '../../contexts/BookContext'
import { updateChapter, clearChapterContent, archiveChapter, restoreChapter, getChapterVersions, getChapterVersion, activateChapterVersion, getChapters, splitChapter } from '../../api'
import type { ChapterVersionMeta, ChapterVersionFull } from '../../api'
import { chapterStatusColor } from '../../utils/chapterStatus'
import ChapterContent from '../../components/ChapterContent'
import IllustrationFigure from '../../components/IllustrationFigure'
import { splitParagraphs } from '../../utils/paragraphs'

export default function ChapterView() {
  const { chapterId } = useParams<{ chapterId: string }>()
  const navigate = useNavigate()
  const {
    book, setBook, streamBuffer, streamingChapterId, isRunning, canEdit, isWaitingForInput,
    illustrations, characters, refreshIllustrations, handlePlanIllustrations, handleRenderIllustrations,
  } = useBookContext()

  const [editingChapter, setEditingChapter] = useState(false)
  const [chapterEditTitle, setChapterEditTitle] = useState('')
  const [chapterEditOutline, setChapterEditOutline] = useState('')
  const [chapterEditContent, setChapterEditContent] = useState('')
  const [savingChapter, setSavingChapter] = useState(false)
  const [showHistory, setShowHistory] = useState(false)
  const [versions, setVersions] = useState<ChapterVersionMeta[]>([])
  const [previewVersion, setPreviewVersion] = useState<ChapterVersionFull | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [activating, setActivating] = useState(false)
  // Split form (opened from the Chapters page with ?split=1)
  const [searchParams, setSearchParams] = useSearchParams()
  const splitting = searchParams.get('split') === '1'
  const [splitFirstOutline, setSplitFirstOutline] = useState<string | null>(null)
  const [splitSecondTitle, setSplitSecondTitle] = useState('')
  const [splitSecondOutline, setSplitSecondOutline] = useState('')
  const [splitBusy, setSplitBusy] = useState(false)
  const [splitError, setSplitError] = useState<string | null>(null)

  const chapter = book?.chapters?.find(c => c.id === Number(chapterId))
  if (!book) return null
  if (!chapter) return <p className="empty">Chapter not found.</p>

  const bookId = book.id
  const reloadChapters = async () => {
    const r = await getChapters(bookId)
    setBook(prev => prev ? { ...prev, chapters: r.data } : prev)
  }

  const closeSplit = () => {
    setSearchParams({}, { replace: true })
    setSplitFirstOutline(null)
    setSplitSecondTitle('')
    setSplitSecondOutline('')
    setSplitError(null)
  }

  const firstOutline = splitFirstOutline ?? chapter.outline ?? ''
  const handleSplit = async () => {
    setSplitBusy(true)
    setSplitError(null)
    try {
      await splitChapter(bookId, chapter.id, {
        firstOutline: firstOutline.trim(),
        secondTitle: splitSecondTitle.trim(),
        secondOutline: splitSecondOutline.trim(),
      })
      await reloadChapters()
      closeSplit()
    } catch (err) {
      setSplitError(axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err))
    } finally {
      setSplitBusy(false)
    }
  }
  const statusColor = chapterStatusColor
  const scenes = illustrations.filter(i => i.kind === 'Scene' && i.chapterId === chapter.id)
  const unrenderedScenes = scenes.filter(i => i.status !== 'Rendered').length
  const paragraphCount = splitParagraphs(chapter.content).length

  const startEditingChapter = () => {
    setChapterEditTitle(chapter.title)
    setChapterEditOutline(chapter.outline ?? '')
    setChapterEditContent(chapter.content ?? '')
    setEditingChapter(true)
  }

  const handleSaveChapterEdit = async () => {
    setSavingChapter(true)
    try {
      const r = await updateChapter(bookId, chapter.id, {
        title: chapterEditTitle,
        outline: chapterEditOutline,
        content: chapterEditContent,
        status: chapter.status as never,
      })
      setBook(prev => prev ? { ...prev, chapters: (prev.chapters ?? []).map(c => c.id === r.data.id ? r.data : c) } : prev)
      setEditingChapter(false)
    } finally {
      setSavingChapter(false)
    }
  }

  const handleClearChapter = async () => {
    if (!confirm(`Clear all content for Chapter ${chapter.number}: "${chapter.title}"? This cannot be undone.`)) return
    const r = await clearChapterContent(bookId, chapter)
    setBook(prev => prev ? {
      ...prev,
      chapters: (prev.chapters ?? []).map(c => c.id === r.data.id ? r.data : c)
    } : prev)
  }

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try {
      const r = await getChapterVersions(bookId, chapter.id)
      setVersions(r.data)
    } finally {
      setLoadingHistory(false)
    }
    setShowHistory(true)
    setPreviewVersion(null)
  }

  const handlePreview = async (versionId: number) => {
    if (previewVersion?.id === versionId) { setPreviewVersion(null); return }
    const r = await getChapterVersion(bookId, chapter.id, versionId)
    setPreviewVersion(r.data)
  }

  const handleActivate = async (versionId: number) => {
    if (!confirm('Activate this version? It will replace the current chapter content.')) return
    setActivating(true)
    try {
      const r = await activateChapterVersion(bookId, chapter.id, versionId)
      setBook(prev => prev ? {
        ...prev,
        chapters: (prev.chapters ?? []).map(c => c.id === chapter.id ? r.data.chapter : c)
      } : prev)
      setVersions(prev => prev.map(v => ({ ...v, isActive: v.id === versionId })))
      setShowHistory(false)
    } finally {
      setActivating(false)
    }
  }

  const handleArchive = async () => {
    if (!confirm(`Archive Chapter ${chapter.number}: "${chapter.title}"? It keeps its place and can be restored from the Chapters page; the chapters after it are renumbered.`)) return
    await archiveChapter(bookId, chapter.id)
    await reloadChapters()   // the chapters after it moved up
    navigate(`/books/${bookId}/chapters`)
  }

  const handleRestoreChapter = async () => {
    await restoreChapter(bookId, chapter.id)
    await reloadChapters()   // the chapters after it moved down
  }

  const sortedChapters = (book.chapters ?? []).filter(c => !c.isArchived).sort((a, b) => a.number - b.number)
  const currentIdx = sortedChapters.findIndex(c => c.id === chapter.id)
  const prevChapter = currentIdx > 0 ? sortedChapters[currentIdx - 1] : null
  const nextChapter = currentIdx < sortedChapters.length - 1 ? sortedChapters[currentIdx + 1] : null

  if (showHistory) {
    return (
      <div className="chapter-view">
        <div className="history-panel-header">
          <h3>📜 Chapter {chapter.number} History</h3>
          <button className="btn-sm btn-ghost" onClick={() => setShowHistory(false)}>✕ Close</button>
        </div>
        {loadingHistory && <p className="empty">Loading…</p>}
        {!loadingHistory && versions.length === 0 && <p className="empty">No versions yet.</p>}
        <div className="history-list">
          {versions.map(v => (
            <div key={v.id} className={`history-item ${previewVersion?.id === v.id ? 'history-item-active' : ''}`}>
              <div className="history-item-meta">
                {v.isActive && <span className="history-source-badge">active</span>}
                <span className="history-reason">v{v.versionNumber} — {v.createdBy}</span>
                <span className="history-date">{new Date(v.createdAt).toLocaleString()}</span>
                <span className="history-date">{v.wordCount.toLocaleString()} words</span>
              </div>
              <div className="history-item-actions">
                <button className="btn-sm btn-ghost" onClick={() => handlePreview(v.id)}>
                  {previewVersion?.id === v.id ? 'Hide' : 'Preview'}
                </button>
                {!v.isActive && (
                  <button className="btn-sm" disabled={activating} onClick={() => handleActivate(v.id)}>Activate</button>
                )}
              </div>
              {previewVersion?.id === v.id && (
                <div className="history-preview history-preview-content">
                  <p><strong>{previewVersion.title}</strong></p>
                  {previewVersion.outline && <p><em>{previewVersion.outline}</em></p>}
                  <ReactMarkdown>{previewVersion.content?.slice(0, 800) + (previewVersion.content?.length > 800 ? '…' : '')}</ReactMarkdown>
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    )
  }

  if (splitting && !chapter.isArchived) {
    return (
      <div className="chapter-view">
        <div className="chapter-edit-form">
          <h2>✂ Split Chapter {chapter.number}: {chapter.title}</h2>
          <p className="hint">
            Divide the outline between this chapter and a new one right after it. Both go back to <strong>Outlined</strong>{' '}
            to be written again{chapter.content?.trim() ? ' — the current text stays in this chapter\'s version history' : ''}.
            The chapters after it are renumbered.
          </p>
          <label>Chapter {chapter.number} keeps — outline
            <textarea rows={6} value={firstOutline} onChange={e => setSplitFirstOutline(e.target.value)} />
          </label>
          <label>New Chapter {chapter.number + 1} — title
            <input value={splitSecondTitle} placeholder={`${chapter.title} (II)`} onChange={e => setSplitSecondTitle(e.target.value)} />
          </label>
          <label>New Chapter {chapter.number + 1} — outline
            <textarea rows={6} value={splitSecondOutline} onChange={e => setSplitSecondOutline(e.target.value)} />
          </label>
          {splitError && <p className="error-msg" role="alert">{splitError}</p>}
          <div className="chapter-edit-actions">
            <button disabled={splitBusy || !firstOutline.trim() || !splitSecondOutline.trim()} onClick={handleSplit}>
              {splitBusy ? 'Splitting…' : '✂ Split'}
            </button>
            <button className="btn-ghost" disabled={splitBusy} onClick={closeSplit}>Cancel</button>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="chapter-view">
      {editingChapter ? (
        <div className="chapter-edit-form">
          {isWaitingForInput && (
            <p className="edit-during-pause-hint">
              ✎ The agent is paused — your saved changes are what the next step reads.
            </p>
          )}
          <label>Title<input value={chapterEditTitle} onChange={e => setChapterEditTitle(e.target.value)} /></label>
          <label>Outline<textarea rows={4} value={chapterEditOutline} onChange={e => setChapterEditOutline(e.target.value)} /></label>
          <label>Content<textarea className="chapter-content-editor" rows={20} value={chapterEditContent} onChange={e => setChapterEditContent(e.target.value)} /></label>
          <div className="chapter-edit-actions">
            <button disabled={savingChapter} onClick={handleSaveChapterEdit}>{savingChapter ? 'Saving…' : 'Save'}</button>
            <button className="btn-ghost" disabled={savingChapter} onClick={() => setEditingChapter(false)}>Cancel</button>
          </div>
        </div>
      ) : (
        <>
          <div className="chapter-header">
            <h2>Chapter {chapter.number}: {chapter.title}</h2>
          </div>
          <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', marginBottom: '1rem' }}>
            {canEdit && (
              <span className="phase-status-badge" style={{ background: chapter.status === 'Done' ? 'rgba(34,197,94,0.15)' : statusColor(chapter.status) + '22', color: chapter.status === 'Done' ? '#22c55e' : statusColor(chapter.status) }}>{chapter.status}</span>
            )}
            {chapter.isArchived && <span className="history-source-badge">archived</span>}
            {canEdit && !chapter.isArchived && (
              <button className="btn-icon" onClick={startEditingChapter} title="Edit chapter title, outline and content">✎ Edit</button>
            )}
            <button className="btn-icon" onClick={handleOpenHistory} title="View version history">📜 History</button>
            {canEdit && !chapter.isArchived && chapter.status !== 'Outlined' && (
              <button className="btn-clear-chapter" onClick={handleClearChapter} title="Clear chapter content and reset status to Outlined">↺ Clear</button>
            )}
            {canEdit && !chapter.isArchived && (
              <button className="btn-icon" onClick={() => setSearchParams({ split: '1' })} title="Split this chapter in two by its outline">✂ Split</button>
            )}
            {canEdit && !chapter.isArchived && (
              <button className="btn-archive" onClick={handleArchive} title="Archive this chapter">🗄 Archive</button>
            )}
            {!isRunning && !chapter.isArchived && chapter.content?.trim() && (
              <button className="btn-icon" onClick={() => {
                if (scenes.some(i => !i.imageId) && !confirm('Re-plan this chapter\'s illustrations? Planned ones that were never rendered are replaced.')) return
                handlePlanIllustrations('Chapter', chapter.id)
              }} title="Have the LLM plan this chapter's illustrations (prompts and positions)">🎨 Plan illustrations</button>
            )}
            {!isRunning && unrenderedScenes > 0 && (
              <button className="btn-icon" onClick={() => handleRenderIllustrations('Chapter', chapter.id)}
                title="Render this chapter's illustrations with the image model (unload the LLM first if VRAM is short)">
                ▶ Render {unrenderedScenes} illustration{unrenderedScenes === 1 ? '' : 's'}
              </button>
            )}
            {chapter.isArchived && (
              <button className="btn-icon" onClick={handleRestoreChapter} title="Restore this chapter">♻ Restore</button>
            )}
          </div>
          {chapter.outline && (
            <div className="outline"><strong>Outline:</strong> {chapter.outline}</div>
          )}
          {(chapter.povCharacter || chapter.foreshadowingNotes || chapter.payoffNotes) && (
            <div className="chapter-meta-fields">
              {chapter.povCharacter && <span className="ch-meta-item"><strong>POV:</strong> {chapter.povCharacter}</span>}
              {chapter.foreshadowingNotes && <span className="ch-meta-item"><strong>Foreshadowing:</strong> {chapter.foreshadowingNotes}</span>}
              {chapter.payoffNotes && <span className="ch-meta-item"><strong>Payoff:</strong> {chapter.payoffNotes}</span>}
            </div>
          )}
          <div className="chapter-content">
            {streamBuffer && streamingChapterId === chapter.id ? (
              <ReactMarkdown>{streamBuffer}</ReactMarkdown>
            ) : chapter.content ? (
              <ChapterContent
                content={chapter.content}
                illustrations={scenes}
                renderFigure={ill => (
                  <IllustrationFigure
                    bookId={bookId}
                    illustration={ill}
                    editable={canEdit && !chapter.isArchived}
                    canRender={!isRunning}
                    characters={characters}
                    paragraphCount={paragraphCount}
                    onRender={(i, reroll) => handleRenderIllustrations('Single', i.id, reroll)}
                    onChanged={refreshIllustrations}
                  />
                )}
              />
            ) : (
              <p className="empty">Waiting to be written by the agents…</p>
            )}
          </div>
          <div className="chapter-prev-next">
            {prevChapter ? (
              <button className="btn-ghost" onClick={() => navigate(`/books/${bookId}/chapters/${prevChapter.id}`)}>
                ← Ch {prevChapter.number}: {prevChapter.title}
              </button>
            ) : <span />}
            {nextChapter ? (
              <button className="btn-ghost" onClick={() => navigate(`/books/${bookId}/chapters/${nextChapter.id}`)}>
                Ch {nextChapter.number}: {nextChapter.title} →
              </button>
            ) : <span />}
          </div>
        </>
      )}
    </div>
  )
}
