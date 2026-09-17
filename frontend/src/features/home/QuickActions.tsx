import { useEffect, useState } from 'react';
import { ArrowRight, FolderPlus, MessageCirclePlus, Plus, UserPlus, Zap } from 'lucide-react';
import type { Project, ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { createChatWithContextPersona } from '../../lib/defaultPersona';
import { C, FONT, R } from '../../lib/design';
import { openTaskInSection } from '../../lib/tasks';
import { useRecentIds } from '../../lib/pinnedProjects';
import type { HubTab } from '../../components/HubTabs';
import { NewTaskDialog } from '../tasks/NewTaskDialog';
import { useSlotItem } from '../../lib/subsystems/registry';
import type { QuickActionNoteCtx } from '../../lib/subsystems/registryCore';
import { AddProjectDialog } from '../projects/dialogs/AddProjectDialog';
import { ProjectIcon } from '../projects/ProjectIcon';
import { ensureProjectsLoaded } from '../projects/useAllProjects';
import { WidgetCard } from './WidgetCard';

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

// Возврат в последний открытый проект — самый частый вход с дашборда, поэтому плашка
// занимает целую строку сетки ('1 / -1', а не span 2: при одной колонке span 2 добавил
// бы неявную вторую и разъехалась бы вся сетка).
function LastProjectTile({ project, onClick }: { project: Project; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        gridColumn: '1 / -1',
        display: 'flex', alignItems: 'center', gap: 10, borderRadius: 10,
        width: '100%', minWidth: 0, boxSizing: 'border-box', textAlign: 'left',
        padding: '9px 13px', cursor: 'pointer',
        background: hover ? C.bgSelected : C.bgCard,
        border: `1px solid ${C.borderLight}`,
      }}
    >
      <ProjectIcon project={project} size={30} radius={R.md} />
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
        <span style={{
          fontFamily: FONT.sans, fontSize: 13.5, color: C.textPrimary,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {project.name}
        </span>
        <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
          Последний проект
        </span>
      </span>
      <ArrowRight size={15} strokeWidth={2} style={{ color: C.accent, flexShrink: 0 }} />
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
  const [newProjectOpen, setNewProjectOpen] = useState(false);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);
  // Последний открытый проект берём из клиентского MRU (lib/pinnedProjects) — это
  // «куда я заходил», а не updatedAt проекта: чужая активность не должна подменять
  // плашку. Сам Project тянем из списка; не нашёлся (удалён, чужой) — плашки нет.
  const recentIds = useRecentIds();
  const lastId = recentIds[0];
  const [lastProject, setLastProject] = useState<Project | null>(null);
  useEffect(() => {
    if (!lastId) { setLastProject(null); return; }
    let alive = true;
    // Общий кэш шапки и палитры (TTL 60с) — отдельный запрос /projects тут не нужен
    ensureProjectsLoaded()
      .then(list => { if (alive) setLastProject(list.find(p => p.id === lastId) ?? null); })
      .catch(() => {});
    return () => { alive = false; };
  }, [lastId]);
  // Кнопка «Новая заметка» — вклад слота quick-action (фича Notes). Нет вклада —
  // кнопки нет (подсистема выключена/не зарегистрирована).
  const quickNote = useSlotItem<QuickActionNoteCtx>('quick-action', 'new-note');

  // Новый чат вне проекта: создаем и передаем готовому listener'у App (cc-open-chat) —
  // тот сам переключит раздел «Чаты» и откроет чат
  const newChat = async () => {
    if (creatingChat) return;
    setCreatingChat(true);
    try {
      // Чат создаётся от лица личной дефолт-персоны
      const chat = await createChatWithContextPersona();
      window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
    } catch {
      setCreatingChat(false);
    }
  };

  // Диалогу проекта нужны группы
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
        {lastProject && (
          <LastProjectTile project={lastProject} onClick={() => onOpenProject(lastProject)} />
        )}
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
        {quickNote?.render?.({ ActionButton })}
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
