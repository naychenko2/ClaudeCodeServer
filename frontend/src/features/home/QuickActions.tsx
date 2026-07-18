import { useState } from 'react';
import { FolderPlus, MessageCirclePlus, NotebookPen, Plus, UserPlus, Zap } from 'lucide-react';
import type { Project, ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { openTaskInSection } from '../../lib/tasks';
import { ensureNotesLoaded } from '../../lib/notes';
import type { HubTab } from '../../components/HubTabs';
import { NewTaskDialog } from '../tasks/NewTaskDialog';
import { NewNoteDialog } from '../notes/NewNoteDialog';
import { AddProjectDialog } from '../projects/dialogs/AddProjectDialog';
import { WidgetCard } from './WidgetCard';
import { openNote } from './NotesWidget';

// Хинт разделу «Персоны»: открыть мастер создания сразу после перехода с дашборда
export const PENDING_PERSONA_CREATE_KEY = 'cc_pending_persona_create';

// Кнопка быстрого действия — заметная плашка с иконкой
function ActionButton({ icon, label, onClick, disabled }: {
  icon: React.ReactNode; label: string; onClick: () => void; disabled?: boolean;
}) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 8, borderRadius: 10,
        width: '100%', minWidth: 0, boxSizing: 'border-box',
        padding: '9px 13px', cursor: disabled ? 'default' : 'pointer',
        background: hover && !disabled ? C.bgSelected : C.bgCard,
        border: `1px solid ${C.borderLight}`, opacity: disabled ? 0.6 : 1,
        fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, whiteSpace: 'nowrap',
      }}
    >
      <span style={{ display: 'flex', color: C.accent, flexShrink: 0 }}>{icon}</span>
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</span>
    </button>
  );
}

// «Быстрые действия»: создание чата, задачи, заметки, проекта и персоны с дашборда.
export function QuickActions({ onHubTab, onOpenProject }: {
  onHubTab: (t: HubTab) => void;
  onOpenProject: (p: Project) => void;
}) {
  const [creatingChat, setCreatingChat] = useState(false);
  const [newTaskOpen, setNewTaskOpen] = useState(false);
  const [newNoteOpen, setNewNoteOpen] = useState(false);
  const [newProjectOpen, setNewProjectOpen] = useState(false);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);

  // Новый чат вне проекта: создаем и передаем готовому listener'у App (cc-open-chat) —
  // тот сам переключит раздел «Чаты» и откроет чат
  const newChat = async () => {
    if (creatingChat) return;
    setCreatingChat(true);
    try {
      const chat = await api.chats.create();
      window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
    } catch {
      setCreatingChat(false);
    }
  };

  // Диалогу заметки нужны стор заметок (автодополнение папок) и группы (диалогу проекта)
  const openNewNote = () => { void ensureNotesLoaded(); setNewNoteOpen(true); };
  const openNewProject = () => {
    api.projectGroups.list().then(setGroups).catch(() => {});
    setNewProjectOpen(true);
  };
  // Новая персона: мастер создания живет в контентной зоне раздела «Персоны» —
  // переходим туда с хинтом на автозапуск (PersonasPage подхватит при монтировании)
  const newPersona = () => {
    sessionStorage.setItem(PENDING_PERSONA_CREATE_KEY, '1');
    onHubTab('personas');
  };

  return (
    <WidgetCard icon={<Zap size={16} strokeWidth={2} />} title="Быстрые действия">
      {/* Сетка с равной шириной кнопок: колонки тянутся одинаково, ряды добираются сами */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: 8 }}>
        <ActionButton
          icon={<MessageCirclePlus size={15} strokeWidth={2} />}
          label={creatingChat ? 'Создаю…' : 'Новый чат'}
          onClick={() => void newChat()}
          disabled={creatingChat}
        />
        <ActionButton
          icon={<Plus size={15} strokeWidth={2} />}
          label="Новая задача"
          onClick={() => setNewTaskOpen(true)}
        />
        <ActionButton
          icon={<NotebookPen size={15} strokeWidth={2} />}
          label="Новая заметка"
          onClick={openNewNote}
        />
        <ActionButton
          icon={<FolderPlus size={15} strokeWidth={2} />}
          label="Новый проект"
          onClick={openNewProject}
        />
        <ActionButton
          icon={<UserPlus size={15} strokeWidth={2} />}
          label="Новая персона"
          onClick={newPersona}
        />
      </div>
      {newTaskOpen && (
        <NewTaskDialog
          onCreated={(task, configure) => {
            setNewTaskOpen(false);
            // «Создать и настроить» — открываем задачу в ее разделе; иначе TasksWidget
            // подхватит новую задачу сам по realtime task_changed
            if (configure) openTaskInSection(task);
          }}
          onClose={() => setNewTaskOpen(false)}
        />
      )}
      {newNoteOpen && (
        <NewNoteDialog
          onCreated={id => { setNewNoteOpen(false); openNote(id); }}
          onClose={() => setNewNoteOpen(false)}
        />
      )}
      {newProjectOpen && (
        <AddProjectDialog
          groups={groups}
          onSuccess={p => { setNewProjectOpen(false); onOpenProject(p); }}
          onClose={() => setNewProjectOpen(false)}
        />
      )}
    </WidgetCard>
  );
}
