import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useBookContext } from '../../contexts/BookContext'
import { getLlmConfig, updateBook } from '../../api'
import type { Illustration, LlmConfig } from '../../api'
import IllustrationFigure from '../../components/IllustrationFigure'

/**
 * Review and render the book's illustrations. Planning (LLM) happens during the workflow or from the
 * buttons here; rendering is a separate image-model run so the LLM can be unloaded first.
 */
export default function Illustrations() {
  const navigate = useNavigate()
  const {
    book, setBook, characters, illustrations, isRunning, canEdit, runStatus, workflowLog,
    refreshIllustrations, handlePlanIllustrations, handleRenderIllustrations,
  } = useBookContext()
  const [config, setConfig] = useState<LlmConfig | null>(null)

  useEffect(() => {
    if (!book) return
    getLlmConfig(book.id).then(r => setConfig(r.data)).catch(() => setConfig(null))
  }, [book?.id]) // eslint-disable-line react-hooks/exhaustive-deps

  if (!book) return null
  const bookId = book.id

  const chapters = (book.chapters ?? []).filter(c => !c.isArchived).sort((a, b) => a.number - b.number)
  const cover = illustrations.find(i => i.kind === 'Cover')
  const activeCharacterIds = new Set(characters.filter(c => !c.isArchived).map(c => c.id))
  const portraits = illustrations.filter(i => i.kind === 'Portrait' && i.characterCardId && activeCharacterIds.has(i.characterCardId))
  const liveChapterIds = new Set(chapters.map(c => c.id))
  const live = illustrations.filter(i => i.kind !== 'Scene' || (i.chapterId && liveChapterIds.has(i.chapterId)))
  const pending = live.filter(i => i.status !== 'Rendered').length
  const failed = live.filter(i => i.status === 'Failed').length
  const rendered = live.filter(i => i.status === 'Rendered').length
  const imageConfigured = !!config?.imageEndpoint
  const allChaptersDone = chapters.length > 0 && chapters.every(c => c.status === 'Done')
  const rendering = isRunning && runStatus?.role === 'Illustrator'

  const onRender = (ill: Illustration, reroll: boolean) => handleRenderIllustrations('Single', ill.id, reroll)
  const characterName = (id: number | null) => characters.find(c => c.id === id)?.name ?? 'Character'

  const enableForBook = async () => {
    const r = await updateBook(bookId, { ...book, illustrationsEnabled: true })
    setBook(prev => prev ? { ...prev, illustrationsEnabled: r.data.illustrationsEnabled } : prev)
  }

  const figure = (ill: Illustration, extra?: { paragraphCount?: number }) => (
    <IllustrationFigure
      key={ill.id}
      bookId={bookId}
      illustration={ill}
      editable={canEdit}
      canRender={!isRunning && imageConfigured}
      characters={characters}
      onRender={onRender}
      onChanged={refreshIllustrations}
      compact
      {...extra}
    />
  )

  return (
    <div>
      <div className="page-header"><h2>🎨 Illustrations</h2></div>

      {!book.illustrationsEnabled && (
        <div className="card" style={{ marginBottom: '1rem' }}>
          <p>Illustrations are off for this book, so the workflow doesn’t plan them. Turn them on to have each finished
            chapter planned automatically, or plan them yourself with the buttons below.</p>
          <button className="btn-sm" style={{ marginTop: '0.5rem' }} onClick={enableForBook}>Enable illustrations for this book</button>
        </div>
      )}

      {config && !imageConfigured && (
        <div className="card" style={{ marginBottom: '1rem', borderColor: 'var(--warning)' }}>
          <p>No image endpoint is configured, so illustrations can be planned but not rendered.{' '}
            <Link to={`/books/${bookId}/settings`}>Set one in the book’s LLM settings</Link>, or in the{' '}
            <Link to="/settings">global settings</Link> / a <Link to="/presets">preset</Link>.</p>
        </div>
      )}

      <div className="illustrations-actions">
        <button disabled={isRunning} onClick={() => handlePlanIllustrations('Missing')}
          title="LLM: plan finished chapters that have no illustrations yet, then the cover once the book is written">
          🎨 Plan missing
        </button>
        <button className="btn-secondary" disabled={isRunning || chapters.every(c => !c.content?.trim())}
          onClick={() => handlePlanIllustrations('Cover')} title="LLM: (re)plan the cover">
          🎨 Plan cover
        </button>
        <button className="btn-secondary" disabled={isRunning || !imageConfigured || portraits.every(p => p.status === 'Rendered')}
          onClick={() => handleRenderIllustrations('Portraits')}
          title="Render the character portraits first to check each character’s look — re-roll any you don’t like before illustrating the book">
          ▶ Render portraits
        </button>
        <button disabled={isRunning || !imageConfigured || pending === 0} onClick={() => handleRenderIllustrations('All')}
          title="Render everything not yet rendered: portraits, cover, then chapter scenes">
          ▶ Illustrate book ({pending})
        </button>
        <span className="hint">
          {rendered} rendered · {pending} to render{failed > 0 ? ` · ${failed} failed` : ''}
        </span>
      </div>
      {imageConfigured && (
        <p className="hint" style={{ marginBottom: '1rem' }}>
          Rendering makes no LLM calls.{' '}
          {config?.unloadLlmBeforeRender && config.provider === 'Ollama'
            ? 'The Ollama model is unloaded automatically before rendering starts.'
            : 'If the LLM and the image model share a GPU, unload the LLM before you render.'}
          {!allChaptersDone && ' The book isn’t finished yet — you can still render what is planned so far.'}
        </p>
      )}
      {rendering && workflowLog.length > 0 && (
        <p className="hint" style={{ marginBottom: '1rem' }}><span className="spinner" /> {workflowLog[workflowLog.length - 1]}</p>
      )}

      <section className="illustrations-section">
        <h3>Cover</h3>
        {cover
          ? <div style={{ maxWidth: 360 }}>{figure(cover)}</div>
          : <p className="empty">{allChaptersDone ? 'No cover planned yet — use Plan cover.' : 'The cover is planned once every chapter is written.'}</p>}
      </section>

      <section className="illustrations-section">
        <h3>Characters</h3>
        {portraits.length === 0 ? (
          <p className="empty">No portraits yet — they are planned together with the first chapter’s illustrations.</p>
        ) : (
          <div className="illustrations-grid">
            {portraits.map(p => (
              <div key={p.id}>
                <strong style={{ display: 'block', marginBottom: '0.4rem' }}>{characterName(p.characterCardId)}</strong>
                {figure(p)}
              </div>
            ))}
          </div>
        )}
        {portraits.length > 0 && (
          <p className="hint" style={{ marginTop: '0.5rem' }}>
            Each portrait’s seed is reused for every scene centred on that character. Re-roll (🎲) a portrait until the
            look is right, then use “Re-render portrait &amp; scenes” on the <Link to={`/books/${bookId}/characters`}>Characters</Link> page.
          </p>
        )}
      </section>

      {chapters.map(ch => {
        const scenes = illustrations
          .filter(i => i.kind === 'Scene' && i.chapterId === ch.id)
          .sort((a, b) => a.paragraphIndex - b.paragraphIndex)
        if (scenes.length === 0 && !ch.content?.trim()) return null
        return (
          <section key={ch.id} className="illustrations-section">
            <h3>
              <a href="#" onClick={e => { e.preventDefault(); navigate(`/books/${bookId}/chapters/${ch.id}`) }}>
                Chapter {ch.number}: {ch.title}
              </a>
            </h3>
            {scenes.length === 0 ? (
              <p className="empty">
                No illustrations planned.{' '}
                {!isRunning && ch.content?.trim() && (
                  <button className="btn-sm btn-ghost" onClick={() => handlePlanIllustrations('Chapter', ch.id)}>🎨 Plan</button>
                )}
              </p>
            ) : (
              <div className="illustrations-grid">{scenes.map(s => figure(s))}</div>
            )}
          </section>
        )
      })}
    </div>
  )
}
