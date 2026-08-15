// Личная задача (вне проекта) из календаря: детали в модальном окне поверх
// календаря — воркспейса у такой задачи нет. Десктоп: центрированная карточка,
// мобила: bottom-sheet (по паттерну ui/Modal).

import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import type { Project, Task } from '../../types';
import { C, R, SHADOW, Z } from '../../lib/design';
import { openChatById } from '../../lib/openChat';
import { TaskDetailsPane } from './TaskDetailsPane';

interface Props {
  task: Task;
  isMobile?: boolean;
  // Проект задачи: без него панель показывает её как личную (без названия проекта и
  // секции файлов). Календарь проекта не знает и не передаёт — карточка доклада в чате
  // (DelegationReportCard) передаёт, там файлы задачи и нужны
  project?: Project | null;
  onOpenFile?: (path: string) => void;
  // Открыть сразу в редактировании (свежесозданная личная задача)
  startInEdit?: boolean;
  onClose: () => void;
}

export function TaskDetailsModal({ task, isMobile, project = null, onOpenFile, startInEdit, onClose }: Props) {
  // Чат-исполнитель проектной задачи — сессия проекта, вне раздела «Чаты» её нет:
  // куда открывать, решает общий хелпер. Модалка лежит поверх всего, поэтому после
  // состоявшегося перехода закрываем её — иначе открытый чат остался бы под ней
  const handleOpenSession = async (sessionId: string) => {
    const opened = await openChatById(sessionId, { missingTitle: 'Чат задачи', missingBody: 'Чат не найден — возможно, он удалён' });
    if (opened) onClose();
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
          project={project}
          isMobile={isMobile}
          startInEdit={startInEdit}
          onBack={onClose}
          onClose={onClose}
          onOpenSession={handleOpenSession}
          onOpenFile={onOpenFile}
          onDeleted={onClose}
        />
      </div>
    </div>,
    document.body,
  );
}
