// Шапка панели «Чтение» — те же примитивы и та же геометрия, что у шапки FileViewer
// (Toolbar/ToolbarIconButton/PillSwitch, TB.heightDesktop): человек не должен
// почувствовать, что вместо файла открылся другой продукт. Гибкий элемент строки —
// только заголовок статьи (домен под ним, «Назад», «Открыть оригинал в браузере» —
// несжимаемые иконки внутри той же сжимаемой группы). Правый якорь-выход — режим
// «Сплит | Полный» (PillSwitch, вырождается в один тумблер на tier 'tight') + «Закрыть»:
// несжимаемая группа последним ребёнком строки, ровно как в FileViewer.tsx.
//
// Режим «Страница целиком | Режим чтения» (ADR-006 §2, макет
// provider-limit-reader-header-v1.html §2): pill-чип сразу после домена показывает
// ТЕКУЩИЙ режим (точка C.info — живой сайт во фрейме, C.textMuted — извлечённый текст)
// и он же — ручной переключатель: клик переводит в противоположный режим. Серверный
// вердикт авторитетен для заголовков, но не всеточен, поэтому человек всегда может
// сменить режим в обе стороны. В состояниях загрузки/ошибки чипа нет — режим ещё не
// определён (макет §2.3).
import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { ArrowLeft, ArrowLeftRight, Columns2, ExternalLink, Maximize2, SquareStack, X } from 'lucide-react';
import { C, FONT, FS, R, SP, TB } from '../../../lib/design';
import { Toolbar, ToolbarIconButton, PillSwitch } from '../../../components/Toolbar';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useContextButton } from '../../../features/chatContext/useContextButton';
import type { ReaderPanelActions, ReaderPanelState } from './useReaderPanel';

// Ступени по ширине панели — те же пороги, что у FileViewer (840/600/400): один и тот
// же переключатель режима должен сжиматься одинаково что у файла, что у статьи.
type Tier = 'comfort' | 'cozy' | 'narrow' | 'tight';

function hostOf(url: string | null): string {
  if (!url) return '';
  try { return new URL(url).hostname; } catch { return url; }
}

// Чип режима у домена (макет §2, спецификация §3). На узкой панели (tier 'tight',
// < 400px) подпись скрывается — чип сжимается до точки и иконки, полный текст остаётся
// в title. Иконка переключения (ArrowLeftRight) видна всегда: hover на тач-устройствах
// недоступен, поэтому без иконки чип не читается как переключатель.
function ModeChip({ state, tight, onToggle }: {
  state: ReaderPanelState;
  tight: boolean;
  onToggle: () => void;
}) {
  const isPage = state.mode === 'page';
  const label = isPage ? 'Страница целиком' : 'Режим чтения';
  // mixed content: iframe невозможен в принципе — чип только показывает режим
  const locked = state.iframeUnavailable;
  const title = locked
    ? 'Режим чтения: http-страницу нельзя встроить из https-приложения'
    : isPage
    ? 'Показана страница целиком — нажмите, чтобы перейти в режим чтения'
    : 'Показан режим чтения — нажмите, чтобы показать страницу целиком';
  const chipStyle: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 5, flexShrink: 0,
    background: C.bgSelected, color: C.textSecondary, border: 'none', borderRadius: R.max,
    padding: tight ? '3px 6px' : '2px 8px', fontSize: FS.xs, fontFamily: FONT.sans,
    lineHeight: 1.3, whiteSpace: 'nowrap',
    cursor: locked ? 'default' : 'pointer',
  };
  const dot = (
    <span style={{ width: 6, height: 6, borderRadius: '50%', flexShrink: 0, background: isPage ? C.info : C.textMuted }} />
  );
  const toggleIcon = (
    <ArrowLeftRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
  );
  if (locked) {
    return <span title={title} style={chipStyle}>{tight ? <>{dot}{toggleIcon}</> : <>{dot}{toggleIcon}{label}</>}</span>;
  }
  return (
    <button type="button" onClick={onToggle} title={title} aria-label={title} style={chipStyle}>
      {tight ? <>{dot}{toggleIcon}</> : <>{dot}{toggleIcon}{label}</>}
    </button>
  );
}

interface Props {
  state: ReaderPanelState;
  actions: Pick<ReaderPanelActions, 'back' | 'toggleExpand' | 'openInBrowser' | 'toggleMode'>;
  onClose: () => void;
  // Планшет: как у файла — всегда на всю контентную зону, переключателя режима нет
  isTablet?: boolean;
}

