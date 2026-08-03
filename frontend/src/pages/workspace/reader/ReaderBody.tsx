// Тело панели «Чтение»: пусто / загрузка / ошибка / статья — все состояния из макета
// (docs/mockups/link-reader-v1.html §4) плюс однократная плашка приватности при первом
// открытии (ADR-005 §1). Кнопка «отправить прочитанное в чат» сюда НЕ добавляется —
// это инвариант ADR (текст в панели недоверенный), а не забытая деталь.
import { AlertTriangle, Newspaper } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Button, EmptyState } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { READER_ERROR_COPY, type ReaderErrorAction } from './readerErrors';
import { ReaderArticle } from './ReaderArticle';
import type { ReaderPanelActions, ReaderPanelState } from './useReaderPanel';

const SKELETON_WIDTHS = ['70%', '100%', '96%', '88%', '100%', '92%', '60%'];

function Skeleton() {
  return (
    <div>
      {SKELETON_WIDTHS.map((w, i) => (
        <div key={i} style={{
          width: w, height: i === 0 ? 20 : i === 4 ? 80 : 12, borderRadius: 6,
          background: C.bgSelected, marginBottom: i === 0 ? 16 : i === 4 ? 16 : 10,
          animation: 'cc-reader-pulse 1.4s ease-in-out infinite',
        }} />
      ))}
      <style>{'@keyframes cc-reader-pulse{0%,100%{opacity:.55}50%{opacity:.95}}'}</style>
    </div>
  );
}

// Порядок кнопок фиксирован (главная — та, что точно сработает), а не порядок из
// READER_ERROR_COPY.actions (он лишь перечисляет набор): «Открыть в браузере» первой,
// «Повторить» вторичной, «Закрыть» — когда браузеру предложить нечего (invalid-url)
const ACTION_ORDER: ReaderErrorAction[] = ['browser', 'retry', 'close'];

function ErrorView({ state, actions, onClose }: { state: ReaderPanelState; actions: ReaderPanelActions; onClose: () => void }) {
  const copy = state.error ? READER_ERROR_COPY[state.error.code] : null;
  if (!copy) return null;
  const ordered = ACTION_ORDER.filter(a => copy.actions.includes(a));
  return (
    <EmptyState
      icon={<AlertTriangle size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
      title="Не получилось показать страницу рядом"
      subtitle={copy.reason}
      action={
        <div style={{ display: 'flex', gap: SP.sm, justifyContent: 'center', flexWrap: 'wrap' }}>
          {ordered.map((a, i) => a === 'browser' ? (
            <Button key={a} variant={i === 0 ? 'primary' : 'secondary'} size="sm" onClick={actions.openInBrowser}>Открыть в браузере</Button>
          ) : a === 'retry' ? (
            <Button key={a} variant={i === 0 ? 'primary' : 'secondary'} size="sm" onClick={actions.retry}>Повторить</Button>
          ) : (
            <Button key={a} variant={i === 0 ? 'primary' : 'secondary'} size="sm" onClick={onClose}>Закрыть</Button>
          ))}
        </div>
      }
    />
  );
}

// Плашка приватности — однократная, ВНУТРИ панели, не модалка (ADR §1)
function PrivacyBanner({ onDismiss }: { onDismiss: () => void }) {
  return (
    <div style={{
      background: C.bgInset, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
      padding: SP.md, marginBottom: SP.md, display: 'flex', flexDirection: 'column', gap: 6,
    }}>
      <div style={{ fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>
        Страницу открывает сервер, а не ваш браузер
      </div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
        Сайт увидит адрес сервера, а не ваш. Ваши куки и вход не передаются — закрытые страницы
        так не откроются. Прочитанное нигде не сохраняется.
      </div>
      <div>
        <Button variant="secondary" size="xs" onClick={onDismiss}>Понятно</Button>
      </div>
    </div>
  );
}

interface Props {
  state: ReaderPanelState;
  actions: ReaderPanelActions;
  onClose: () => void;
  // Колонка чтения центрируется — 680 внутри CHAT_MAX_W (ADR/макет §6): в узкой рельсе
  // это просто max-width, в развёрнутом виде реально центрирует текст
  maxWidth?: number;
}

export function ReaderBody({ state, actions, onClose, maxWidth = 680 }: Props) {
  if (!state.open) {
    return (
      <EmptyState
        compact
        icon={<Newspaper size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Пока нечего читать"
        subtitle="Наведите на ссылку в переписке и нажмите значок — страница откроется здесь."
      />
    );
  }
  return (
    <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: `${SP.lg}px ${SP.lg}px 28px`, background: C.bgPanel }}>
      <div style={{ maxWidth, margin: '0 auto' }}>
        {/* Плашка объясняет саму механику ридера (сервер идёт по ссылке, не браузер) —
            показывается при первом открытии НЕЗАВИСИМО от того, загрузилась статья
            или нет (ошибка/загрузка — тоже открытие панели), иначе на ошибающемся
            сервере пользователь её вообще ни разу не увидит */}
        {!state.bannerDismissed && <PrivacyBanner onDismiss={actions.dismissBanner} />}
        {state.loading && <Skeleton />}
        {!state.loading && state.error && <ErrorView state={state} actions={actions} onClose={onClose} />}
        {!state.loading && !state.error && state.page && (
          <>
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, color: C.textMuted,
              background: C.bgInset, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
              padding: '6px 10px', marginBottom: SP.md,
            }}>
              Текст извлечён со страницы · вёрстка сайта не сохраняется
            </div>
            <ReaderArticle markdown={state.page.markdown} anchor={anchorOf(state.url)} onFollow={actions.follow} />
          </>
        )}
      </div>
    </div>
  );
}

function anchorOf(url: string | null): string | null {
  if (!url) return null;
  try { return decodeURIComponent(new URL(url).hash.replace(/^#/, '')) || null; } catch { return null; }
}
