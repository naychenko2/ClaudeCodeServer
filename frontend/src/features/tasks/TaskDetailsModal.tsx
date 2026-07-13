// Личная задача (вне проекта) из календаря: детали в модальном окне поверх
// календаря — воркспейса у такой задачи нет. Десктоп: центрированная карточка,
// мобила: bottom-sheet (по паттерну ui/Modal).

import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import type { Task } from '../../types';
import { C, R, SHADOW, Z } from '../../lib/design';
import { api } from '../../lib/api';
import { TaskDetailsPane } from './TaskDetailsPane';

interface Props {
  task: Task;
  isMobile?: boolean;
  // Открыть сразу в редактировании (свежесозданная личная задача)
  startInEdit?: boolean;
  onClose: () => void;
}

export function TaskDetailsModal({ task, isMobile, startInEdit, onClose }: Props) {
  const handleOpenSession = async (sessionId: string) => {
    try {
      const chat = await api.chats.get(sessionId);
      if (chat) {
        window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
      }
    } catch { /* не удалось открыть чат */ }
  };

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
          key={task.id}
          task={task}
          project={null}
          isMobile={isMobile}
          startInEdit={startInEdit}
          onBack={onClose}
          onClose={onClose}
          onOpenSession={handleOpenSession}
          onDeleted={onClose}
        />
      </div>
    </div>,
    document.body,
  );
}
