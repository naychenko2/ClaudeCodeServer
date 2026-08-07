import { Component, type CSSProperties, type ErrorInfo, type ReactNode } from 'react';
import { C, FONT, FS, R, SP } from '../lib/design';
import { Button } from './ui/Button';
import { PageCanvas } from './ui/PageCanvas';
import { LiveDoodle } from './errorScreen/LiveDoodle';
import { SnakeGame, SnakeMark } from './errorScreen/SnakeGame';

// Признак «не удалось догрузить код» (лёг фронт-сервис / выкатка новой версии).
function isChunkError(err: unknown): boolean {
  const msg = err instanceof Error ? `${err.name} ${err.message}` : String(err);
  return /ChunkLoadError|dynamically imported module|Importing a module script failed|Loading chunk/i.test(msg);
}

// Дудлы-герои в стилистике фона-холста (тонкая линия «от руки»): рисуются
// currentColor, поэтому темизация бесплатная — цвет задаёт контейнер.
// Сбой рендера встречает живой персонаж (LiveDoodle), а обрыв догрузки —
// статичный дудл ниже: там «связи нет», и оживлять нечего.
function DisconnectedDoodle() {
  return (
    <svg width="132" height="104" viewBox="0 0 132 104" fill="none" stroke="currentColor"
         strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
      <g transform="rotate(2 66 40)">
        <path d="M44 60h44a17 17 0 003-33.6A23 23 0 0044 22a19 19 0 000 38z" />
      </g>
      {/* Пунктир вниз с разрывом: пакет не доехал */}
      <path d="M66 68v6M66 80v6" />
      <path d="M58 88l8 8 8-8" />
      {/* Разрыв связи — «молния» сбоку */}
      <path d="M96 74l7-9-9 1 7-9" />
    </svg>
  );
}

interface Props {
  children?: ReactNode;
  // Локальный fallback (напр. плашка «модуль недоступен»). Не задан — полноэкранная заглушка.
  fallback?: ReactNode | ((error: Error) => ReactNode);
}

interface State {
  error: Error | null;
  chunk: boolean;
  // Стек компонентов из componentDidCatch — самое полезное в отчёте: показывает,
  // на каком экране рвануло. Живёт в state, чтобы попасть в «Подробности».
  componentStack: string;
  detailsOpen: boolean;
  copied: boolean;
  gameOpen: boolean;
  // Сколько ягод съедено и сколько раз змейка погибла: дудл реагирует на то
  // и на другое (см. LiveDoodle.cheer / grief)
  cheer: number;
  grief: number;
}

const INITIAL: State = { error: null, chunk: false, componentStack: '', detailsOpen: false, copied: false, gameOpen: false, cheer: 0, grief: 0 };

// Второстепенные действия экрана — текстовыми ссылками, а не кнопками: две
// полновесные кнопки уже заняты выходом из сбоя, и третья спорила бы с ними.
const linkBtn: CSSProperties = {
  background: 'none', border: 'none', padding: 0, cursor: 'pointer',
  fontFamily: 'inherit', fontSize: FS.sm, color: C.textMuted, textDecoration: 'underline',
};

export class ErrorBoundary extends Component<Props, State> {
  state: State = INITIAL;
  private copiedTimer = 0;

  static getDerivedStateFromError(error: unknown): Partial<State> {
    return {
      error: error instanceof Error ? error : new Error(String(error)),
      chunk: isChunkError(error),
    };
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    console.error('[ErrorBoundary] перехвачена ошибка рендера:', error, info.componentStack);
    this.setState({ componentStack: info.componentStack ?? '' });
  }

  componentWillUnmount() {
    window.clearTimeout(this.copiedTimer);
  }

  private reload = () => window.location.reload();

  // Стрелка-метод, а не инлайн: SnakeGame держит его в зависимостях игрового
  // цикла, и новая функция на каждый рендер пересоздавала бы интервал хода
  private cheerUp = () => this.setState(s => ({ cheer: s.cheer + 1 }));
  private grieve = () => this.setState(s => ({ grief: s.grief + 1 }));

  // Выход из наглухо сломанного экрана: сбрасываем маршрут в хэше и грузим заново.
  // Без этого из упавшего раздела нет пути никуда, кроме ручной правки адреса.
  private goHome = () => {
    window.location.hash = '';
    window.location.reload();
  };

  // Текст для баг-репорта: что упало, где упало и на какой версии страницы.
  private report(): string {
    const { error, componentStack } = this.state;
    return [
      `${error?.name ?? 'Error'}: ${error?.message ?? ''}`,
      error?.stack ?? '',
      componentStack ? `Стек компонентов:${componentStack}` : '',
      `URL: ${window.location.href}`,
      `UA: ${navigator.userAgent}`,
    ].filter(Boolean).join('\n\n');
  }

