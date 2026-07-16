import { useState, useEffect, useRef, useCallback, useMemo, type ReactNode } from 'react';
import type { Project, Session, SkillsData, AuthState, Task } from '../types';
import { SessionList } from '../components/SessionList';
import { FileExplorer } from '../components/FileExplorer';
import { ChatPanel } from '../components/ChatPanel';
import { FileViewer } from '../components/FileViewer';
import { ArtifactsPanel } from '../components/ArtifactsPanel';
import { KnowledgePanel } from '../components/KnowledgePanel';
import { UsageScreen } from '../components/UsageScreen';
import { joinProject, leaveProject, onMessage, onReconnected } from '../lib/signalr';
import { loadWorkspaceState, saveWorkspaceState } from '../lib/workspaceState';
import { api } from '../lib/api';
import { C, FONT } from '../lib/design';
import { useSidebarWidth } from '../lib/sidebarWidth';
import { MOBILE_MAX, MOBILE_QUERY, TABLET_MAX } from '../lib/breakpoints';
import { PillSwitch } from '../components/Toolbar';
import type { HubTab } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { BackButton, IconButton, Splitter } from '../components/ui';
import { navPush, navReplace, parseHash, type NavSnapshot } from '../lib/nav';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { TasksPanel } from '../features/tasks/TasksPanel';
import { TaskDetailsPane } from '../features/tasks/TaskDetailsPane';
import { TaskBoard } from '../features/tasks/board/TaskBoard';
import { BoardColumnsDialog } from '../features/tasks/board/BoardColumnsDialog';
import { resolveColumns, taskColumnKey, ensureTasksLoaded } from '../lib/tasks';
import type { BoardColumn } from '../types';
import { useTasks } from '../lib/tasks';
import { ensurePersonasLoaded } from '../lib/personas';
import { ProjectPersonasPanel, ProjectPersonaPane } from '../features/personas/ProjectPersonasPanel';
import type { PersonaView } from '../features/personas/PersonaToolbar';
import { TeamCommandCenter } from '../features/personas/TeamCommandCenter';
import { ToolsSidebar, type PreviewSession } from '../components/tools/ToolsSidebar';
import { TerminalView } from '../components/terminal/TerminalView';
import { PreviewView } from '../components/preview/PreviewView';
import * as terminalApi from '../lib/terminalSignalr';

interface Props {
  project: Project;
  onGoToProjects: () => void;
  // Переключение раздела хаба «Чаты | Проекты» из верхней шапки проекта
  onSwitchHub: (t: HubTab) => void;
  auth: AuthState;
  onLogout: () => void;
}

type LeftTab = 'sessions' | 'files' | 'tasks' | 'personas' | 'tools';
type FileSubTab = 'files' | 'knowledge';

// Иконки вкладок проекта для мобильного компакт-режима (Feather-стиль, как HubTabs)
const leftTabSvg = (children: React.ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{children}</svg>
);
const LEFT_TAB_ICONS: Record<LeftTab, React.ReactNode> = {
  sessions: leftTabSvg(<path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />),
  files: leftTabSvg(<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />),
  tasks: leftTabSvg(<><path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" /></>),
  personas: leftTabSvg(<><circle cx="12" cy="8" r="4" /><path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" /></>),
  tools: leftTabSvg(<><polyline points="4 17 10 11 4 5" /><line x1="12" y1="19" x2="20" y2="19" /></>),
};

function useWindowWidth() {
  const [width, setWidth] = useState(window.innerWidth);
  useEffect(() => {
    const handler = () => setWidth(window.innerWidth);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, []);
  return width;
}

// Высота ВИДИМОЙ области (над клавиатурой). При открытии мобильной клавиатуры
// visualViewport ужимается — привязав к нему высоту контейнера, прижимаем композер
// к низу видимой части и убираем прокрутку поля ввода за экран.
function useViewportHeight() {
  const [h, setH] = useState(() => window.visualViewport?.height ?? window.innerHeight);
  useEffect(() => {
    const vv = window.visualViewport;
    const update = () => setH(vv?.height ?? window.innerHeight);
    update();
    vv?.addEventListener('resize', update);
    vv?.addEventListener('scroll', update);
    window.addEventListener('resize', update);
    return () => {
      vv?.removeEventListener('resize', update);
      vv?.removeEventListener('scroll', update);
      window.removeEventListener('resize', update);
    };
  }, []);
  return h;
}

// Контентная зона «Инструменты» (терминал/preview). ВЫНЕСЕНА из WorkspacePage на
// module-level: если определять её внутри компонента, каждый ре-рендер WorkspacePage
// (терминал шлёт activity/output → setState) создаёт новый тип компонента и React
// ремонтирует всё поддерево — xterm пересоздаётся, экран чернеет, ввод/вывод теряются.
function ToolsPaneView({
  projectId, toolsTab, terminals, activeTerminalId, activeTerminalName, terminalBusy,
  onTerminalActivity, previewSessions, activePreviewId, onStopPreview,
}: {
  projectId: string
  toolsTab: 'terminal' | 'preview'
  terminals: terminalApi.TerminalInfo[]
  activeTerminalId: string | null
  activeTerminalName?: string
  terminalBusy: boolean
  onTerminalActivity: (busy: boolean) => void
  previewSessions: PreviewSession[]
  activePreviewId: string | null
  onStopPreview: (id: string) => void
}) {
  const activePreview = previewSessions.find(p => p.id === activePreviewId);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Хедер контентной зоны — как у других панелей */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center',
        height: 52, padding: '0 14px',
        borderBottom: `1px solid ${C.divider}`, background: C.bgMain,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1, minWidth: 0 }}>
          {toolsTab === 'terminal' ? (
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.textHeading}
              strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="4 17 10 11 4 5" /><line x1="12" y1="19" x2="20" y2="19" />
            </svg>
          ) : (
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.textHeading}
              strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="3" width="20" height="14" rx="2" ry="2" /><line x1="8" y1="21" x2="16" y2="21" />
            </svg>
          )}
          <span style={{ fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {toolsTab === 'terminal' ? (activeTerminalName ?? 'Терминал') : 'Preview'}
          </span>
          {/* Индикатор активности терминала */}
          {toolsTab === 'terminal' && activeTerminalId && (
            <span style={{
              fontSize: 11, color: terminalBusy ? C.warning : C.success,
              display: 'flex', alignItems: 'center', gap: 4, marginLeft: 8,
            }}>
              <span style={{
                width: 6, height: 6, borderRadius: '50%',
                background: terminalBusy ? C.warning : C.success,
              }} />
              {terminalBusy ? 'выполняется…' : 'готов'}
            </span>
          )}
        </div>
      </div>
      {/* Контент — flex-колонка, чтобы TerminalView/PreviewView (flex:1) растянулись на всю высоту */}
      <div style={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {toolsTab === 'terminal' ? (
          terminals.length > 0 ? (
            // Каждый терминал — отдельный смонтированный xterm; неактивные скрыты (не размонтированы),
            // поэтому сохраняют буфер и продолжают копить вывод в фоне.
            terminals.map(t => (
              <div key={t.id} style={{
                flex: 1, minHeight: 0, flexDirection: 'column',
                display: t.id === activeTerminalId ? 'flex' : 'none',
              }}>
                <TerminalView
                  terminalId={t.id}
                  visible={t.id === activeTerminalId}
                  onActivity={t.id === activeTerminalId ? onTerminalActivity : undefined}
                />
              </div>
            ))
          ) : (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontSize: 14 }}>
              Выберите или создайте терминал
            </div>
          )
        ) : toolsTab === 'preview' && activePreview ? (
          <PreviewView
            preview={activePreview}
            projectId={projectId}
            onStop={onStopPreview}
          />
        ) : (
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontSize: 14 }}>
            Запустите dev-сервер
          </div>
        )}
      </div>
    </div>
  );
}

