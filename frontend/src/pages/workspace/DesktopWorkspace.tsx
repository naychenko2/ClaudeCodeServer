// Тело нового интерфейса проекта «как десктопный Claude Code» (флаг workspace-cc-panels,
// только десктоп ≥1200): слева — панель ТОЛЬКО с чатами проекта, в центре — чат
// (или файл/задача/персона/доска/коммит), справа — рельса рабочих инструментов
// со стеком панелей (RightPanelStack): План, Файлы, Задачи, Команда, Терминал, Preview.
// WorkspacePage остаётся владельцем состояния и обработчиков — сюда всё приходит
// пропсами (контент панелек тоже собирается там); HubHeader и диалоги тоже там.
import { useState, useRef, type ReactNode, type PointerEvent as ReactPointerEvent } from 'react';
import { Plus, MessageCircle } from 'lucide-react';
import type { Project, Session, Task, SkillInfo, AgentInfo } from '../../types';
import { C, FONT } from '../../lib/design';
import { useSidebarWidth, SIDEBAR_MIN, SIDEBAR_MAX } from '../../lib/sidebarWidth';
import { Button, IconButton } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { Splitter } from '../../components/ui/Splitter';
import { SidebarSplitter } from '../../components/ui/SidebarSplitter';
import { SessionList } from '../../components/SessionList';
import { ChatPanel } from '../../components/ChatPanel';
import { FileViewer } from '../../components/FileViewer';
import { GitCommitView } from '../../components/GitCommitView';
import { TaskDetailsPane } from '../../features/tasks/TaskDetailsPane';
import { ProjectPersonaPane } from '../../features/personas/ProjectPersonasPanel';
import { SidebarProjectSwitcher } from '../../features/projects/SidebarProjectSwitcher';
import { RightPanelStack } from './RightPanelStack';
import type { PanelKey } from './panelStackState';

export type SidebarMode = 'pinned' | 'collapsed';

