// Тело нового интерфейса проекта «как десктопный Claude Code» (флаг workspace-cc-panels,
// только десктоп ≥1200): слева — панель ТОЛЬКО с чатами проекта, в центре — чат
// (или файл/задача/персона/доска/коммит), справа — рельса рабочих инструментов
// со стеком панелей (RightPanelStack): План, Файлы, Задачи, Команда, Терминал, Preview.
// WorkspacePage остаётся владельцем состояния и обработчиков — сюда всё приходит
// пропсами (контент панелек тоже собирается там); HubHeader и диалоги тоже там.
import { useState, useRef, type ReactNode, type PointerEvent as ReactPointerEvent } from 'react';
import { Plus, MessageCircle } from 'lucide-react';
import type { Project, Session, Task, SkillInfo, AgentInfo } from '../../types';
import { C, FONT, ISLAND, CHAT_COLUMN_W } from '../../lib/design';
import { useCenterOffset } from '../../lib/centerOffset';
import { Button, Island } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { SessionList } from '../../components/SessionList';
import { ChatPanel } from '../../components/ChatPanel';
import { FileViewer } from '../../components/FileViewer';
import { GitCommitView } from '../../components/GitCommitView';
import { TaskDetailsPane } from '../../features/tasks/TaskDetailsPane';
import { ProjectPersonaPane } from '../../features/personas/ProjectPersonasPanel';
import { ProjectRail } from '../../features/projects/ProjectRail';
import { PanelZone } from './PanelZone';
import { useSessionPanels } from './useSessionPanels';
import { startPointerDrag } from '../../lib/pointerDrag';
import type { PanelKey } from './panelCatalog';

export type SidebarMode = 'pinned' | 'collapsed';

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
  onToggleFullscreen: () => void;
  openCommitSha: string | null;
  openCommitFile?: string | null;
  onCloseCommit: () => void;
  onOpenFileFromChat: (path: string) => void;
  onCloseFile: () => void;
  selectedTask: Task | null;
  autoEditTaskId: string | null;
  onOpenTaskSession: (sessionId: string) => void;
  onOpenFileFromTree: (path: string, line?: number) => void;
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
  // Панели проекта: доступность инструментов + готовый контент панелек.
  // Контент общий для обеих зон — панель рисует та зона, в которой она лежит.
  toolsEnabled: boolean;
  panels: Partial<Record<PanelKey, ReactNode>>;
  // Числа-кружки на кнопках проекта в рельсе (changes/tasks/terminal/preview)
  railCounts?: Partial<Record<PanelKey, number>>;
  // Сколько чатов у проекта; null — ещё не знаем. Пока чатов нет, панель «Чаты»
  // не рендерится совсем — как сайдбар в разделе «Чаты»
  chatCount: number | null;
  // Точное число от списка чатов, пока панель на экране
  onSessionsChanged: (n: number) => void;
  // Хук на явную активацию панели из рельсы (клик открыл панель) — проброс в RightPanelStack
  onPanelOpen?: (k: PanelKey) => void;
}