  private copy = () => {
    navigator.clipboard?.writeText(this.report())
      .then(() => {
        this.setState({ copied: true });
        window.clearTimeout(this.copiedTimer);
        this.copiedTimer = window.setTimeout(() => this.setState({ copied: false }), 1500);
      })
      .catch(() => {});
  };

  render() {
    const { error, chunk, detailsOpen, copied, gameOpen, cheer, grief } = this.state;
    if (!error) return this.props.children;

    // Локальный fallback имеет приоритет над полноэкранной заглушкой
    const { fallback } = this.props;
    if (fallback !== undefined) {
      return typeof fallback === 'function' ? fallback(error) : fallback;
    }

    // Два разных события под одной заглушкой, и тон у них разный: догрузка кода
    // чаще всего рвётся не из-за поломки, а из-за выкатки новой версии.
    const title = chunk ? 'Приложение не догрузилось' : 'Что-то пошло не так';
    // Вторая строка — отдельным абзацем: совет «что делать» не должен теряться
    // в хвосте объяснения, за которым его перестают читать
    const [what, howTo] = chunk
      ? ['Обычно так бывает после обновления продукта: браузер держит старую версию страницы.',
         'Перезагрузите — подхватится новая. Если не помогло, сервер приложения недоступен.']
      : ['Интерфейс споткнулся и не смог отрисовать этот экран.',
         'Чаще всего помогает перезагрузка страницы.'];

    return (
      <PageCanvas style={{
        height: 'auto', minHeight: '100dvh', overflow: 'auto',
        alignItems: 'center', justifyContent: 'center',
        padding: SP.xl, boxSizing: 'border-box',
      }}>
        <div style={{
          display: 'flex', flexDirection: 'column', alignItems: 'center',
          textAlign: 'center', maxWidth: 480, gap: SP.md,
        }}>
          <div style={{ color: C.textMuted, opacity: 0.75 }}>
            {chunk ? <DisconnectedDoodle /> : <LiveDoodle cheer={cheer} grief={grief} />}
          </div>

          <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: FS.h2, color: C.textHeading, letterSpacing: '-0.01em' }}>
            {title}
          </div>
          <div style={{ fontSize: FS.md, color: C.textSecondary, lineHeight: 1.55 }}>
            <div>{what}</div>
            <div style={{ marginTop: SP.xs }}>{howTo}</div>
          </div>

          <div style={{ display: 'flex', gap: SP.sm, marginTop: SP.xs, flexWrap: 'wrap', justifyContent: 'center' }}>
            <Button variant="primary" size="md" onClick={this.reload}>Обновить страницу</Button>
            <Button variant="ghost" size="md" onClick={this.goHome}>На главную</Button>
          </div>

          {/* Второстепенное — ссылками в один ряд: детали для разбирательства
              и змейка для ожидания. Игра свёрнута, чтобы не спорить с кнопками */}
          <div style={{ display: 'flex', gap: SP.md, marginTop: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
            <button onClick={() => this.setState(s => ({ detailsOpen: !s.detailsOpen }))} style={linkBtn}>
              {detailsOpen ? 'Скрыть подробности' : 'Подробности ошибки'}
            </button>
            <button
              onClick={() => this.setState(s => ({ gameOpen: !s.gameOpen }))}
              style={{ ...linkBtn, display: 'inline-flex', alignItems: 'center', gap: SP.xs, textDecoration: 'none' }}
            >
              <SnakeMark />
              <span style={{ textDecoration: 'underline' }}>
                {gameOpen ? 'Убрать змейку' : 'Тут спрятана змейка'}
              </span>
            </button>
          </div>

          {gameOpen && <SnakeGame onEat={this.cheerUp} onDie={this.grieve} />}

          {detailsOpen && (
            <div style={{ width: '100%', display: 'flex', flexDirection: 'column', gap: SP.sm, alignItems: 'center' }}>
              <pre style={{
                width: '100%', boxSizing: 'border-box', margin: 0, textAlign: 'left',
                maxHeight: 200, overflow: 'auto',
                background: C.bgInset, borderRadius: R.xl, padding: SP.md,
                fontFamily: FONT.mono, fontSize: FS.xs, lineHeight: 1.5, color: C.textSecondary,
                whiteSpace: 'pre-wrap', wordBreak: 'break-word',
              }}>
                {this.report()}
              </pre>
              <button onClick={this.copy} style={linkBtn}>
                {copied ? 'Скопировано' : 'Скопировать детали'}
              </button>
            </div>
          )}
        </div>
      </PageCanvas>
    );
  }
}