interface Props {
  // Планшет (601–1199): файл всегда fullscreen, правая зона — упрощённый solo
  isTablet?: boolean;
  project: Project;
  // Имя проекта в шапке сайдбара — из projectForEdit (обновляется после настроек)
  projectForEdit: Project;
  onOpenProjectSettings: () => void;
  // Сайдбар: общий стейт WorkspacePage (persist cc_sidebar_mode)
  sidebarMode: SidebarMode;
  setSidebarMode: (m: SidebarMode) => void;
  // Сессии
  activeSession: Session | null;
  onSelectSession: (s: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated: (s: Session) => void;
  // Создание чата по клику (кнопка в пустом состоянии центра) + сброс при удалении последнего
  onCreateSession: () => void;
  onClearSession: () => void;
  creatingSession?: boolean;
  workflowRunningFor?: string;
  // Бандл ChatPanel
  pendingMessage?: string;
  onPendingMessageSent: () => void;
  onWorkflowRunning: (active: boolean, sessionId: string) => void;
  skills?: SkillInfo[];
  agents?: AgentInfo[];
  attachedFiles: string[];
  onAttachedFilesChange: (files: string[]) => void;
  onResume: (message?: string) => void;
  // Центр: файл/коммит/задача, открытые из чата или диплинка
  openFile: string | null;
  openFileDiffMode: boolean;
  gitStagePath?: string | null;
  fileFullscreen: boolean;
  onEnterFullscreen: () => void;
  openCommitSha: string | null;
  openCommitFile?: string | null;
  onCloseCommit: () => void;
  onOpenFileFromChat: (path: string) => void;
  onCloseFile: () => void;
  selectedTask: Task | null;
  autoEditTaskId: string | null;
  onOpenTaskSession: (sessionId: string) => void;
  onOpenFileFromTree: (path: string) => void;
  onCloseTask: () => void;
  // Персона из панельки «Команда» — студия в центре (приоритет ниже задачи, выше доски)
  selectedPersonaId: string | null;
  personaCreating: boolean;
  onOpenPersonaChat: (session: Session) => void;
  onPersonaSelectAfterCreate: (id: string) => void;
  onPersonaCleared: () => void;
  // Командный центр (кнопка «Команда» в панельке персон) — в центре, ниже персоны
  teamCenterOpen: boolean;
  onCloseTeamCenter: () => void;
  teamCenterArea: ReactNode;
  // Доска задач: включается вкладкой «Доска» в панельке задач, рендерится в центре
  boardOpen: boolean;
  boardArea: ReactNode;
  // Превью dev-сервиса: выбирается в панельке «Preview», окно живёт в центре
  previewOpen: boolean;
  previewArea: ReactNode;
  onClosePreview: () => void;
  // Правая рельса: доступность инструментов + готовый контент панелек
  toolsEnabled: boolean;
  panels: Partial<Record<Exclude<PanelKey, 'plan'>, ReactNode>>;
  // Контролы в шапки карточек панелей (напр. переключатель видов задач)
  panelHeaderExtras?: Partial<Record<PanelKey, ReactNode>>;
}

export function DesktopWorkspace(p: Props) {
  const [sidebarWidth, setSidebarWidth] = useSidebarWidth();
  // Подсветка активного сплиттера: сайдбар или split чат|файл
  const [dragging, setDragging] = useState<'sidebar' | 'split' | null>(null);

  // Пропорция чат/файл в split-режиме (как chatFlex в старой ветке; не персистится)
  const [chatFlex, setChatFlex] = useState(1);
  const splitContainerRef = useRef<HTMLDivElement>(null);

  const handleSidebarDrag = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sidebarWidth;
    const onMove = (ev: PointerEvent) => {
      setSidebarWidth(Math.max(SIDEBAR_MIN, Math.min(SIDEBAR_MAX, startW + (ev.clientX - startX))));
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(null);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
    setDragging('sidebar');
  };

  // Split чат|файл: пересчёт пропорции из пиксельных ширин (копия handleSplitterMouseDown)
  const handleSplitDrag = (e: ReactPointerEvent) => {
    e.preventDefault();
    const container = splitContainerRef.current;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    const onMove = (ev: PointerEvent) => {
      const chatW = Math.max(200, Math.min(rect.width - 200, ev.clientX - rect.left));
      const fileW = rect.width - chatW;
      setChatFlex(chatW / fileW);
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(null);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
    setDragging('split');
  };

  const openSidebar = p.sidebarMode !== 'pinned' ? () => p.setSidebarMode('pinned') : undefined;

  // Явный выбор чата в списке закрывает открытые в центре студию персоны,
  // командный центр и превью сервиса
  const handleSelectSession = (s: Session, firstMessage?: string, autoSelect?: boolean) => {
    if (!autoSelect) {
      if (p.selectedPersonaId || p.personaCreating) p.onPersonaCleared();
      if (p.teamCenterOpen) p.onCloseTeamCenter();
      if (p.previewOpen) p.onClosePreview();
    }
    p.onSelectSession(s, firstMessage, autoSelect);
  };

  const personaOpen = !!p.selectedPersonaId || p.personaCreating;

  // Панель чатов: шапка проекта (без вкладок) + SessionList
  const sidebar = (
    <div style={{ width: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel, flexShrink: 0, height: '100%' }}>
      <div style={{ padding: '8px 10px 6px', flexShrink: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minHeight: 28 }}>
          {/* Плашка проекта = переключатель проектов; настройки открываются
              кликом по иконке активного проекта */}
          <SidebarProjectSwitcher project={p.projectForEdit} onOpenSettings={p.onOpenProjectSettings} />
        </div>
      </div>
      <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        <SessionList project={p.project} activeSession={p.activeSession} onSelect={handleSelectSession} onSessionUpdated={p.onSessionUpdated} onCleared={p.onClearSession} isMobile={false} workflowRunningFor={p.workflowRunningFor} />
      </div>
    </div>
  );

  const chatPanel = p.activeSession ? (
    <ChatPanel
      session={p.activeSession} project={p.project} onOpenFile={p.onOpenFileFromChat}
      pendingMessage={p.pendingMessage} onPendingMessageSent={p.onPendingMessageSent}
      onSessionUpdated={p.onSessionUpdated} isMobile={false} onWorkflowRunning={p.onWorkflowRunning}
      onOpenSidebar={openSidebar} skills={p.skills} agents={p.agents}
      attachedFiles={p.attachedFiles} onAttachedFilesChange={p.onAttachedFilesChange} onResume={p.onResume}
    />
  ) : (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {p.sidebarMode === 'collapsed' && (
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}`, background: C.bgMain }}>
          <IconButton size="md" variant="soft" onClick={() => p.setSidebarMode('pinned')} title="Открыть панель">
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
              <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
            </svg>
          </IconButton>
        </div>
      )}
      <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 400, gap: 10 }}>
          <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 4 }}>
            <MessageCircle size={ICON_SIZE.xl} strokeWidth={2} />
          </div>
          <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 22, color: C.textHeading, letterSpacing: '-0.01em' }}>
            С чего начнём?
          </div>
          <div style={{ fontSize: 13.5, color: C.textSecondary, lineHeight: 1.55, maxWidth: 360 }}>
            Начните новый чат по этому проекту или выберите существующий слева.
          </div>
          <Button
            variant="primary" size="md" glow loading={p.creatingSession}
            onClick={p.onCreateSession} style={{ marginTop: 10 }}
            leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}
          >
            Новый чат
          </Button>
        </div>
      </div>
    </div>
  );

  return (
    <>
      {/* === Сайдбар чатов: pinned в потоке + сплиттер с кнопкой «свернуть» === */}
      {p.sidebarMode === 'pinned' && (
        <>
          <div style={{ width: sidebarWidth, flexShrink: 0, height: '100%' }}>
            {sidebar}
          </div>
          <SidebarSplitter active={dragging === 'sidebar'} onMouseDown={handleSidebarDrag} onCollapse={() => p.setSidebarMode('collapsed')} />
        </>
      )}

      {/* === Центр: коммит → задача → персона → доска → файл (split/fullscreen) → чат === */}
      {!p.openFile && p.openCommitSha && (
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex' }}>
          <GitCommitView project={p.project} sha={p.openCommitSha} initialPath={p.openCommitFile} onClose={p.onCloseCommit} />
        </div>
      )}

      {!p.openFile && !p.openCommitSha && p.selectedTask && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <TaskDetailsPane key={p.selectedTask.id} task={p.selectedTask} project={p.project} startInEdit={p.selectedTask.id === p.autoEditTaskId} onOpenSession={p.onOpenTaskSession} onOpenFile={p.onOpenFileFromTree} onClose={p.onCloseTask} onDeleted={p.onCloseTask} />
        </div>
      )}

      {/* Студия персоны из панельки «Команда» */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && personaOpen && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <ProjectPersonaPane project={p.project} personaId={p.personaCreating ? null : p.selectedPersonaId} creating={p.personaCreating} onOpenChat={p.onOpenPersonaChat} onSelectPersona={p.onPersonaSelectAfterCreate} onCleared={p.onPersonaCleared} onBack={p.onPersonaCleared} />
        </div>
      )}

      {/* Командный центр (кнопка «Команда» в панельке персон) */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && p.teamCenterOpen && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {p.teamCenterArea}
        </div>
      )}

      {/* Доска задач (вкладка «Доска» в панельке задач) */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && p.boardOpen && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {p.boardArea}
        </div>
      )}

      {/* Превью dev-сервиса (выбран в панельке «Preview») */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && p.previewOpen && (
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {p.previewArea}
        </div>
      )}

      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && !p.previewOpen && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {chatPanel}
        </div>
      )}

      {p.openFile && !p.fileFullscreen && !p.isTablet && (
        <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0 }}>
          <div style={{ flex: chatFlex, overflow: 'hidden', minWidth: 200 }}>
            {chatPanel}
          </div>
          <Splitter orientation="v" active={dragging === 'split'} onMouseDown={handleSplitDrag} />
          <div style={{ flex: 1, overflow: 'hidden', minWidth: 200 }}>
            <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onToggleFullscreen={p.onEnterFullscreen} onOpenSidebar={openSidebar} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} />
          </div>
        </div>
      )}

      {p.openFile && (p.fileFullscreen || p.isTablet) && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onOpenSidebar={openSidebar} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} />
        </div>
      )}

      {/* === Справа: стек рабочих панелей + рельса иконок === */}
      <RightPanelStack
        isTablet={p.isTablet}
        session={p.activeSession}
        projectId={p.project.id}
        rootPath={p.project.rootPath}
        toolsEnabled={p.toolsEnabled}
        panels={p.panels}
        panelHeaderExtras={p.panelHeaderExtras}
      />
    </>
  );
}
