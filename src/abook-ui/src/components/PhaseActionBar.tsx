import { useBookContext } from '../contexts/BookContext'

type Phase = 'storybible' | 'characters' | 'plotthreads' | 'chapters'

interface PhaseActionBarProps {
  phase: Phase
  onClear: () => void
  onHistory?: () => void
  clearLabel?: string
  style?: React.CSSProperties
}

export default function PhaseActionBar({ phase, onClear, onHistory, clearLabel = '🗄 Archive', style }: PhaseActionBarProps) {
  const { isPhaseComplete, handleCompletePhase, handleReopenPhase, handleClearPhase } = useBookContext()

  return (
    <div className="phase-actions" style={style}>
      {isPhaseComplete(phase) ? (
        <>
          <span className="phase-status-badge phase-complete">✅ Complete</span>
          <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase(phase)}>↺ Reopen</button>
        </>
      ) : (
        <>
          <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
          <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase(phase)}>✓ Complete</button>
        </>
      )}
      {onHistory && (
        <button className="btn-sm btn-ghost phase-action-btn" onClick={onHistory}>📜 History</button>
      )}
      <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase(phase, onClear)}>{clearLabel}</button>
    </div>
  )
}
