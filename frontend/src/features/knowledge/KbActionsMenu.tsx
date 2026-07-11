import type { ReactNode } from 'react';
import type { KnowledgeBaseSummary } from '../../types';
import { C, FONT, R, MODAL_W } from '../../lib/design';
import { Menu, MenuItem, Modal } from '../../components/ui';

type Action =
  | { sep: true }
  | { label: string; danger?: boolean; icon: ReactNode; onClick: () => void };

// Унифицированное контекстное меню базы знаний: десктоп/планшет — выпадающий popup
// (Menu, позиционируется по относительно-позиционированному родителю — обёртке кнопки ⋯),
// мобила — bottom-sheet (Modal сам становится шторкой на узком экране). Состав един
// для всех баз: «Добавить документ», для удаляемых — разделитель + «Удалить базу».
// «Открыть» здесь нет намеренно: клик по самой карте уже открывает базу.
export function KbActionsMenu({ kb, isMobile, onClose, onAddDocument, onDelete }: {
  kb: KnowledgeBaseSummary;
  isMobile: boolean;
  onClose: () => void;
  onAddDocument: () => void;
  onDelete: () => void;
}) {
  const actions: Action[] = [
    { label: 'Добавить документ', icon: <><path d="M12 5v14M5 12h14" /></>, onClick: onAddDocument },
  ];
  if (kb.deletable) {
    actions.push({ sep: true });
    actions.push({ label: 'Удалить базу', danger: true, icon: <><path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></>, onClick: onDelete });
  }

  const run = (fn: () => void) => { onClose(); fn(); };

  if (isMobile) {
    return (
      <Modal title={kb.title} width={MODAL_W.form}
        subtitle={kb.deletable ? undefined : 'Привязана к другому разделу — удаляется через него'}
        onClose={onClose}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          {actions.map((a, i) => 'sep' in a ? (
            <div key={i} style={{ height: 1, background: C.border, margin: '6px 4px' }} />
          ) : (
            <button key={i} onClick={() => run(a.onClick)} style={sheetBtn(a.danger)}>
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                {a.icon}
              </svg>
              {a.label}
            </button>
          ))}
        </div>
      </Modal>
    );
  }

  return (
    <Menu onClose={onClose} top={32} align="right" minWidth={212}>
      {actions.map((a, i) => 'sep' in a ? (
        <div key={i} style={{ height: 1, background: C.border, margin: '4px 2px' }} />
      ) : (
        <MenuItem key={i} icon={a.icon} label={a.label} danger={a.danger} onClick={() => run(a.onClick)} />
      ))}
    </Menu>
  );
}

function sheetBtn(danger?: boolean): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 12, width: '100%',
    padding: '13px 12px', borderRadius: R.md, border: 'none', background: 'transparent',
    cursor: 'pointer', fontFamily: FONT.sans, fontSize: 15, textAlign: 'left',
    color: danger ? C.danger : C.textPrimary,
  };
}