export function ReaderHeaderBar({ state, actions, onClose, isTablet }: Props) {
  const rootRef = useRef<HTMLDivElement>(null);
  const [panelWidth, setPanelWidth] = useState(0);
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setPanelWidth(el.clientWidth));
    ro.observe(el);
    setPanelWidth(el.clientWidth);
    return () => ro.disconnect();
  }, []);
  const tier: Tier = panelWidth === 0 || panelWidth >= 840 ? 'comfort'
    : panelWidth >= 600 ? 'cozy'
    : panelWidth >= 400 ? 'narrow'
    : 'tight';

  const host = hostOf(state.url);
  const title = state.loading
    ? 'Загружаем страницу…'
    : state.error
    ? 'Не удалось показать'
    : (state.page?.title || host);
  // Пока грузим/ошибка — разворачивать нечего (как в исходной версии шапки)
  const showModeSwitch = !isTablet && !state.loading && !state.error;

  // «В контекст чата» (фича chat-context): подпись материала — заголовок статьи,
  // домен как запасной (по нему статью узнают в полосе, если заголовка ещё нет)
  const chatContext = useContextButton('url', state.url, state.page?.title || host);
  const addToChatContext = () => {
    chatContext.toggle();
    // Из полного экрана материал сворачивается к чату: полоса вкладок живёт
    // только в сплите, а контекст — про работу рядом с разговором
    if (!chatContext.inContext && state.expanded && !isTablet) actions.toggleExpand();
  };

  return (
    <Toolbar>
      <div ref={rootRef} style={{ display: 'flex', alignItems: 'center', gap: TB.gap, flex: 1, minWidth: 0 }}>
        {/* Сжимаемая зона слева: «Назад» + заголовок/домен + «Открыть в браузере».
            Гибкий элемент здесь только заголовок — остальное несжимаемые иконки. */}
        <div style={{ display: 'flex', alignItems: 'center', gap: TB.gap, flex: 1, minWidth: 0 }}>
          {state.canGoBack && (
            <ToolbarIconButton onClick={actions.back} title="Назад">
              <ArrowLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </ToolbarIconButton>
          )}

          <div style={{ flex: '1 1 auto', minWidth: 0 }}>
            <div style={{
              fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, lineHeight: 1.25,
              color: state.loading ? C.textSecondary : C.textHeading,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }} title={state.loading || state.error ? undefined : (state.page?.title ?? undefined)}>
              {title}
            </div>
            {host && (
              <div style={{
                display: 'flex', alignItems: 'center', gap: 5, fontSize: FS.xs, color: C.textMuted,
                lineHeight: 1.3, whiteSpace: 'nowrap', overflow: 'hidden',
              }}>
                <span style={{ width: 12, height: 12, flexShrink: 0, borderRadius: 3, background: C.accentMuted }} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{host}</span>
                {/* Режим не показывается на загрузке/ошибке — он ещё не определён (макет §2.3) */}
                {!state.loading && !state.error && state.mode && (
                  <ModeChip state={state} tight={tier === 'tight'} onToggle={actions.toggleMode} />
                )}
              </div>
            )}
          </div>

          {chatContext.available && (
            <ToolbarIconButton
              onClick={addToChatContext} title={chatContext.title}
              color={chatContext.inContext ? C.accent : undefined}
            >
              <SquareStack size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </ToolbarIconButton>
          )}

          <ToolbarIconButton onClick={actions.openInBrowser} title="Открыть оригинал в браузере">
            <ExternalLink size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        </div>

        {/* Правый якорь-выход: режим просмотра + «Закрыть» — несжимаемая группа
            последним ребёнком строки, как в FileViewer (см. FileViewer.tsx:1330-1372):
            доступна всегда, даже когда панель ужата до предела. */}
        <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center', flexShrink: 0 }}>
          {showModeSwitch && (
            tier === 'tight' ? (
              <ToolbarIconButton
                onClick={actions.toggleExpand}
                title={state.expanded ? 'Свернуть: сплит с чатом' : 'Развернуть на весь экран'}
              >
                {state.expanded
                  ? <Columns2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  : <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              </ToolbarIconButton>
            ) : (
              <PillSwitch
                value={state.expanded ? 'full' : 'split'}
                iconsOnly={tier !== 'comfort'}
                options={[
                  { value: 'split' as const, label: 'Сплит', title: 'Сплит с чатом', icon: <Columns2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
                  { value: 'full' as const, label: 'Полный', title: 'На весь экран', icon: <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
                ]}
                onChange={v => {
                  // toggleExpand — toggle без аргумента: клик по уже активному сегменту
                  // должен быть no-op, иначе увело бы в противоположный режим
                  if (v === 'full' && !state.expanded) actions.toggleExpand();
                  if (v === 'split' && state.expanded) actions.toggleExpand();
                }}
              />
            )
          )}
          <ToolbarIconButton onClick={onClose} title="Закрыть">
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        </div>
      </div>
    </Toolbar>
  );
}
