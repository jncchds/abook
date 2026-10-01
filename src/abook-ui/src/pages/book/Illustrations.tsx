import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useBookContext } from '../../contexts/BookContext'
import { getLlmConfig, updateBook } from '../../api'
import type { Illustration, LlmConfig } from '../../api'
import IllustrationFigure from '../../components/IllustrationFigure'
import ArtDirectionCard from '../../components/ArtDirectionCard'
import VisualAssetsCard from '../../components/VisualAssetsCard'

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
  const hasText = chapters.some(c => c.content?.trim())
  const canRender = !isRunning && imageConfigured
  const pendingPortraits = portraits.filter(p => p.status !== 'Rendered').length

  const enableForBook = async () => {
    const r = await updateBook(bookId, { ...book, illustrationsEnabled: true })
    setBook(prev => prev ? { ...prev, illustrationsEnabled: r.data.illustrationsEnabled } : prev)
  }

  const replanChapter = (chapterId: number, scenes: Illustration[]) => {
    if (scenes.some(i => !i.imageId) && !confirm('Re-plan this chapter\'s illustrations? Planned ones that were never rendered are replaced.')) return
    handlePlanIllustrations('Chapter', chapterId)
  }

  const figure = (ill: Illustration) => (
    <IllustrationFigure
      key={ill.id}
      bookId={bookId}
      illustration={ill}
      editable={canEdit}
      canRender={canRender}
      characters={characters}
      onRender={onRender}
      onChanged={refreshIllustrations}
      compact
    />
  )

  return (
    <div>
      <div className="page-header"><h2>🎨 Illustrations</h2></div>

      {!book.illustrationsEnabled && (
        <div className="card" style={{ marginBottom: '1rem' }}>
          <p>Illustrations are off for this book, so the workflow doesn’t plan them. Turn them on to have each finished
            chapter planned automatically, or plan them yourself below.</p>
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

      {/* Whole book: the two runs, in the order they happen. */}
      <div className="card illustrations-steps">
        <div className="illustrations-step">
          <span className="illustrations-step-label">1 · Plan <span className="hint">(LLM)</span></span>
          <button className="btn-secondary" disabled={isRunning || !hasText} onClick={() => handlePlanIllustrations('Missing')}
            title="Chapters that have no illustrations yet, then the cover once every chapter is written">
            🎨 Plan everything missing
          </button>
          <span className="hint">Chapters without illustrations, then the cover once the book is written.</span>
        </div>
        <div className="illustrations-step">
          <span className="illustrations-step-label">2 · Render <span className="hint">(image model)</span></span>
          <button disabled={!canRender || pending === 0} onClick={() => handleRenderIllustrations('All')}
            title="Portraits, cover, then chapter scenes — everything not yet rendered">
            ▶ Illustrate book ({pending})
          </button>
          <span className="hint">
            {config?.unloadLlmBeforeRender && config.provider === 'Ollama'
              ? 'The Ollama model is unloaded automatically first.'
              : 'No LLM calls — unload the LLM first if it shares the GPU.'}
          </span>
        </div>
        <p className="hint illustrations-status">
          {rendered} rendered · {pending} to render{failed > 0 ? ` · ${failed} failed` : ''}
          {!allChaptersDone && ' · the book isn’t finished — you can render what is planned so far'}
        </p>
        {rendering && workflowLog.length > 0 && (
          <p className="hint illustrations-status"><span className="spinner" /> {workflowLog[workflowLog.length - 1]}</p>
        )}
      </div>

      {/* 1. Art direction — the style every image shares. */}
      <ArtDirectionCard
        book={book}
        editable={canEdit}
        canPlan={!isRunning}
        onRewrite={() => handlePlanIllustrations('Style')}
        onSaved={patch => { setBook(prev => prev ? { ...prev, ...patch } : prev); refreshIllustrations() }}
      />

      {/* 2. Characters — settle each look before the scenes reuse it. */}
      <section className="illustrations-section">
        <div className="illustrations-section-header">
          <h3>2 · Characters</h3>
          <div className="illustrations-section-actions">
            <button className="btn-sm btn-secondary" disabled={isRunning || characters.every(c => c.isArchived)}
              onClick={() => {
                if (portraits.length > 0 && !confirm('Rewrite every character\'s visual description? Prompts that quote the old descriptions are updated to match.')) return
                handlePlanIllustrations('Characters')
              }}
              title="LLM: write each character's visual description and portrait">
              🎨 {portraits.length > 0 ? 'Rewrite descriptions' : 'Describe characters'}
            </button>
            <button className="btn-sm" disabled={!canRender || pendingPortraits === 0} onClick={() => handleRenderIllustrations('Portraits')}
              title="Render the portraits to check each look — re-roll any you don't like before illustrating the book">
              ▶ Render portraits ({pendingPortraits})
            </button>
          </div>
        </div>
        {portraits.length === 0 ? (
          <p className="empty">No portraits yet — they are planned with the first chapter’s illustrations, or use Describe characters.</p>
        ) : (
          <>
            <div className="illustrations-grid">
              {portraits.map(p => (
                <div key={p.id}>
                  <strong style={{ display: 'block', marginBottom: '0.4rem' }}>{characterName(p.characterCardId)}</strong>
                  {figure(p)}
                </div>
              ))}
            </div>
            <p className="hint" style={{ marginTop: '0.5rem' }}>
              Each portrait’s seed is reused for every scene centred on that character. Re-roll (🎲) a portrait until the
              look is right, then use “Re-render portrait &amp; scenes” on the <Link to={`/books/${bookId}/characters`}>Characters</Link> page.
            </p>
          </>
        )}
      </section>

      {/* 3. Recurring assets — outfits, props and places kept identical across images. */}
      <VisualAssetsCard bookId={bookId} editable={canEdit} refreshKey={illustrations} onChanged={refreshIllustrations} />

      {/* 4. Chapters — in reading order. */}
      <section className="illustrations-section">
        <div className="illustrations-section-header"><h3>4 · Chapters</h3></div>
        {!hasText && <p className="empty">No chapter has been written yet.</p>}
        {chapters.map(ch => {
          const scenes = illustrations
            .filter(i => i.kind === 'Scene' && i.chapterId === ch.id)
            .sort((a, b) => a.paragraphIndex - b.paragraphIndex)
          if (scenes.length === 0 && !ch.content?.trim()) return null
          const pendingScenes = scenes.filter(i => i.status !== 'Rendered').length
          return (
            <div key={ch.id} className="illustrations-chapter">
              <div className="illustrations-section-header">
                <h4>
                  <a href="#" onClick={e => { e.preventDefault(); navigate(`/books/${bookId}/chapters/${ch.id}`) }}>
                    Chapter {ch.number}: {ch.title}
                  </a>
                </h4>
                <div className="illustrations-section-actions">
                  <button className="btn-sm btn-secondary" disabled={isRunning || !ch.content?.trim()}
                    onClick={() => replanChapter(ch.id, scenes)} title="LLM: plan this chapter's illustrations">
                    🎨 {scenes.length > 0 ? 'Re-plan' : 'Plan'}
                  </button>
                  <button className="btn-sm" disabled={!canRender || pendingScenes === 0}
                    onClick={() => handleRenderIllustrations('Chapter', ch.id)} title="Render this chapter's unrendered illustrations">
                    ▶ Render ({pendingScenes})
                  </button>
                </div>
              </div>
              {scenes.length === 0
                ? <p className="empty">No illustrations planned.</p>
                : <div className="illustrations-grid">{scenes.map(s => figure(s))}</div>}
            </div>
          )
        })}
      </section>

      {/* 5. Cover — planned once the whole book is written. */}
      <section className="illustrations-section">
        <div className="illustrations-section-header">
          <h3>5 · Cover</h3>
          <div className="illustrations-section-actions">
            <button className="btn-sm btn-secondary" disabled={isRunning || !hasText}
              onClick={() => {
                if (cover && !confirm('Re-plan the cover? Its current image stays until it is re-rendered.')) return
                handlePlanIllustrations('Cover')
              }} title="LLM: plan the cover">
              🎨 {cover ? 'Re-plan cover' : 'Plan cover'}
            </button>
            <button className="btn-sm" disabled={!canRender || !cover || cover.status === 'Rendered'}
              onClick={() => cover && handleRenderIllustrations('Single', cover.id)} title="Render the cover">
              ▶ Render cover
            </button>
          </div>
        </div>
        {cover
          ? <div style={{ maxWidth: 360 }}>{figure(cover)}</div>
          : <p className="empty">{allChaptersDone ? 'No cover planned yet.' : 'The cover is planned once every chapter is written.'}</p>}
      </section>
    </div>
  )
}
