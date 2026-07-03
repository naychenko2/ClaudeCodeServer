// Личная задача (вне проекта) из календаря: детали в модальном окне поверх
// календаря — воркспейса у такой задачи нет. Десктоп: центрированная карточка,
// мобила: bottom-sheet (по паттерну ui/Modal).

import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import type { Task } from '../../types';
import { C, R, SHADOW, Z } from '../../lib/design';
import { TaskDetailsPane } from './TaskDetailsPane';

interface Props {
  task: Task;
  isMobile?: boolean;
  onClose: () => void;
}

export function TaskDetailsModal({ task, isMobile, onClose }: Props) {
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  const overlay: React.CSSProperties = {
    position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal,
    display: 'flex', justifyContent: 'center',
    alignItems: isMobile ? 'flex-end' : 'center',
    padding: isMobile ? 0 : 16,
  };

  const card: React.CSSProperties = isMobile
    ? {
        width: '100%', height: '92dvh', background: C.bgMain,
        borderTopLeftRadius: R.sheet, borderTopRightRadius: R.sheet,
        boxShadow: SHADOW.sheet, overflow: 'hidden', boxSizing: 'border-box',
      }
    : {
        width: 680, maxWidth: '100%', height: '82vh', background: C.bgMain,
        borderRadius: R.modal, boxShadow: SHADOW.modal,
        overflow: 'hidden', boxSizing: 'border-box',
      };

  return createPortal(
    <div style={overlay} onPointerDown={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div style={card}>
        <TaskDetailsPane
          task={task}
          project={null}
          isMobile={isMobile}
          onBack={onClose}
          onClose={onClose}
          onDeleted={onClose}
        />
      </div>
    </div>,
    document.body,
  );
}