export function DesktopWorkspace(p: Props) {
  // Подсветка активного сплиттера: сайдбар или split чат|файл
  const [dragging, setDragging] = useState<'sidebar' | 'split' | null>(null);

  // Панели текущей сессии (План/Агенты/Персона). Раньше их собирала правая зона
  // внутри себя — и потому они были прибиты к ней; теперь это часть общего набора,
  // и они переносятся между рельсами наравне с остальными.
  const sessionPanels = useSessionPanels(p.activeSession, p.project.id, p.project.rootPath);

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
  // здесь только содержимое — список чатов на белом фоне контентной зоны, как у
  // панелей правой рельсы. Переключатель проектов жил сначала шапкой внутри этой
  // панели, потом отдельной панелью «Проекты»; теперь это док второй левой рельсы.
  // Пока чатов нет — панели нет вовсе (undefined в наборе). Так же ведёт себя сайдбар
  // раздела «Чаты»: пустой список показывать незачем, а создать чат зовёт центр.
  // chatCount === null — ещё считаем: панель показываем, чтобы она не мигала на старте.
  const chatsPanel = p.chatCount === 0 ? undefined : (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
      <SessionList project={p.project} activeSession={p.activeSession} onSelect={handleSelectSession} onSessionUpdated={p.onSessionUpdated} onSessionsChanged={p.onSessionsChanged} onCleared={p.onClearSession} isMobile={false} workflowRunningFor={p.workflowRunningFor} />
    </div>
  );

  // ОБЩИЙ набор контента панелей: обе зоны получают его целиком и рисуют только
  // те панели, что лежат именно в них. Чаты собираются здесь, инструменты проекта
  // приходят из WorkspacePage, панели сессии — из useSessionPanels.
  const zonePanels: Partial<Record<PanelKey, ReactNode>> = {
    chats: chatsPanel,
    ...p.panels,
  };

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
            Начните новый чат по этому проекту.
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

  // В центре одиночный чат — единственный режим с колонкой фиксированной ширины,
  // поэтому только ему нужна компенсация перекоса зон (файл, доска, граф и превью
  // резиновые: им положено занимать всю колонку целиком).
  // Ширина — CHAT_COLUMN_W, а не CHAT_MAX_W: компенсации отдаётся только то, что
  // остаётся сверх ПОЛНОЙ потребности ленты (колонка + жёлоб + полоса прокрутки).
  const chatOnly = !p.openFile && !p.openCommitSha && !p.selectedTask && !personaOpen
    && !p.teamCenterOpen && !p.boardOpen && !p.previewOpen && !p.graphOpen;
  const { rootRef: offsetRootRef, centerRef: offsetCenterRef } = useCenterOffset(chatOnly ? CHAT_COLUMN_W : undefined);

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
    <div ref={offsetRootRef} style={{
      flex: 1, minWidth: 0, display: 'flex', position: 'relative',
      // Снизу — просторный pad, сверху — узкий gap под шапкой; по бокам 0 —
      // обе рельсы прижаты к краям окна.
      // Фон прозрачный: дудл-холст (CanvasBackdrop) рисует корень WorkspacePage
      padding: `${ISLAND.gap}px 0 ${ISLAND.pad}px 0`,
    }}>
      {/* === Слева: рельса иконок + её панели, под рельсой — док проектов ===
          Обе зоны получают ОДИН набор контента: панель рисует та зона, в которой
          она сейчас лежит, поэтому её можно перетащить с одной стороны на другую.
          Открытие/сворачивание — иконкой рельсы, ширина тянется её сплиттером.
          Док проектов в раскладке не участвует: он вторая капсула у края окна и
          переключает проект, не уводя из воркспейса. */}
      <PanelZone
        side="left"
        panels={zonePanels}
        railCounts={p.railCounts}
        toolsEnabled={p.toolsEnabled}
        sessionPanels={sessionPanels}
        onPanelOpen={p.onPanelOpen}
        railFooter={<ProjectRail project={p.projectForEdit} onOpenSettings={p.onOpenProjectSettings} />}
      />

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

      {/* Одиночный чат — без рамки на холсте, в остров выделена только его шапка.
          overflow visible: композер стоит на нижней кромке зоны, и hidden срезал бы
          его тень (у ленты свой скролл, вылезать наружу нечему). Так же устроена
          обёртка centerBare в IslandScaffold, где чат живёт на холсте */}
      {chatOnly && (
        <div ref={offsetCenterRef} style={{ flex: 1, overflow: 'visible', minWidth: 0 }}>
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
              <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onToggleFullscreen={p.onToggleFullscreen} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} />
            </div>
          </Island>
        </div>
      )}

      {p.openFile && (p.fileFullscreen || p.isTablet) && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {/* На планшете сплита нет — тумблер режима не показываем */}
          <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onToggleFullscreen={p.isTablet ? undefined : p.onToggleFullscreen} fullscreen={p.fileFullscreen} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} />
        </div>
      )}

      {/* === Справа: стек рабочих панелей + рельса иконок === */}
      <PanelZone
        side="right"
        compact={p.isTablet}
        panels={zonePanels}
        railCounts={p.railCounts}
        toolsEnabled={p.toolsEnabled}
        sessionPanels={sessionPanels}
        onPanelOpen={p.onPanelOpen}
      />
    </div>
  );
}