export function WorkspacePage({ project, onGoToProjects, onSwitchHub, auth, onLogout }: Props) {
  // Восстанавливаем состояние окна для этого проекта (компонент перемонтируется при входе в проект)
  const [leftTab, setLeftTab] = useState<LeftTab>(() => {
    const savedRaw = loadWorkspaceState(project.id)?.leftTab;
    // Сохранённое 'agents' — ключ до переименования вкладки персон
    const saved = (savedRaw as string) === 'agents' ? 'personas' : savedRaw;
    const ok = saved === 'sessions' || saved === 'files' || saved === 'tasks'
      || saved === 'personas' || saved === 'tools';
    return ok && (saved !== 'tools' || project.toolsEnabled) ? saved! : 'sessions';
  });
  const [fileSubTab, setFileSubTab] = useState<FileSubTab>(() => loadWorkspaceState(project.id)?.fileSubTab ?? 'files');
  const [activeSession, setActiveSession] = useState<Session | null>(() => {
    // Стартовая сессия от «Поговорить» проектной персоны (раздел «Персоны»): проект уже
    // открыт App-ом, сессию выбираем здесь — SessionList не перебьёт её авто-выбором list[0].
    try {
      const raw = sessionStorage.getItem('cc_pending_session');
      if (raw) {
        const s = JSON.parse(raw) as Session;
        if (s.projectId === project.id) { sessionStorage.removeItem('cc_pending_session'); return s; }
      }
    } catch { /* битый json — игнорируем */ }
    return loadWorkspaceState(project.id)?.activeSession ?? null;
  });
  const [pendingMessage, setPendingMessage] = useState<string | undefined>();
  const [openFile, setOpenFile] = useState<string | null>(() => loadWorkspaceState(project.id)?.openFile ?? null);
  const [fileFullscreen, setFileFullscreen] = useState(() => loadWorkspaceState(project.id)?.fileFullscreen ?? false);
  const [chatFlex, setChatFlex] = useState(1); // 1:1 = 50/50 по умолчанию
  const [workflowRunningFor, setWorkflowRunningFor] = useState<string | null>(null);
  const [showUsage, setShowUsage] = useState(false);
  // Ссылка «Подробная статистика» в pop-up бейджа fal.ai открывает единый экран «Использование»
  useEffect(() => {
    const open = () => setShowUsage(true);
    window.addEventListener('open-fal-stats', open);
    return () => window.removeEventListener('open-fal-stats', open);
  }, []);
  // Открыть только что созданную сессию этого проекта (групповой чат из ChatPanel):
  // проект уже открыт, событие приходит без ремоунта страницы
  useEffect(() => {
    const open = (e: Event) => {
      const s = (e as CustomEvent<{ session?: Session }>).detail?.session;
      if (s && s.projectId === project.id) setActiveSession(s);
    };
    window.addEventListener('cc-open-project-session', open);
    return () => window.removeEventListener('cc-open-project-session', open);
  }, [project.id]);
  const [editProjectOpen, setEditProjectOpen] = useState(false);
  const [projectForEdit, setProjectForEdit] = useState(project);
  type ToolsTab = 'terminal' | 'preview';
  const [toolsTab, setToolsTab] = useState<ToolsTab>('terminal');
  const [activeTerminalId, setActiveTerminalId] = useState<string | null>(null);
  const [activePreviewId, setActivePreviewId] = useState<string | null>(null);
  const [previewSessions, setPreviewSessions] = useState<PreviewSession[]>([]);
  const [terminalBusy, setTerminalBusy] = useState(false);
  // Список терминалов проекта — поднят сюда (не в ToolsSidebar): нужен и хедеру ToolsPane
  // для имени активного, а на мобиле сайдбар и контент — разные вью и не смонтированы разом.
  const [terminals, setTerminals] = useState<terminalApi.TerminalInfo[]>([]);
  const activeTerminalName = terminals.find(t => t.id === activeTerminalId)?.name;

  const refreshTerminals = useCallback(async () => {
    try { setTerminals(await terminalApi.listTerminals(project.id)); } catch { /* офлайн */ }
  }, [project.id]);

  // Пока открыта вкладка «Инструменты» — держим список и слушаем статусы/переименования
  useEffect(() => {
    if (leftTab !== 'tools') return;
    void refreshTerminals();
    return terminalApi.onTerminalMessage(msg => {
      if (msg.type === 'terminal_status') void refreshTerminals();
      else if (msg.type === 'terminal_renamed' && msg.terminalId) {
        setTerminals(prev => prev.map(t => t.id === msg.terminalId ? { ...t, name: msg.name ?? t.name } : t));
      }
    });
  }, [leftTab, refreshTerminals]);

  const handleCreateTerminal = useCallback(async () => {
    try {
      const t = await terminalApi.createTerminal(project.id);
      setTerminals(prev => [...prev.filter(x => x.id !== t.id), t]);
      setActiveTerminalId(t.id);
    } catch { /* офлайн */ }
  }, [project.id]);

  const handleStopTerminal = useCallback(async (id: string) => {
    await terminalApi.stopTerminal(id);
    setActiveTerminalId(prev => prev === id ? null : prev);
    void refreshTerminals();
  }, [refreshTerminals]);

  const handleRenameTerminal = useCallback(async (id: string, name: string) => {
    try {
      const updated = await terminalApi.renameTerminal(id, name);
      if (updated) setTerminals(prev => prev.map(t => t.id === id ? updated : t));
    } catch { /* офлайн */ }
  }, []);
  const activeSessionRef = useRef<Session | null>(null);
  activeSessionRef.current = activeSession;

  // Стор персон — чтобы SessionList показал аватар/имя персоны у её сессий,
  // а вкладка «Команда» знала, есть ли персоны у проекта (для пустого стейта)
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Нужно для резолва контекста «в рамках какой задачи» в ArtifactsPanel/бэйджах чата
  // (плашка в ChatOriginBadge полагается на getTaskById из уже загруженного стора)
  useEffect(() => { void ensureTasksLoaded(); }, []);

  // Инструменты выключили в настройках (projectForEdit) — уводим с вкладки «Инструменты»,
  // иначе останется висеть недоступный режим. При включении вкладка появится сама
  // (leftTabOptions пересчитывается из projectForEdit) — перезагрузка не нужна.
  useEffect(() => {
    if (leftTab === 'tools' && !projectForEdit.toolsEnabled) setLeftTab('sessions');
  }, [projectForEdit.toolsEnabled, leftTab]);

  // Вкладка «Команда»: список персон — в сайдбаре, форма — в центральной зоне.
  // Состояние выбора поднято сюда, чтобы синхронизировать список ↔ форму.
  const personasMode = leftTab === 'personas';
  const [selectedPersonaId, setSelectedPersonaId] = useState<string | null>(null);
  const [personaCreating, setPersonaCreating] = useState(false);
  // Вкладка студии персоны, на которую нужно сразу открыться (бэйдж автоматизации в чате) —
  // одноразовая, сбрасывается любым обычным выбором персоны
  const [pendingPersonaView, setPendingPersonaView] = useState<PersonaView | null>(null);
  const handlePersonaSelect = (id: string) => {
    setSelectedPersonaId(id);
    setPersonaCreating(false);
    setPendingPersonaView(null);
    navPush({ screen: 'project', project, view: isMobile ? 'chat' : 'sidebar', file: null, task: null, persona: id });
    if (isMobile) setMobileView('chat');
  };
  const handlePersonaNew = () => {
    setSelectedPersonaId(null);
    setPersonaCreating(true);
    if (isMobile) setMobileView('chat');
  };
  const handlePersonaCleared = () => {
    setSelectedPersonaId(null);
    setPersonaCreating(false);
    if (isMobile) setMobileView('sidebar');
  };
  // Командный центр — сбросить выбор персоны и показать центр команды (①-L1)
  const handleShowTeam = () => {
    setSelectedPersonaId(null);
    setPersonaCreating(false);
    navPush({ screen: 'project', project, view: isMobile ? 'chat' : 'sidebar', file: null, task: null, persona: null });
    if (isMobile) setMobileView('chat');
  };
  // После создания новой персоны переключаемся с «создания» на её редактирование
  const handlePersonaSelectAfterCreate = (id: string) => {
    setSelectedPersonaId(id);
    setPersonaCreating(false);
  };

  const handleWorkflowRunning = useCallback((active: boolean, sessionId: string) => {
    setWorkflowRunningFor(prev => {
      if (active) return sessionId;
      return prev === sessionId ? null : prev;
    });
  }, []);
  const splitContainerRef = useRef<HTMLDivElement>(null);
  const [indexedFileNames, setIndexedFileNames] = useState<Set<string>>(new Set());
  const [knowledgeDocMap, setKnowledgeDocMap] = useState<Map<string, string>>(new Map()); // filename → docId
  const [indexingFiles, setIndexingFiles] = useState<Set<string>>(new Set());
  const [indexingFolders, setIndexingFolders] = useState<Set<string>>(new Set());
  const [skillsData, setSkillsData] = useState<SkillsData | null>(null);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const handleAttachToChat = useCallback((path: string) => {
    setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path]);
  }, []);

  useEffect(() => {
    api.knowledge.getStatus(project.id).then(s => {
      const names = new Set<string>();
      const docMap = new Map<string, string>();
      for (const d of s.documents) {
        const fname = d.name.split('/').pop() ?? d.name;
        names.add(fname);
        docMap.set(fname, d.id);
      }
      setIndexedFileNames(names);
      setKnowledgeDocMap(docMap);
    }).catch(() => {});
  }, [project.id]);

  useEffect(() => {
    api.skills.list(project.id).then(setSkillsData).catch(() => {});
  }, [project.id]);
  // Скиллы для «/» композера: глобальные + workflow-скрипты + плагины (вызываются той же командой /имя)
  const composerSkills = useMemo(
    () => [...(skillsData?.skills ?? []), ...(skillsData?.workflows ?? []), ...(skillsData?.plugins ?? [])],
    [skillsData]);
  // мобайл: показываем либо sidebar, либо chat
  const [mobileView, setMobileView] = useState<'sidebar' | 'chat'>('sidebar');

