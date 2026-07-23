import { Component, type ErrorInfo, type ReactNode } from 'react';
import { C, FONT, R } from '../lib/design';

// Признак «не удалось догрузить код» (лёг фронт-сервис / выкатка новой версии).
function isChunkError(err: unknown): boolean {
  const msg = err instanceof Error ? `${err.name} ${err.message}` : String(err);
  return /ChunkLoadError|dynamically imported module|Importing a module script failed|Loading chunk/i.test(msg);
}

interface Props {
  children?: ReactNode;
  // Локальный fallback (напр. плашка «модуль недоступен»). Не задан — полноэкранная заглушка.
  fallback?: ReactNode | ((error: Error) => ReactNode);
}

interface State {
  error: Error | null;
  chunk: boolean;
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null, chunk: false };

  static getDerivedStateFromError(error: unknown): State {
    return {
      error: error instanceof Error ? error : new Error(String(error)),
      chunk: isChunkError(error),
    };
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    console.error('[ErrorBoundary] перехвачена ошибка рендера:', error, info.componentStack);
  }

  private reload = () => window.location.reload();

  render() {
    const { error, chunk } = this.state;
    if (!error) return this.props.children;

    // Локальный fallback имеет приоритет над полноэкранной заглушкой
    const { fallback } = this.props;
    if (fallback !== undefined) {
      return typeof fallback === 'function' ? fallback(error) : fallback;
    }

    const title = chunk ? 'Не удалось загрузить приложение' : 'Что-то пошло не так';
    const hint = chunk
      ? 'Похоже, сервер приложения недоступен или вышло обновление. Обновите страницу — обычно это помогает.'
      : 'Произошёл сбой в интерфейсе. Обновите страницу, чтобы продолжить.';

    return (
      <div style={{ minHeight: '100vh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 420, gap: 12 }}>
          <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 24, color: C.textHeading, letterSpacing: '-0.01em' }}>{title}</div>
          <div style={{ fontSize: 14, color: C.textSecondary, lineHeight: 1.55 }}>{hint}</div>
          <button onClick={this.reload} style={{ marginTop: 8, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg, padding: '9px 18px', fontSize: 14, fontWeight: 600, cursor: 'pointer', fontFamily: 'inherit' }}>Обновить страницу</button>
        </div>
      </div>
    );
  }
}
