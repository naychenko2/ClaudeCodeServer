import { useState } from 'react';
import type { ReactNode } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import type { KnowledgeBaseSummary } from '../../types';
import { C, FONT, R, MODAL_W } from '../../lib/design';
import { Menu, Modal } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

type Action =
  | { sep: true }
  | { label: string; danger?: boolean; icon: ReactNode; onClick: () => void };

// Унифицированное контекстное меню базы знаний: десктоп/планшет — выпадающий popup
// (Menu, позиционируется по относительно-позиционированному родителю — обёртке кнопки ⋯),
// мобила — bottom-sheet (Modal сам становится шторкой на узком экране). Состав един
// для всех баз: «Добавить документ», для удаляемых — разделитель + «Удалить базу».
// «Открыть» здесь нет намеренно: клик по самой карте уже открывает базу.
// Иконки — lucide-компоненты; поэтому десктоп рисует собственную строку-кнопку вместо
// MenuItem (тот оборачивает icon в свой <svg> и ждет path-фрагменты, а не компонент).
export function KbActionsMenu({ kb, isMobile, onClose, onAddDocument, onDelete }: {
  kb: KnowledgeBaseSummary;
  isMobile: boolean;
  onClose: () => void;
  onAddDocument: () => void;
  onDelete: () => void;
}) {
  const actions: Action[] = [
    { label: 'Добавить документ', icon: <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, onClick: onAddDocument },
  ];
  if (kb.deletable) {
    actions.push({ sep: true });
    actions.push({ label: 'Удалить базу', danger: true, icon: <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, onClick: onDelete });
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
              {a.icon}
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
        <RowBtn key={i} danger={a.danger} icon={a.icon} label={a.label} onClick={() => run(a.onClick)} />
      ))}
    </Menu>
  );
}

// Строка десктопного меню: визуально идентична MenuItem (та же сетка/отступы/наведение),
// но иконка — lucide-компонент напрямую, без обёртки в <svg>.
function RowBtn({ danger, icon, label, onClick }: {
  danger?: boolean;
  icon: ReactNode;
  label: string;
  onClick: () => void;
}) {
  const [hover, setHover] = useState(false);
  const color = danger ? C.danger : C.textPrimary;
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
        background: hover ? C.bgSelected : 'none', border: 'none', borderRadius: R.md,
        padding: '9px 10px', cursor: 'pointer', color, fontSize: 13.5, fontFamily: FONT.sans,
      }}
    >
      {icon}
      {label}
    </button>
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
