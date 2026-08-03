// Шапка панели «Чтение» — те же примитивы и та же геометрия, что у шапки FileViewer
// (Toolbar/ToolbarIconButton/PillSwitch, TB.heightDesktop): человек не должен
// почувствовать, что вместо файла открылся другой продукт. Гибкий элемент строки —
// только заголовок статьи (домен под ним, «Назад», «Открыть в браузере» — несжимаемые
// иконки внутри той же сжимаемой группы). Правый якорь-выход — режим «Сплит | Полный»
// (PillSwitch, вырождается в один тумблер на tier 'tight') + «Закрыть»: несжимаемая
// группа последним ребёнком строки, ровно как в FileViewer.tsx (правый якорь-выход).
import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, Columns2, ExternalLink, Maximize2, X } from 'lucide-react';
import { C, FONT, FS, SP, TB } from '../../../lib/design';
import { Toolbar, ToolbarIconButton, PillSwitch } from '../../../components/Toolbar';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import type { ReaderPanelActions, ReaderPanelState } from './useReaderPanel';

// Ступени по ширине панели — те же пороги, что у FileViewer (840/600/400): один и тот
// же переключатель режима должен сжиматься одинаково что у файла, что у статьи.
type Tier = 'comfort' | 'cozy' | 'narrow' | 'tight';

function hostOf(url: string | null): string {
  if (!url) return '';
  try { return new URL(url).hostname; } catch { return url; }
}

interface Props {
  state: ReaderPanelState;
  actions: Pick<ReaderPanelActions, 'back' | 'toggleExpand' | 'openInBrowser'>;
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
                lineHeight: 1.3, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}>
                <span style={{ width: 12, height: 12, flexShrink: 0, borderRadius: 3, background: C.accentMuted }} />
                {host}
              </div>
            )}
          </div>

          <ToolbarIconButton onClick={actions.openInBrowser} title="Открыть в браузере">
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
