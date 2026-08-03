// Композиция панели «Чтение»: тело для рельсы, фабрика кастомной шапки и оверлей
// «Развёрнуто» (весь холст, portal — тот же приём, что у Modal). Держатели состояния —
// WorkspacePage/ChatsPage (один экземпляр useReaderPanel на страницу).
import { createPortal } from 'react-dom';
import { C, ISLAND, Z } from '../../../lib/design';
import { Island } from '../../../components/ui';
import { ReaderHeaderBar } from './ReaderHeaderBar';
import { ReaderBody } from './ReaderBody';
import type { ReaderPanelActions, ReaderPanelState } from './useReaderPanel';

interface Props {
  state: ReaderPanelState;
  actions: ReaderPanelActions;
}

// Тело для `panels.reader` — состояния без шапки (шапку несёт отдельно panelHeaders,
// см. buildReaderPanelHeaders). Пока панель развёрнута на весь холст, рельса ничего
// не показывает — контент виден только в ReaderExpandedOverlay (см. WorkspacePage).
export function ReaderRailContent({ state, actions }: Props) {
  if (state.expanded) return null;
  return <ReaderBody state={state} actions={actions} onClose={actions.closeReader} />;
}

// «Развёрнуто»: чат и панели скрыты, ридер занимает холст целиком (портал в document.body,
// приём как у Modal). Рендерится ВСЕГДА (сама решает, показываться ли) — панель рельсы
// в это время пуста (ReaderRailContent), поэтому переключатель не может жить внутри неё.
export function ReaderExpandedOverlay({ state, actions }: Props) {
  if (!state.expanded) return null;
  return createPortal(
    <div style={{
      position: 'fixed', inset: 0, zIndex: Z.modal, background: ISLAND.canvas,
      display: 'flex', padding: ISLAND.pad,
    }}>
      <Island bg={ISLAND.bg} style={{ flex: 1, minWidth: 0 }} borderColor={C.borderLight}>
        <ReaderHeaderBar state={state} actions={actions} onClose={actions.closeReader} />
        <ReaderBody state={state} actions={actions} onClose={actions.closeReader} />
      </Island>
    </div>,
    document.body,
  );
}
