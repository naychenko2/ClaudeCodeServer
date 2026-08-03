// Шапка панели «Чтение» — 52px (TB.heightDesktop) вместо типовых 40: несёт заголовок
// страницы и домен в две строки. Осознанное отклонение от системы, принятое для этой
// панели (см. постановку задачи и docs/mockups/link-reader-proposal.md). Заменяет
// стандартный IslandHeader целиком (PanelShell.headerContent) — рисуется только пока
// есть что показать (загрузка/ошибка/статья); в пустом состоянии панель использует
// обычную 40px-шапку PanelShell по умолчанию (см. ReaderPanel.tsx).
import { ArrowLeft, ExternalLink, Maximize2, Minimize2, X } from 'lucide-react';
import { C, FONT, FS, TB } from '../../../lib/design';
import { IconButton } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import type { ReaderPanelActions, ReaderPanelState } from './useReaderPanel';

function hostOf(url: string | null): string {
  if (!url) return '';
  try { return new URL(url).hostname; } catch { return url; }
}

interface Props {
  state: ReaderPanelState;
  actions: Pick<ReaderPanelActions, 'back' | 'toggleExpand' | 'openInBrowser'>;
  onClose: () => void;
}

export function ReaderHeaderBar({ state, actions, onClose }: Props) {
  const host = hostOf(state.url);
  const title = state.loading
    ? 'Загружаем страницу…'
    : state.error
    ? 'Не удалось показать'
    : (state.page?.title || host);

  return (
    <div style={{
      height: TB.heightDesktop, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8,
      padding: '0 6px 0 12px', width: '100%',
    }}>
      <div style={{ flex: 1, minWidth: 0 }}>
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
      <div style={{ display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0 }}>
        {state.canGoBack && (
          <IconButton size="md" title="Назад" onClick={actions.back}>
            <ArrowLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
        {/* Развернуть/свернуть — только когда есть что разворачивать: не во время загрузки */}
        {!state.loading && !state.error && (
          <IconButton
            size="md"
            title={state.expanded ? 'Свернуть к панели' : 'Развернуть на всю контентную зону'}
            onClick={actions.toggleExpand}
          >
            {state.expanded
              ? <Minimize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              : <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          </IconButton>
        )}
        <IconButton size="md" title="Открыть в браузере" onClick={actions.openInBrowser}>
          <ExternalLink size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton size="md" title="Закрыть" onClick={onClose}>
          <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
    </div>
  );
}
