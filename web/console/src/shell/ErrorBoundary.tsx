import { Component, type ErrorInfo, type ReactNode } from 'react';
import i18n from '../i18n';
import { StatusPanel } from '../pages/StatusPanel';

/** Last-resort boundary: keeps a render error from blanking the app. Strings come from i18n directly (class
 * component), and the actions are plain links/buttons because this can render outside the router. */
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
      <div className="dam-root dam-crash">
        <StatusPanel
          variant="fault"
          role="alert"
          title={i18n.t('error.title')}
          description={i18n.t('error.body')}
          actions={
            <>
              <button type="button" className="dam-btn dam-btn--primary" onClick={() => window.location.reload()}>
                {i18n.t('error.reload')}
              </button>
              <a href="/" className="dam-btn dam-btn--secondary">
                {i18n.t('error.home')}
              </a>
            </>
          }
        />
      </div>
    );
  }
}
