// Кастомная шапка «Чтения» для PanelZone.panelHeaders — отдельным файлом (не в
// ReaderPanel.tsx): это единственный не-компонентный экспорт рядом с компонентами
// ломает Fast Refresh (react-refresh/only-export-components).
import { TB } from '../../../lib/design';
import { ReaderHeaderBar } from './ReaderHeaderBar';
import type { ReaderPanelActions, ReaderPanelState } from './useReaderPanel';

// Только когда есть что показывать (загрузка/ошибка/статья); в пустом состоянии
// и пока панель развёрнута панель молча остаётся на стандартной 40px-шапке PanelShell
// (headerContent не передаётся вовсе — undefined).
export function buildReaderPanelHeaders(state: ReaderPanelState, actions: ReaderPanelActions) {
  if (!state.open || state.expanded) return undefined;
  return {
    reader: {
      height: TB.heightDesktop,
      content: (onCloseThis: () => void) => (
        <ReaderHeaderBar state={state} actions={actions} onClose={() => { actions.closeReader(); onCloseThis(); }} />
      ),
    },
  };
}
