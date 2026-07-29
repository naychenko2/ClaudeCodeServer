// Тело нового интерфейса проекта «как десктопный Claude Code» (флаг workspace-cc-panels,
// только десктоп ≥1200): слева — панель ТОЛЬКО с чатами проекта, в центре — чат
// (или файл/задача/персона/доска/коммит), справа — рельса рабочих инструментов
// со стеком панелей (RightPanelStack): План, Файлы, Задачи, Команда, Терминал, Preview.
// WorkspacePage остаётся владельцем состояния и обработчиков — сюда всё приходит
// пропсами (контент панелек тоже собирается там); HubHeader и диалоги тоже там.
import { useState, useRef, type ReactNode, type PointerEvent as ReactPointerEvent } from 'react';
import { Plus, MessageCircle } from 'lucide-react';
import type { Project, Session, Task, SkillInfo, AgentInfo } from '../../types';
import { C, FONT, ISLAND } from '../../lib/design';
import { Button, Island } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { SessionList } from '../../components/SessionList';
import { ChatPanel } from '../../components/ChatPanel';
import { FileViewer } from '../../components/FileViewer';
import { GitCommitView } from '../../components/GitCommitView';
import { TaskDetailsPane } from '../../features/tasks/TaskDetailsPane';
import { ProjectPersonaPane } from '../../features/personas/ProjectPersonasPanel';
import { SidebarProjectSwitcher } from '../../features/projects/SidebarProjectSwitcher';
import { RightPanelStack } from './RightPanelStack';
import { LeftPanelStack } from './LeftPanelStack';
import { startPointerDrag } from '../../lib/pointerDrag';
import type { LeftPanelKey, PanelKey } from './panelStackState';

export type SidebarMode = 'pinned' | 'collapsed';

// Dev-заглушки левых панелей. В рельсе реально живут только «Чаты», поэтому
// проверить раскладку колонками (перетаскивание, solo, ресайз) в интерфейсе
// нечем — под import.meta.env.DEV подкладываем пустышки. В production-бандле
// ветка вырезается DCE, как и витрина дизайн-системы #/ui-kit.
const devLeftPanel = (title: string): ReactNode => (
  <div style={{ padding: 16, fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, lineHeight: 1.5 }}>
    Заглушка «{title}» — только в dev, для проверки раскладки левых панелей.
  </div>
);
const DEV_LEFT_PANELS: Partial<Record<LeftPanelKey, ReactNode>> = import.meta.env.DEV
  ? { files: devLeftPanel('Файлы'), tasks: devLeftPanel('Задачи'), personas: devLeftPanel('Команда') }
  : {};

interface Props {
  // Планшет (601–1199): файл всегда fullscreen, правая зона — упрощённый solo
  isTablet?: boolean;
  project: Project;
  // Имя проекта в шапке панели чатов — из projectForEdit (обновляется после настроек)
  projectForEdit: Project;
  onOpenProjectSettings: () => void;
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
  // Документ «Граф зависимостей»: открывается из панельки «Граф», живёт в центре
  graphOpen: boolean;
  graphArea: ReactNode;
  // Правая рельса: доступность инструментов + готовый контент панелек
  toolsEnabled: boolean;
  panels: Partial<Record<Exclude<PanelKey, 'plan'>, ReactNode>>;
  // Контролы в шапки карточек панелей (напр. переключатель видов задач)
  panelHeaderExtras?: Partial<Record<PanelKey, ReactNode>>;
  // Числа-кружки на кнопках проекта в рельсе (changes/tasks/terminal/preview)
  railCounts?: Partial<Record<PanelKey, number>>;
  // Хук на явную активацию панели из рельсы (клик открыл панель) — проброс в RightPanelStack
  onPanelOpen?: (k: PanelKey) => void;
}

