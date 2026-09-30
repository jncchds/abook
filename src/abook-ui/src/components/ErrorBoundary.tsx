import { Component, type ErrorInfo, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'

interface Props { children: ReactNode }
interface BoundaryProps extends Props { resetKey: string }
interface State { error: Error | null }

/**
 * Catches render errors so one broken page shows a message instead of blanking the whole app.
 * Background agent runs are unaffected — they live on the server.
 */
class Boundary extends Component<BoundaryProps, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidUpdate(prev: BoundaryProps) {
    // Clear only an error that is showing — never remount healthy pages on navigation.
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: null })
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Render error', error, info.componentStack)
  }

  render() {
    if (!this.state.error) return this.props.children
    return (
      <div className="error-boundary" role="alert" style={{ padding: '2rem', maxWidth: 640 }}>
        <h2>Something went wrong on this page</h2>
        <p>Any running agent keeps going on the server. Reload to continue.</p>
        <pre style={{ whiteSpace: 'pre-wrap', opacity: 0.7 }}>{this.state.error.message}</pre>
        <button onClick={() => window.location.reload()}>Reload</button>
      </div>
    )
  }
}

/** Error boundary that clears a caught error when the route changes, so navigating away recovers the app. */
export default function ErrorBoundary({ children }: Props) {
  const { pathname } = useLocation()
  return <Boundary resetKey={pathname}>{children}</Boundary>
}
