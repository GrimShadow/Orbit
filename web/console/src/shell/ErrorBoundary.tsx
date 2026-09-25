import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button } from '@dam/ui-kit';
import i18n from '../i18n';

/** Last-resort boundary: keeps a render error from blanking the app. Strings come from i18n directly (class component). */
export class ErrorBoundary extends Component<{ children: ReactNode }, { error?: Error }> {
  state: { error?: Error } = {};

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Unhandled UI error', error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div role="alert" className="dam-page dam-stack">
        <h1>{i18n.t('error.title')}</h1>
        <p>{i18n.t('error.body')}</p>
        <Button variant="primary" onClick={() => window.location.reload()}>
          {i18n.t('error.reload')}
        </Button>
      </div>
    );
  }
}