export function DesktopWorkspace(p: Props) {
  // Подсветка активного сплиттера: сайдбар или split чат|файл
  const [dragging, setDragging] = useState<'sidebar' | 'split' | null>(null);

  // Пропорция чат/файл в split-режиме (как chatFlex в старой ветке; не персистится)
  const [chatFlex, setChatFlex] = useState(1);
  const splitContainerRef = useRef<HTMLDivElement>(null);

  // Split чат|файл: пересчёт пропорции из пиксельных ширин (копия handleSplitterMouseDown)
  const handleSplitDrag = (e: ReactPointerEvent) => {
    e.preventDefault();
    const container = splitContainerRef.current;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    setDragging('split');
    startPointerDrag(
      ev => {
        const chatW = Math.max(200, Math.min(rect.width - 200, ev.clientX - rect.left));
        setChatFlex(chatW / (rect.width - chatW));
      },
      { onEnd: () => setDragging(null) },
    );
  };

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

  // Контент панели «Чаты» левой рельсы. Заголовок панели рисует PanelShell, поэтому
  // здесь только содержимое: переключатель проектов и список чатов на белом фоне
  // контентной зоны — как у панелей правой рельсы.
  // Переключатель проектов пока живёт в контенте панели; в планах — вынести его
  // в собственную панель рельсы.
  const chatsPanel = (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
      <div style={{ padding: '8px 10px', flexShrink: 0, borderBottom: `1px solid ${C.border}` }}>
        {/* Плашка проекта = переключатель проектов; настройки открываются
            кликом по иконке активного проекта */}
        <SidebarProjectSwitcher project={p.projectForEdit} onOpenSettings={p.onOpenProjectSettings} />
      </div>
      <div style={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        <SessionList project={p.project} activeSession={p.activeSession} onSelect={handleSelectSession} onSessionUpdated={p.onSessionUpdated} onCleared={p.onClearSession} isMobile={false} workflowRunningFor={p.workflowRunningFor} />
      </div>
    </div>
  );

  // Фабрика центра-чата: одиночный режим — чат без рамки с шапкой-островом
  // (headerIsland), в split рядом с файлом — обычный вид внутри своего острова
  const chatPanel = (headerIsland: boolean) => p.activeSession ? (
    <ChatPanel
      session={p.activeSession} project={p.project} onOpenFile={p.onOpenFileFromChat}
      pendingMessage={p.pendingMessage} onPendingMessageSent={p.onPendingMessageSent}
      onSessionUpdated={p.onSessionUpdated} isMobile={false} onWorkflowRunning={p.onWorkflowRunning}
      skills={p.skills} agents={p.agents}
      attachedFiles={p.attachedFiles} onAttachedFilesChange={p.onAttachedFilesChange}
      headerIsland={headerIsland}
    />
  ) : (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
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

  // Центральный остров: карточка на холсте, внутри — оригинальная обёртка режима
  // (flex:1 в колонке острова растягивает её на всю высоту). По бокам — доп. воздух
  // (ISLAND.centerGap сверх зазора-сплиттера), чтобы карточка не липла к соседям
  const centerIsland = (children: ReactNode) => (
    <Island bg={C.bgMain} style={{ flex: 1, minWidth: 0, margin: `0 ${ISLAND.centerGap}px` }}>
      {children}
    </Island>
  );

  return (
    // Холст Islands: собственный relative-контекст (fullscreen-панель и планшетный
    // drawer RightPanelStack позиционируются absolute от него). Справа padding нет —
    // рельса инструментов прижата к краю окна.
    <div style={{
      flex: 1, minWidth: 0, display: 'flex', position: 'relative',
      // Снизу — просторный pad, сверху — узкий gap под шапкой; по бокам 0 —
      // обе рельсы прижаты к краям окна.
      // Фон прозрачный: дудл-холст (CanvasBackdrop) рисует корень WorkspacePage
      padding: `${ISLAND.gap}px 0 ${ISLAND.pad}px 0`,
    }}>
      {/* === Слева: рельса иконок + панель чатов (зеркало правой рельсы) ===
          Открытие/сворачивание — иконкой рельсы, ширина тянется её сплиттером;
          прежние sidebarMode/useSidebarWidth здесь больше не нужны. */}
      <LeftPanelStack panels={{ chats: chatsPanel, ...DEV_LEFT_PANELS }} />

      {/* === Центр: коммит → задача → персона → доска → файл (split/fullscreen) → чат === */}
      {!p.openFile && p.openCommitSha && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex' }}>
          <GitCommitView project={p.project} sha={p.openCommitSha} initialPath={p.openCommitFile} onClose={p.onCloseCommit} />
        </div>
      )}

      {!p.openFile && !p.openCommitSha && p.selectedTask && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <TaskDetailsPane key={p.selectedTask.id} task={p.selectedTask} project={p.project} startInEdit={p.selectedTask.id === p.autoEditTaskId} onOpenSession={p.onOpenTaskSession} onOpenFile={p.onOpenFileFromTree} onClose={p.onCloseTask} onDeleted={p.onCloseTask} />
        </div>
      )}

      {/* Студия персоны из панельки «Команда»: закрытие — крестиком справа
          (левой стрелки «назад» на десктопе нет) */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && personaOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <ProjectPersonaPane project={p.project} personaId={p.personaCreating ? null : p.selectedPersonaId} creating={p.personaCreating} onOpenChat={p.onOpenPersonaChat} onSelectPersona={p.onPersonaSelectAfterCreate} onCleared={p.onPersonaCleared} onClose={p.onPersonaCleared} />
        </div>
      )}

      {/* Командный центр (кнопка «Команда» в панельке персон) */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && p.teamCenterOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {p.teamCenterArea}
        </div>
      )}

      {/* Доска задач (вкладка «Доска» в панельке задач) */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && p.boardOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {p.boardArea}
        </div>
      )}

      {/* Превью dev-сервиса (выбран в панельке «Preview») */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && p.previewOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {p.previewArea}
        </div>
      )}

      {/* Документ «Граф зависимостей» (открыт из панельки «Граф») */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && !p.previewOpen && p.graphOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {p.graphArea}
        </div>
      )}

      {/* Одиночный чат — без рамки на холсте, в остров выделена только его шапка */}
      {!p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && !p.previewOpen && !p.graphOpen && (
        <div style={{ flex: 1, overflow: 'hidden', minWidth: 0 }}>
          {chatPanel(true)}
        </div>
      )}

      {/* Split чат|файл — ДВА острова, ресайз живёт в зазоре между ними */}
      {p.openFile && !p.fileFullscreen && !p.isTablet && (
        <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0, margin: `0 ${ISLAND.centerGap}px` }}>
          <Island bg={C.bgMain} style={{ flex: chatFlex, minWidth: 200 }}>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              {chatPanel(false)}
            </div>
          </Island>
          <IslandSplitter orientation="v" active={dragging === 'split'} onMouseDown={handleSplitDrag} />
          <Island bg={C.bgMain} style={{ flex: 1, minWidth: 200 }}>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onToggleFullscreen={p.onEnterFullscreen} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} />
            </div>
          </Island>
        </div>
      )}

      {p.openFile && (p.fileFullscreen || p.isTablet) && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} />
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
        railCounts={p.railCounts}
        onPanelOpen={p.onPanelOpen}
      />
    </div>
  );
}
