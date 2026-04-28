import { useBookContext } from '../../contexts/BookContext'

export default function StatePage() {
  const { workflowSteps, isRunning, runStatus, clearTokenUsageForBook } = useBookContext()

  return (
    <div className="view-content">
      <div className="view-header">
        <h2>🏃 Current State</h2>
        {workflowSteps.length > 0 && !isRunning && (
          <button className="btn-sm btn-danger" onClick={clearTokenUsageForBook}>🗑 Clear History</button>
        )}
      </div>
      {isRunning && (
        <div className="agent-running-banner" style={{ marginBottom: '1rem' }}>
          <span className="spinner" /> {runStatus?.role} is {runStatus?.state === 'WaitingForInput' ? 'waiting for input…' : 'running…'}
        </div>
      )}
      {workflowSteps.length === 0 ? (
        <p className="empty">No workflow steps recorded yet. Start a workflow to see progress here.</p>
      ) : (
        <div className="current-state-list">
          {workflowSteps.map(s => (
            <div key={s.id} className="workflow-step-item">
              <div className="wsi-icon">›</div>
              <div className="wsi-body">
                <div className="wsi-step">{s.step}</div>
                <div className="wsi-meta">
                  <span className="wsi-time">{s.time}</span>
                  {s.modelName && <span className="wsi-model">{s.modelName}</span>}
                  {s.endpoint && <span className="wsi-endpoint" title={s.endpoint}>{s.endpoint.replace(/https?:\/\//, '').slice(0, 30)}{(s.endpoint.length ?? 0) > 33 ? '…' : ''}</span>}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
