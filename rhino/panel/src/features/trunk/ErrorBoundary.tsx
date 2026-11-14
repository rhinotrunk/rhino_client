import { Component, type ErrorInfo, type ReactNode } from 'react';

interface State { error?: string }

/** Shows crashes in the panel instead of a blank dock (render errors, uncaught errors, rejected promises). */
export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = {};

  static getDerivedStateFromError(error: unknown): State {
    return { error: describe(error) };
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    this.setState({ error: `${describe(error)}\n\nComponent stack:${info.componentStack ?? ''}` });
  }

  componentDidMount() {
    window.addEventListener('error', this.onError);
    window.addEventListener('unhandledrejection', this.onRejection);
  }

  componentWillUnmount() {
    window.removeEventListener('error', this.onError);
    window.removeEventListener('unhandledrejection', this.onRejection);
  }

  private onError = (e: ErrorEvent) => this.setState({ error: describe(e.error ?? e.message) });
  private onRejection = (e: PromiseRejectionEvent) => this.setState({ error: describe(e.reason) });

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div role="alert" className="space-y-2 p-3 text-sm">
        <p className="font-semibold text-destructive">The RhinoTrunk panel crashed</p>
        <p className="text-muted-foreground">Copy this text and send it to the team. Reopen the panel to try again.</p>
        <pre className="max-h-[70vh] select-text overflow-auto whitespace-pre-wrap rounded-md bg-muted p-2 font-mono text-xs">{this.state.error}</pre>
      </div>
    );
  }
}

function describe(error: unknown) {
  if (error instanceof Error) return `${error.name}: ${error.message}\n${error.stack ?? ''}`;
  return String(error);
}
