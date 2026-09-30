import { Fragment, type ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import type { Illustration } from '../api'
import { splitParagraphs } from '../utils/paragraphs'

interface Props {
  content: string
  /** The chapter's scene illustrations; each is shown after the paragraph at its paragraphIndex. */
  illustrations?: Illustration[]
  renderFigure: (illustration: Illustration) => ReactNode
}

/** Chapter prose with its illustrations placed after the paragraphs they illustrate. */
export default function ChapterContent({ content, illustrations = [], renderFigure }: Props) {
  if (illustrations.length === 0) return <ReactMarkdown>{content}</ReactMarkdown>

  const paragraphs = splitParagraphs(content)
  const byParagraph = new Map<number, Illustration[]>()
  for (const ill of illustrations) {
    const index = Math.min(Math.max(ill.paragraphIndex, 0), Math.max(paragraphs.length - 1, 0))
    byParagraph.set(index, [...(byParagraph.get(index) ?? []), ill])
  }

  return (
    <>
      {paragraphs.map((p, i) => (
        <Fragment key={i}>
          <ReactMarkdown>{p}</ReactMarkdown>
          {byParagraph.get(i)?.map(ill => <Fragment key={ill.id}>{renderFigure(ill)}</Fragment>)}
        </Fragment>
      ))}
    </>
  )
}