const windowWidth = useWindowWidth();
  const viewportH = useViewportHeight();
  const isMobile = windowWidth <= MOBILE_MAX;
  const isTablet = windowWidth > MOBILE_MAX && windowWidth <= TABLET_MAX;

  // Задачи (за фич-флагом): вкладка «Задачи» в сайдбаре + карточка задачи в центре.
  // Открытая задача ведёт себя как открытый файл: переключение вкладок сайдбара
  // основную зону не трогает — карточка открывается кликом и закрывается крестиком.
  const allTasks = useTasks();
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  // Свежесозданная задача — её карточка открывается сразу в режиме редактирования
  const [autoEditTaskId, setAutoEditTaskId] = useState<string | null>(null);
  const tasksMode = leftTab === 'tasks';
  const selectedTask = selectedTaskId
    ? allTasks.find(t => t.id === selectedTaskId && t.projectId === project.id) ?? null
    : null;

  // Режим доски задач проекта: доска рендерится в основной области.
  const [projectBoard, setProjectBoard] = useState<boolean>(() => {
    const t = parseHash();
    if (t && t.screen === 'project' && t.projectId === project.id && t.board) return true;
    try { return localStorage.getItem(`cc_proj_board_${project.id}`) === '1'; } catch { return false; }
  });

  const showProjectBoard = tasksMode && projectBoard && !selectedTask;
  const handleProjectBoard = (on: boolean) => {
    setProjectBoard(on);
    try { localStorage.setItem(`cc_proj_board_${project.id}`, on ? '1' : '0'); } catch { /* ignore */ }
    if (on) setSelectedTaskId(null);
    // Запись истории: браузерные «назад/вперёд» входят/выходят из доски.
    // На мобиле доска живёт в основной области — переходим туда из сайдбара.
    const view: 'sidebar' | 'chat' = on && isMobile ? 'chat' : isMobile ? 'sidebar' : mobileView;
    if (on && isMobile) setMobileView('chat');
    navPush({ screen: 'project', project, view, file: null, task: null, board: on });
  };
  const projectTasks = useMemo(
    () => allTasks.filter(t => t.projectId === project.id),
    [allTasks, project.id],
  );
  const projectBoardById = useMemo(() => new Map([[project.id, project]]), [project]);
  // Кастомные колонки доски проекта (правятся в редакторе, обновляются локально после сохранения)
  const [boardColumns, setBoardColumns] = useState<BoardColumn[] | undefined>(project.boardColumns);
  const [columnsDialog, setColumnsDialog] = useState(false);
  // Проект мог освежиться серверными данными (App refetch) — подхватываем колонки из пропа
  useEffect(() => { setBoardColumns(project.boardColumns); }, [project]);
  const projectColumns = useMemo(() => resolveColumns(boardColumns), [boardColumns]);
  // Число задач в каждой колонке — для предупреждения при удалении непустой колонки
  const columnTaskCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    projectColumns.forEach(c => { counts[c.id] = 0; });
    projectTasks.forEach(t => {
      if (t.virtual) return;
      const key = taskColumnKey(t, projectColumns);
      counts[key] = (counts[key] ?? 0) + 1;
    });
    return counts;
  }, [projectTasks, projectColumns]);
  const openColumnsEditor = () => setColumnsDialog(true);
  const columnsDialogEl = columnsDialog && (
    <BoardColumnsDialog
      projectId={project.id}
      columns={projectColumns}
      taskCounts={columnTaskCounts}
      onSaved={p => { setBoardColumns(p.boardColumns); setColumnsDialog(false); }}
      onClose={() => setColumnsDialog(false)}
    />
  );
  const ProjectBoardArea = (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {isMobile && (
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', borderBottom: `1px solid ${C.border}`, background: C.bgPanel }}>
          <IconButton size="md" variant="soft" onClick={() => window.history.back()} title="К списку задач">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M15 18l-6-6 6-6" /></svg>
          </IconButton>
          <span style={{ fontFamily: FONT.sans, fontWeight: 700, fontSize: 15, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Доска · {project.name}
          </span>
        </div>
      )}
      <div style={{ flex: 1, overflowY: 'auto', boxSizing: 'border-box', padding: isMobile ? '12px 14px 20px' : '16px 22px 20px' }}>
        <TaskBoard
          tasks={projectTasks}
          columns={projectColumns}
          projectsById={projectBoardById}
          onOpenTask={t => handleSelectTask(t)}
          isMobile={isMobile}
          quickAddProjectId={project.id}
          scope="project"
          inlineToolbar={isMobile}
          onEditColumns={openColumnsEditor}
        />
      </div>
    </div>
  );

  const handleSelectTask = (task: Task, autoEdit?: boolean) => {
    setSelectedTaskId(task.id);
    setAutoEditTaskId(autoEdit ? task.id : null);
    // Открытый файл уступает место карточке задачи
    setOpenFile(null);
    if (isMobile) {
      setMobileView('chat');
      navPush({ screen: 'project', project, view: 'chat', file: null, task: task.id });
    } else {
      navPush({ screen: 'project', project, view: mobileView, file: null, task: task.id });
    }
  };

  // Переход из карточки задачи в связанный диалог
  const handleOpenTaskSession = async (sessionId: string) => {
    try {
      const sessions = await api.sessions.list(project.id);
      const s = sessions.find(x => x.id === sessionId);
      if (!s) return;
      if (!isMobile) navPush({ screen: 'project', project, view: 'sidebar', file: null, task: null });
      setLeftTab('sessions');
      handleSelectSession(s);
    } catch { /* офлайн — остаёмся на задаче */ }
  };

  const leftTabOptions: { value: LeftTab; label: string; icon?: ReactNode }[] = [
    { value: 'sessions', label: 'Чаты', icon: LEFT_TAB_ICONS.sessions },
    { value: 'files', label: 'Файлы', icon: LEFT_TAB_ICONS.files },
    { value: 'tasks', label: 'Задачи', icon: LEFT_TAB_ICONS.tasks },
    { value: 'personas' as LeftTab, label: 'Команда', icon: LEFT_TAB_ICONS.personas },
    ...(projectForEdit.toolsEnabled ? [{ value: 'tools' as LeftTab, label: 'Инструменты', icon: LEFT_TAB_ICONS.tools }] : []),
  ];

  // «Поговорить» из проектной вкладки «Команда»: сессия персоны создаётся в этом
  // проекте — открываем её на месте (переключаемся на «Чаты» и выбираем).
  const handleOpenPersonaChat = (session: Session) => {
    setLeftTab('sessions');
    handleSelectSession(session);
  };

  // Диплинк файла: App положил «projectId|путь» в sessionStorage.
  // Значение чужого проекта не трогаем — его заберёт WorkspacePage нужного проекта.
  useEffect(() => {
    const raw = sessionStorage.getItem('cc_pending_file');
    if (!raw) return;
    const sep = raw.indexOf('|');
    const [pid, path] = sep === -1 ? [project.id, raw] : [raw.slice(0, sep), raw.slice(sep + 1)];
    if (pid !== project.id) return;
    sessionStorage.removeItem('cc_pending_file');
    setOpenFile(path);
    setFileFullscreen(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Переход из календаря / диплинк задачи: App положил «projectId|taskId» в sessionStorage.
  // Забираем при монтировании и по событию cc-pending-task (клик по тосту уведомления,
  // когда проект уже открыт — ремонта страницы не происходит).
  useEffect(() => {
    const consumePendingTask = () => {
      const raw = sessionStorage.getItem('cc_pending_task');
      if (!raw) return;
      const sep = raw.indexOf('|');
      const [pid, pending] = sep === -1 ? [project.id, raw] : [raw.slice(0, sep), raw.slice(sep + 1)];
      if (pid !== project.id) return;
      sessionStorage.removeItem('cc_pending_task');
      // Флаг «сразу в редактирование» (свежесозданная из календаря)
      const edit = sessionStorage.getItem('cc_pending_task_edit') === '1';
      sessionStorage.removeItem('cc_pending_task_edit');
      setLeftTab('tasks');
      setSelectedTaskId(pending);
      if (edit) setAutoEditTaskId(pending);
      // Открытый файл уступает место карточке задачи
      setOpenFile(null);
      // Пишем запись истории с задачей — hash-URL сохраняет /task/… и после перезагрузки
      if (window.matchMedia(MOBILE_QUERY).matches) {
        setMobileView('chat');
        navPush({ screen: 'project', project, view: 'chat', file: null, task: pending });
      } else {
        navPush({ screen: 'project', project, view: 'sidebar', file: null, task: pending });
      }
    };
    consumePendingTask();
    window.addEventListener('cc-pending-task', consumePendingTask);
    return () => window.removeEventListener('cc-pending-task', consumePendingTask);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  // Диплинк на персону вкладки «Команда»: App положил «projectId|personaId» в sessionStorage
  // (бэйдж автоматизации в чате проектной персоны — см. lib/chatOrigin.ts).
  useEffect(() => {
    const consumePendingPersona = () => {
      const raw = sessionStorage.getItem('cc_pending_persona');
      if (!raw) return;
      const sep = raw.indexOf('|');
      const [pid, pending] = sep === -1 ? [project.id, raw] : [raw.slice(0, sep), raw.slice(sep + 1)];
      if (pid !== project.id) return;
      sessionStorage.removeItem('cc_pending_persona');
      const view = sessionStorage.getItem('cc_pending_persona_view');
      sessionStorage.removeItem('cc_pending_persona_view');
      setLeftTab('personas');
      setSelectedPersonaId(pending);
      setPersonaCreating(false);
      setPendingPersonaView(view === 'automation' ? 'automation' : null);
      setOpenFile(null);
      if (window.matchMedia(MOBILE_QUERY).matches) {
        setMobileView('chat');
        navPush({ screen: 'project', project, view: 'chat', file: null, task: null, persona: pending });
      } else {
        navPush({ screen: 'project', project, view: 'sidebar', file: null, task: null, persona: pending });
      }
    };
    consumePendingPersona();
    window.addEventListener('cc-pending-persona', consumePendingPersona);
    return () => window.removeEventListener('cc-pending-persona', consumePendingPersona);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  // Диплинк проектного чата (#/project/{id}/chat/{chatId}) из уведомления проактивности.
  useEffect(() => {
    const consumePendingProjectChat = async () => {
      const raw = sessionStorage.getItem('cc_pending_project_chat');
      if (!raw) return;
      const sep = raw.indexOf('|');
      const [pid, chatId] = sep === -1 ? [project.id, raw] : [raw.slice(0, sep), raw.slice(sep + 1)];
      if (pid !== project.id) return;
      sessionStorage.removeItem('cc_pending_project_chat');
      try {
        const sessions = await api.sessions.list(project.id);
        const s = sessions.find(x => x.id === chatId);
        if (s) {
          setLeftTab('sessions');
          handleSelectSession(s);
        }
      } catch { /* офлайн — остаёмся как есть */ }
    };
    consumePendingProjectChat();
    window.addEventListener('cc-pending-project-chat', consumePendingProjectChat);
    return () => window.removeEventListener('cc-pending-project-chat', consumePendingProjectChat);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);
  // Ширина сайдбара — общая для всех областей (перетаскиваемая, персистится)
  const [sidebarWidth, setSidebarWidth] = useSidebarWidth();

  // Режим сайдбара: pinned (в потоке) | collapsed (свёрнут) | open (drawer поверх контента)
  // Персистируется только 'pinned'/'collapsed'; 'open' — временное состояние
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed' | 'open'>(() => {
    const v = localStorage.getItem('cc_sidebar_mode');
    return v === 'collapsed' ? 'collapsed' : 'pinned';
  });
  useEffect(() => {
    if (sidebarMode !== 'open') {
      localStorage.setItem('cc_sidebar_mode', sidebarMode);
    }
  }, [sidebarMode]);

  // Панель «Артефакты сессии»: открыта/закрыта + ширина, персист в localStorage
  const [artifactsOpen, setArtifactsOpen] = useState(() => localStorage.getItem('cc_artifacts_open') === '1');
  useEffect(() => { localStorage.setItem('cc_artifacts_open', artifactsOpen ? '1' : '0'); }, [artifactsOpen]);
  const [artifactsWidth, setArtifactsWidth] = useState(() => {
    const v = localStorage.getItem('cc_artifacts_width');
    return v ? Math.max(240, Math.min(480, Number(v))) : 300;
  });
  useEffect(() => { localStorage.setItem('cc_artifacts_width', String(artifactsWidth)); }, [artifactsWidth]);
  const toggleArtifacts = useCallback(() => setArtifactsOpen(v => !v), []);

  // Какой сплиттер сейчас тащим — для подсветки на всём протяжении drag (даже если курсор соскользнул)
  const [draggingSplitter, setDraggingSplitter] = useState<null | 'sidebar' | 'split' | 'artifacts'>(null);
  useEffect(() => {
    if (!draggingSplitter) return;
    const up = () => setDraggingSplitter(null);
    window.addEventListener('pointerup', up);
    return () => window.removeEventListener('pointerup', up);
  }, [draggingSplitter]);

  const handleSidebarSplitterMouseDown = (e: React.PointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sidebarWidth;
    const onMove = (ev: PointerEvent) => {
      setSidebarWidth(Math.max(220, Math.min(520, startW + (ev.clientX - startX))));
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  const handleArtifactsSplitterMouseDown = (e: React.PointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = artifactsWidth;
    const onMove = (ev: PointerEvent) => {
      // Панель справа: тянем влево (clientX уменьшается) → ширина растёт
      setArtifactsWidth(Math.max(240, Math.min(480, startW - (ev.clientX - startX))));
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  const handleSelectSession = (session: Session, firstMessage?: string, autoSelect?: boolean) => {
    setActiveSession(session);
    setPendingMessage(firstMessage);
    if (!autoSelect) {
      // явный выбор — закрываем файл и открытую задачу, показываем чат во весь экран
      setOpenFile(null);
      setSelectedTaskId(null);
      // Пишем запись истории с chatId — для URL #/project/{id}/chat/{chatId}
      // и кнопки «назад/вперёд» браузера.
      if (isMobile) {
        setMobileView('chat');
        navPush({ screen: 'project', project, view: 'chat', file: null, chatId: session.id });
      } else {
        navPush({ screen: 'project', project, view: 'sidebar', file: null, chatId: session.id });
      }
    }
  };

  const handleSessionUpdated = (updated: Session) => {
    setActiveSession(prev => (prev?.id === updated.id ? updated : prev));
  };

  const handleResume = useCallback(async (message?: string) => {
    if (!activeSession || activeSession.status !== 'orphaned') return;
    try {
      const s = await api.sessions.create(project.id, activeSession.mode, activeSession.claudeSessionId ?? undefined, undefined, activeSession.model ?? undefined, activeSession.agentName ?? undefined);
      await api.sessions.delete(project.id, activeSession.id);
      handleSelectSession(s, message);
    } catch { /* офлайн или сбой — ничего не меняем */ }
  }, [activeSession, project.id]);

  // Запоминаем состояние окна (активный чат/файл, панели) для проекта
  useEffect(() => {
    saveWorkspaceState(project.id, { activeSession, openFile, fileFullscreen, leftTab, fileSubTab });
  }, [project.id, activeSession, openFile, fileFullscreen, leftTab, fileSubTab]);

  // Членство в project-группе на всё время открытия проекта (для статусов и watcher'а файлов).
  // Владелец — WorkspacePage (не SessionList, который размонтируется при переходе на «Файлы»).
  useEffect(() => {
    joinProject(project.id).catch(() => {});
    // onReconnected возвращает cleanup — иначе при смене проекта старый callback остаётся
    // навсегда и продолжает джойнить уже закрытый проект при каждом реконнекте
    const unsub = onReconnected(async () => {
      joinProject(project.id).catch(() => {});
      // Сервер не шлёт status_changed при рестарте — рефетчим статус активной сессии
      // чтобы session.status в ChatPanel не застрял в 'working' после убийства процесса
      const sess = activeSessionRef.current;
      if (!sess) return;
      try {
        const sessions = await api.sessions.list(project.id);
        const fresh = sessions.find(s => s.id === sess.id);
        if (fresh && fresh.status !== sess.status) {
          setActiveSession(prev => prev?.id === fresh.id ? { ...prev, status: fresh.status } : prev);
        }
      } catch { /* офлайн — оставляем как есть */ }
    });
    return () => { leaveProject(project.id).catch(() => {}); unsub(); };
  }, [project.id]);

  // Обновляем статус activeSession при status_changed — иначе session.status в ChatPanel frozen
  useEffect(() => {
    return onMessage(msg => {
      if (msg.type !== 'status_changed') return;
      setActiveSession(prev =>
        prev?.id === msg.sessionId
          ? { ...prev, status: msg.status as Session['status'] }
          : prev
      );
    });
  }, []);

  // Кнопки «назад/вперёд» браузера внутри проекта: восстанавливаем вид (sidebar/chat),
  // открытый файл, задачу, чат и доску из снимка истории. Уровень проекта обрабатывает App
  // из того же popstate.
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen !== 'project') return; // выход из проекта — обработает App
      // Снимок другого проекта: App сменит project и WorkspacePage перемонтируется
      // (key={project.id}) — текущий инстанс не должен применять чужой снимок
      if (s.project && s.project.id !== project.id) return;
      setMobileView(s.view ?? 'sidebar');
      const f = s.file ?? null;
      setOpenFile(f);
      if (f === null) setFileFullscreen(false);
      setSelectedTaskId(s.task ?? null);
      setProjectBoard(!!s.board);   // режим доски проекта из снимка истории
      // Персона / командный центр (вкладка «Команда») — восстанавливаем, если снимок несёт
      if (s.persona !== undefined) {
        setLeftTab('personas');
        setSelectedPersonaId(s.persona ?? null);
        setPersonaCreating(false);
        setPendingPersonaView(null);
      }
      // Активный чат — восстанавливаем через существующий механизм pending (sessionStorage + событие)
      if (s.chatId) {
        sessionStorage.setItem('cc_pending_project_chat', `${project.id}|${s.chatId}`);
        window.dispatchEvent(new Event('cc-pending-project-chat'));
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  // Командный центр активен → фиксируем в истории (persona: null), чтобы «назад» из
  // любого диплинка (задача/чат/персона) возвращал именно в командный центр
  useEffect(() => {
    if (leftTab === 'personas' && !selectedPersonaId && !personaCreating) {
      navReplace({ screen: 'project', project, view: isMobile ? mobileView : 'sidebar', file: null, task: null, persona: null });
    }
  }, [leftTab, selectedPersonaId, personaCreating]);

  // из дерева файлов → всегда полноэкранный режим
  const handleOpenFileFromTree = (filePath: string) => {
    setOpenFile(filePath);
    setFileFullscreen(true);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
  };

  // из чата → split на десктопе, fullscreen на планшете/мобайле
  const handleOpenFileFromChat = (filePath: string) => {
    setOpenFile(filePath);
    setFileFullscreen(isMobile || isTablet);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
  };

  const handleEnterFullscreen = () => setFileFullscreen(true);

  const handleSplitterMouseDown = (e: React.PointerEvent) => {
    e.preventDefault();
    const container = splitContainerRef.current;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    const SPLITTER = 5;

    const onMove = (ev: PointerEvent) => {
      const available = rect.width - SPLITTER;
      const chatW = Math.max(200, Math.min(available - 200, ev.clientX - rect.left));
      const fileW = available - chatW;
      if (fileW > 0) setChatFlex(chatW / fileW);
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  const handleTabSwitch = (tab: LeftTab) => {
    setLeftTab(tab);
    if (isMobile) setMobileView('sidebar');
  };

  const handleAddToKnowledge = useCallback(async (relativePath: string) => {
    setIndexingFiles(prev => new Set([...prev, relativePath]));
    try {
      const result = await api.knowledge.indexFile(project.id, relativePath);
      const fileName = relativePath.split('/').pop() ?? relativePath;
      setIndexedFileNames(prev => new Set([...prev, fileName]));
      setKnowledgeDocMap(prev => new Map(prev).set(fileName, result.document.id));
    } catch {
      // KnowledgePanel сразу показывает актуальный статус
    } finally {
      setIndexingFiles(prev => { const next = new Set(prev); next.delete(relativePath); return next; });
    }
  }, [project.id]);

  const handleAddFolderToKnowledge = useCallback(async (relativePath: string) => {
    setIndexingFolders(prev => new Set([...prev, relativePath]));
    try {
      const result = await api.knowledge.indexFolder(project.id, relativePath);
      setIndexedFileNames(prev => {
        const next = new Set(prev);
        for (const doc of result.documents) {
          const fname = (doc as { name: string }).name.split('/').pop() ?? (doc as { name: string }).name;
          next.add(fname);
        }
        return next;
      });
      setKnowledgeDocMap(prev => {
        const next = new Map(prev);
        for (const doc of result.documents) {
          const d = doc as { id: string; name: string };
          const fname = d.name.split('/').pop() ?? d.name;
          next.set(fname, d.id);
        }
        return next;
      });
    } catch {
      // ignore — Dify может быть не настроен
    } finally {
      setIndexingFolders(prev => { const next = new Set(prev); next.delete(relativePath); return next; });
    }
  }, [project.id]);

  const handleRemoveFromKnowledge = useCallback(async (relativePath: string) => {
    const fileName = relativePath.split('/').pop() ?? relativePath;
    const docId = knowledgeDocMap.get(fileName);
    if (!docId) return;
    try {
      await api.knowledge.deleteDocument(project.id, docId);
      setIndexedFileNames(prev => { const n = new Set(prev); n.delete(fileName); return n; });
      setKnowledgeDocMap(prev => { const n = new Map(prev); n.delete(fileName); return n; });
    } catch {
      // игнорируем
    }
  }, [project.id, knowledgeDocMap]);

  // Пропсы для вынесенного ToolsPaneView (стабильный module-level компонент)
  const toolsPaneProps = {
    projectId: project.id, toolsTab, terminals, activeTerminalId, activeTerminalName, terminalBusy,
    onTerminalActivity: setTerminalBusy, previewSessions, activePreviewId,
    onStopPreview: (id: string) => setPreviewSessions(prev => prev.filter(p => p.id !== id)),
  };

  const Sidebar = (
    <div style={{ width: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel, flexShrink: 0, height: '100%' }}>
      {/* Планшет/десктоп: строка управления панелью + строка проекта + tabs (логотип — в HubHeader) */}
      {!isMobile && (
        <div style={{ padding: '16px 16px 14px', flexShrink: 0 }}>
          {/* Строка проекта: свернуть панель + кликабельное имя (→ к списку) + управление */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 13, padding: '0 2px' }}>
            {/* Свернуть панель (◀) — в обоих режимах */}
            <IconButton
              size="sm"
              onClick={() => setSidebarMode('collapsed')}
              title="Свернуть панель"
              style={{ marginLeft: -2 }}
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M15 6l-6 6 6 6"/>
              </svg>
            </IconButton>
            {/* Индикатор + имя проекта — кликабельны, ведут к списку проектов */}
            <div
              onClick={onGoToProjects}
              title="Все проекты"
              onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
              style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1, minWidth: 0, cursor: 'pointer', borderRadius: 7, padding: '4px 6px', transition: 'background 0.12s' }}
            >
              <div style={{ width: 8, height: 8, borderRadius: '50%', background: C.accent, flexShrink: 0 }} />
              <span style={{ fontSize: 13, fontWeight: 500, color: C.textPrimary, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {projectForEdit.name}
              </span>
            </div>
            {/* В режиме open — кнопка «закрепить» (📌) */}
            {sidebarMode === 'open' && (
              <IconButton
                size="sm"
                onClick={() => setSidebarMode('pinned')}
                title="Закрепить панель"
              >
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                  <line x1="12" y1="17" x2="12" y2="22"/><path d="M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24Z"/>
                </svg>
              </IconButton>
            )}
            <IconButton
              size="sm"
              onClick={() => setShowUsage(true)}
              title="Использование (модели + fal.ai)"
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M22 12h-4l-3 9L9 3l-3 9H2"/>
              </svg>
            </IconButton>
            <IconButton
              size="sm"
              onClick={() => setEditProjectOpen(true)}
              title="Настройки проекта"
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <circle cx="12" cy="12" r="3"/>
                <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
              </svg>
            </IconButton>
          </div>
          <PillSwitch<LeftTab>
            value={leftTab}
            options={leftTabOptions}
            onChange={handleTabSwitch}
            fill
            autoCompact
          />
        </div>
      )}
      <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {leftTab === 'sessions' ? (
          <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} workflowRunningFor={workflowRunningFor ?? undefined} />
        ) : leftTab === 'tasks' ? (
          <TasksPanel project={project} selectedTaskId={selectedTaskId} onSelect={handleSelectTask} isMobile={isMobile} boardMode={projectBoard} onBoardMode={handleProjectBoard} onEditColumns={openColumnsEditor} />
        ) : leftTab === 'personas' ? (
          <ProjectPersonasPanel project={project} selectedId={personaCreating ? null : selectedPersonaId} onSelect={handlePersonaSelect} onNew={handlePersonaNew} onShowTeam={handleShowTeam} teamActive={!selectedPersonaId && !personaCreating} />
        ) : leftTab === 'tools' ? (
          <ToolsSidebar projectId={project.id} activeTab={toolsTab} onTabChange={setToolsTab}
            terminals={terminals} onCreateTerminal={handleCreateTerminal}
            onStopTerminal={handleStopTerminal} onRenameTerminal={handleRenameTerminal}
            activeTerminalId={activeTerminalId} onSelectTerminal={setActiveTerminalId}
            activePreviewId={activePreviewId} onSelectPreview={setActivePreviewId}
            previewSessions={previewSessions} onPreviewSessionsChange={setPreviewSessions}
            terminalBusy={terminalBusy} />
        ) : (
          <div style={{ flex: 1, overflow: 'hidden' }}>
            {fileSubTab === 'files'
              ? <FileExplorer project={project} activeFilePath={openFile} isMobile={isMobile} alwaysShowIcons={isTablet} onOpenFile={(f) => { handleOpenFileFromTree(f); if (isMobile) setMobileView('chat'); }} onAddToKnowledge={handleAddToKnowledge} onAddFolderToKnowledge={handleAddFolderToKnowledge} onRemoveFromKnowledge={handleRemoveFromKnowledge} indexedFileNames={indexedFileNames} indexingFiles={indexingFiles} indexingFolders={indexingFolders} onAttachToChat={activeSession && !fileFullscreen ? handleAttachToChat : undefined} onOpenKnowledge={() => setFileSubTab('knowledge')} />
              : <KnowledgePanel project={project} isMobile={isMobile} alwaysShowIcons={isTablet} onDocumentsChanged={setIndexedFileNames} onBack={() => setFileSubTab('files')} />
            }
          </div>
        )}
      </div>
    </div>
  );

  if (isMobile) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: viewportH, background: C.bgPanel, fontFamily: FONT.sans, overflow: 'hidden', position: 'relative' }}>
        {/* Верхняя шапка — только в режиме списка (sidebar). В режиме чата своя
            самодостаточная шапка ChatHeaderBar с кнопкой «назад»; у файла — шапка FileViewer */}
        {!openFile && mobileView === 'sidebar' && (
          <div style={{ padding: '10px 14px', borderBottom: `1px solid ${C.border}`, background: C.bgPanel, display: 'flex', alignItems: 'center', gap: 10, flexShrink: 0 }}>
            <BackButton onClick={onGoToProjects} title={project.name} style={{ flex: 1, minHeight: 40 }}>
              <span style={{ fontWeight: 700, fontSize: 15, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{project.name}</span>
            </BackButton>
            <PillSwitch<LeftTab>
              value={leftTab}
              options={leftTabOptions}
              onChange={handleTabSwitch}
              isMobile
              autoCompact
            />
            <IconButton
              size="md"
              onClick={() => setShowUsage(true)}
              title="Использование (модели + fal.ai)"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M22 12h-4l-3 9L9 3l-3 9H2"/>
              </svg>
            </IconButton>
          </div>
        )}
        {/* Sidebar — ВСЕГДА в DOM: FileExplorer не теряет текущий путь при смене вида */}
        <div style={{ flex: 1, display: !openFile && mobileView === 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
            {leftTab === 'sessions'
              ? <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} workflowRunningFor={workflowRunningFor ?? undefined} />
              : leftTab === 'tasks'
              ? <TasksPanel project={project} selectedTaskId={selectedTaskId} onSelect={handleSelectTask} isMobile={isMobile} boardMode={projectBoard} onBoardMode={handleProjectBoard} onEditColumns={openColumnsEditor} />
              : leftTab === 'personas'
              ? <ProjectPersonasPanel project={project} selectedId={personaCreating ? null : selectedPersonaId} onSelect={handlePersonaSelect} onNew={handlePersonaNew} onShowTeam={handleShowTeam} teamActive={!selectedPersonaId && !personaCreating} />
              : leftTab === 'tools'
              ? <ToolsPaneView {...toolsPaneProps} />
              : (
                <div style={{ flex: 1, overflow: 'hidden' }}>
                  {fileSubTab === 'files'
                    ? <FileExplorer project={project} activeFilePath={openFile} isMobile={isMobile} alwaysShowIcons={isTablet} onOpenFile={handleOpenFileFromTree} onAddToKnowledge={handleAddToKnowledge} onAddFolderToKnowledge={handleAddFolderToKnowledge} onRemoveFromKnowledge={handleRemoveFromKnowledge} indexedFileNames={indexedFileNames} indexingFiles={indexingFiles} indexingFolders={indexingFolders} onAttachToChat={activeSession && !fileFullscreen ? handleAttachToChat : undefined} onOpenKnowledge={() => setFileSubTab('knowledge')} />
                    : <KnowledgePanel project={project} isMobile={isMobile} alwaysShowIcons={isTablet} onDocumentsChanged={setIndexedFileNames} onBack={() => setFileSubTab('files')} />
                  }
                </div>
              )
            }
          </div>
        </div>
        {/* Чат (или карточка задачи в режиме «Задачи») — ВСЕГДА в DOM */}
        <div style={{ flex: 1, display: !openFile && mobileView !== 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          {leftTab === 'tools'
            ? <ToolsPaneView {...toolsPaneProps} />
            : personasMode
            ? ((selectedPersonaId || personaCreating)
                ? <ProjectPersonaPane project={project} personaId={personaCreating ? null : selectedPersonaId} creating={personaCreating} initialView={pendingPersonaView} onOpenChat={handleOpenPersonaChat} onSelectPersona={handlePersonaSelectAfterCreate} onCleared={handlePersonaCleared} onBack={handlePersonaCleared} />
                : <TeamCommandCenter project={project} onOpenPersona={handlePersonaSelect} onNewPersona={handlePersonaNew} onOpenSession={handleOpenPersonaChat} onOpenSessionById={handleOpenTaskSession} />)
            : tasksMode
            ? (selectedTask
                ? <TaskDetailsPane key={selectedTask.id} task={selectedTask} project={project} isMobile startInEdit={selectedTask.id === autoEditTaskId} onBack={() => window.history.back()} onOpenSession={handleOpenTaskSession} onOpenFile={handleOpenFileFromTree} onDeleted={() => { setSelectedTaskId(null); window.history.back(); }} />
                : showProjectBoard
                ? ProjectBoardArea
                : <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontSize: 14 }}>Выберите задачу</div>)
            : activeSession
            ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onBack={() => window.history.back()} onWorkflowRunning={handleWorkflowRunning} skills={composerSkills} agents={skillsData?.agents} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} onResume={handleResume} artifactsOpen={artifactsOpen} onToggleArtifacts={toggleArtifacts} />
            : <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontSize: 14 }}>Выберите или создайте чат</div>
          }
        </div>
        {/* Просмотр файла — FileViewer имеет свою шапку */}
        {openFile && (
          <div style={{ flex: 1, overflow: 'hidden' }}>
            <FileViewer project={project} filePath={openFile} isMobile onClose={() => window.history.back()} />
          </div>
        )}
        {/* Панель «Артефакты сессии» — мобайл: drawer поверх чата */}
        {artifactsOpen && activeSession && !openFile && !personasMode && (
          <>
            <div onClick={() => setArtifactsOpen(false)}
              style={{ position: 'absolute', inset: 0, zIndex: 900, background: C.overlay }} />
            <div style={{ position: 'absolute', top: 0, right: 0, bottom: 0, zIndex: 901, width: 'min(92vw, 380px)', boxShadow: '-4px 0 20px rgba(20,16,10,0.18)' }}>
              <ArtifactsPanel sessionId={activeSession.id} projectId={project.id} rootPath={project.rootPath} isMobile personaId={activeSession.personaId} session={activeSession}
                onOpenFile={(f) => { setArtifactsOpen(false); handleOpenFileFromChat(f); }} onClose={() => setArtifactsOpen(false)} />
            </div>
          </>
        )}
        {columnsDialogEl}
        {showUsage && <UsageScreen onClose={() => setShowUsage(false)} />}
        {editProjectOpen && (
          <EditDialog
            project={projectForEdit}
            onSuccess={updated => { setProjectForEdit(updated); setEditProjectOpen(false); }}
            onClose={() => setEditProjectOpen(false)}
          />
        )}
      </div>
    );
  }

  const NoSession = (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontSize: 14 }}>
      Выберите или создайте чат
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, overflow: 'hidden' }}>
      {/* Единый верхний хаб-хедер на всю ширину (симметрия с разделом «Чаты») */}
      <HubHeader value="projects" onTab={onSwitchHub} auth={auth} onLogout={onLogout} />

      {/* Тело: сайдбар + контент. position:relative — чтобы drawer/overlay легли под хедер */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden', position: 'relative' }}>

      {/* === Pinned: sidebar в flex-потоке, толкает контент === */}
      {sidebarMode === 'pinned' && (
        <>
          <div style={{ width: sidebarWidth, flexShrink: 0, height: '100%' }}>
            {Sidebar}
          </div>
          <Splitter orientation="v" active={draggingSplitter === 'sidebar'}
            onMouseDown={e => { setDraggingSplitter('sidebar'); handleSidebarSplitterMouseDown(e); }} />
        </>
      )}

      {/* === Collapsed / Open: sidebar absolute drawer === */}
      {sidebarMode !== 'pinned' && (
        <div style={{
          position: 'absolute', top: 0, left: 0, bottom: 0, zIndex: 10,
          width: 320,
          transform: sidebarMode === 'open' ? 'translateX(0)' : 'translateX(-320px)',
          transition: 'transform 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
          boxShadow: sidebarMode === 'open' ? '4px 0 20px rgba(20,16,10,0.15)' : 'none',
        }}>
          {Sidebar}
        </div>
      )}

      {/* Overlay — только когда drawer открыт */}
      {sidebarMode === 'open' && (
        <div
          onClick={() => setSidebarMode('collapsed')}
          style={{ position: 'absolute', inset: 0, zIndex: 9, background: C.overlay }}
        />
      )}

      {/* Проп для ChatPanel — открывает drawer когда sidebar не pinned */}
      {(() => {
        const openSidebar = sidebarMode !== 'pinned' ? () => setSidebarMode('open') : undefined;

        // NoSession с топбаром для ☰ (когда нет активного чата)
        const NoSessionWithBar = (
          <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
            {sidebarMode === 'collapsed' && (
              <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}`, background: C.bgMain }}>
                <IconButton
                  size="md"
                  variant="soft"
                  onClick={() => setSidebarMode('open')}
                  title="Открыть панель"
                >
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                    <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
                  </svg>
                </IconButton>
              </div>
            )}
            {NoSession}
          </div>
        );

        // Вкладка «Инструменты»: центральная зона = TerminalView или PreviewView
        if (leftTab === 'tools') {
          const collapsedBar = sidebarMode === 'collapsed' && (
            <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}`, background: C.bgMain }}>
              <IconButton size="md" variant="soft" onClick={() => setSidebarMode('open')} title="Открыть панель">
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                  <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
                </svg>
              </IconButton>
            </div>
          );
          return (
            <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
              {collapsedBar}
              <ToolsPaneView {...toolsPaneProps} />
            </div>
          );
        }

        if (personasMode) {
          const collapsedBar = sidebarMode === 'collapsed' && (
            <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}`, background: C.bgMain }}>
              <IconButton size="md" variant="soft" onClick={() => setSidebarMode('open')} title="Открыть панель">
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                  <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
                </svg>
              </IconButton>
            </div>
          );
          return (
            <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
              {collapsedBar}
              <div style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
                {(selectedPersonaId || personaCreating)
                  ? <ProjectPersonaPane project={project} personaId={personaCreating ? null : selectedPersonaId} creating={personaCreating} initialView={pendingPersonaView} onOpenChat={handleOpenPersonaChat} onSelectPersona={handlePersonaSelectAfterCreate} onCleared={handlePersonaCleared} />
                  : <TeamCommandCenter project={project} onOpenPersona={handlePersonaSelect} onNewPersona={handlePersonaNew} onOpenSession={handleOpenPersonaChat} onOpenSessionById={handleOpenTaskSession} />}
              </div>
            </div>
          );
        }

        // Вкладка «Инструменты» — сразу ToolsPane, не трогаем openFile/selectedTask
        if ((leftTab as string) === 'tools') {
          return <ToolsPaneView {...toolsPaneProps} />;
        }

        return (
          <>
            {/* Открытая задача — карточка в основной зоне (как открытый файл), ✕ возвращает чат */}
            {!openFile && selectedTask && (
              <div style={{ flex: 1, overflow: 'hidden' }}>
                <TaskDetailsPane key={selectedTask.id} task={selectedTask} project={project} startInEdit={selectedTask.id === autoEditTaskId} onOpenSession={handleOpenTaskSession} onOpenFile={handleOpenFileFromTree} onClose={() => window.history.back()} onDeleted={() => { setSelectedTaskId(null); window.history.back(); }} />
              </div>
            )}

            // Нет открытого файла и задачи — доска задач проекта, иначе чат, иначе инструменты
            {!openFile && !selectedTask && (
              <div style={{ flex: 1, overflow: 'hidden' }}>
                {showProjectBoard
                  ? ProjectBoardArea
                  : activeSession
                  ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onWorkflowRunning={handleWorkflowRunning} onOpenSidebar={openSidebar} skills={composerSkills} agents={skillsData?.agents} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} onResume={handleResume} artifactsOpen={artifactsOpen} onToggleArtifacts={toggleArtifacts} />
                  : NoSessionWithBar}
              </div>
            )}

            {/* Split: файл из чата, только на десктопе */}
            {openFile && !fileFullscreen && !isTablet && (
              <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0 }}>
                <div style={{ flex: chatFlex, overflow: 'hidden', minWidth: 200 }}>
                  {activeSession
                    ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onWorkflowRunning={handleWorkflowRunning} onOpenSidebar={openSidebar} skills={composerSkills} agents={skillsData?.agents} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} onResume={handleResume} artifactsOpen={artifactsOpen} onToggleArtifacts={toggleArtifacts} />
                    : NoSessionWithBar}
                </div>
                <Splitter orientation="v" active={draggingSplitter === 'split'}
                  onMouseDown={e => { setDraggingSplitter('split'); handleSplitterMouseDown(e); }} />
                <div style={{ flex: 1, overflow: 'hidden', minWidth: 200 }}>
                  <FileViewer project={project} filePath={openFile} onClose={() => window.history.back()} onToggleFullscreen={handleEnterFullscreen} onOpenSidebar={openSidebar} />
                </div>
              </div>
            )}

            {/* Fullscreen: файл из дерева или планшет */}
            {openFile && (fileFullscreen || isTablet) && (
              <div style={{ flex: 1, overflow: 'hidden' }}>
                <FileViewer project={project} filePath={openFile} onClose={() => window.history.back()} onOpenSidebar={openSidebar} />
              </div>
            )}
          </>
        );
      })()}

      {/* Панель «Артефакты сессии» — десктоп: боковая колонка в потоке (никогда поверх) */}
      {artifactsOpen && activeSession && !isTablet && !personasMode && (
        <>
          <Splitter orientation="v" active={draggingSplitter === 'artifacts'}
            onMouseDown={e => { setDraggingSplitter('artifacts'); handleArtifactsSplitterMouseDown(e); }} />
          <div style={{ width: artifactsWidth, flexShrink: 0, height: '100%' }}>
            <ArtifactsPanel sessionId={activeSession.id} projectId={project.id} rootPath={project.rootPath} personaId={activeSession.personaId} session={activeSession}
              onOpenFile={handleOpenFileFromChat} onClose={() => setArtifactsOpen(false)} />
          </div>
        </>
      )}

      {/* Панель «Артефакты сессии» — планшет: drawer поверх контента (узкий экран) */}
      {artifactsOpen && activeSession && isTablet && !personasMode && (
        <>
          <div onClick={() => setArtifactsOpen(false)}
            style={{ position: 'absolute', inset: 0, zIndex: 19, background: C.overlay }} />
          <div style={{ position: 'absolute', top: 0, right: 0, bottom: 0, zIndex: 20, width: 'min(85vw, 360px)', boxShadow: '-4px 0 20px rgba(20,16,10,0.15)' }}>
            <ArtifactsPanel sessionId={activeSession.id} projectId={project.id} rootPath={project.rootPath} personaId={activeSession.personaId} session={activeSession}
              onOpenFile={(f) => { handleOpenFileFromChat(f); setArtifactsOpen(false); }} onClose={() => setArtifactsOpen(false)} />
          </div>
        </>
      )}

      </div>

      {columnsDialogEl}
      {showUsage && <UsageScreen onClose={() => setShowUsage(false)} />}
      {editProjectOpen && (
        <EditDialog
          project={projectForEdit}
          onSuccess={updated => { setProjectForEdit(updated); setEditProjectOpen(false); }}
          onClose={() => setEditProjectOpen(false)}
        />
      )}
    </div>
  );
}
