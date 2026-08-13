import { useState, useEffect, useRef, useCallback, useMemo, useLayoutEffect, useReducer, type ReactNode } from 'react';
import { Plus, MessageCircle, Network, Puzzle, GitCompare, BookOpen } from 'lucide-react';
import type { Project, Session, SkillsData, AuthState, Task, ProjectService } from '../types';
import { SessionList } from '../components/SessionList';
import { FileExplorer } from '../components/FileExplorer';
import { ChatPanel } from '../components/ChatPanel';
import { FileViewer } from '../components/FileViewer';
import { GitCommitView } from '../components/GitCommitView';
import { GitChangesRail } from '../components/GitChangesRail';
import { PanelZone } from './workspace/PanelZone';
import { useSessionPanels } from './workspace/useSessionPanels';
import { SESSION_KEYS } from './workspace/panelCatalog';
import { KnowledgePanel } from '../components/KnowledgePanel';
import { ModelsSpendModal } from '../features/modelsSpend/ModelsSpendModal';
import { ProjectIntroCard } from '../features/projects/ProjectIntroCard';
import { subscribeModelProvidersNav } from '../lib/modelProvidersNav';
import { joinProject, leaveProject, onMessage, onReconnected } from '../lib/signalr';
import { loadWorkspaceState, saveWorkspaceState, loadFileFullscreenPref, saveFileFullscreenPref, isLeftTab, type LeftTab } from '../lib/workspaceState';
import { api } from '../lib/api';
import { markChatRead } from '../lib/chatReadState';
import { refreshProjectActivity } from '../lib/projectActivity';
import { C, FONT } from '../lib/design';
import { MOBILE_MAX, MOBILE_QUERY, TABLET_MAX } from '../lib/breakpoints';
import { PillSwitch } from '../components/Toolbar';
import { ToolbarOverflowMenu, type OverflowItem } from '../components/ToolbarOverflowMenu';
import type { HubTabValue } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { BackButton, Button, IconButton } from '../components/ui';
import { PageCanvas } from '../components/ui/PageCanvas';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { showToast } from '../lib/toast';
import { navPush, navReplace, parseHash, type NavSnapshot } from '../lib/nav';
import { EditDialog } from '../features/projects/dialogs/EditDialog';
import { TasksPanel } from '../features/tasks/TasksPanel';
import { useTaskFilters, useTaskGroupTab } from '../lib/taskFilters';
import { TaskDetailsPane } from '../features/tasks/TaskDetailsPane';
import { TaskBoard } from '../features/tasks/board/TaskBoard';
import { BoardColumnsDialog } from '../features/tasks/board/BoardColumnsDialog';
import { resolveColumns, taskColumnKey, ensureTasksLoaded } from '../lib/tasks';
import type { BoardColumn } from '../types';
import { useTasks } from '../lib/tasks';
import { useGitState, ensureGit } from '../lib/git';
import { ensurePersonasLoaded } from '../lib/personas';
import { createChatWithContextPersona } from '../lib/defaultPersona';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { ProjectPersonasPanel, ProjectPersonaPane } from '../features/personas/ProjectPersonasPanel';
import type { PersonaView } from '../features/personas/PersonaToolbar';
import { TeamCommandCenter } from '../features/personas/TeamCommandCenter';
import { ToolsSidebar } from '../components/tools/ToolsSidebar';
import { TerminalView } from '../components/terminal/TerminalView';
import { PreviewView } from '../components/preview/PreviewView';
import * as terminalApi from '../lib/terminalSignalr';
import { DesktopWorkspace } from './workspace/DesktopWorkspace';
import { useProjectTerminals } from '../hooks/useProjectTerminals';
import { useProjectServices } from '../hooks/useProjectServices';
import { TerminalPanelContent, PreviewPanelContent } from './workspace/panels';
import { DocsPanel } from './workspace/DocsPanel';
import { DossierHistoryPanel } from './workspace/DossierHistoryPanel';
import { wsPanels } from './workspace/panelStackState';
import { CodeGraphPanel } from '../features/codegraph/CodeGraphPanel';
import { SkillsPanel } from '../components/SkillsPanel';
import { CodeGraphDocument } from '../features/codegraph/CodeGraphDocument';
import { buildCodeGraph } from '../lib/codeGraph';
import { useReaderPanel } from './workspace/reader/useReaderPanel';

interface Props {
  project: Project;
  onGoToProjects: () => void;
  // Переключение раздела хаба «Чаты | Проекты» из верхней шапки проекта
  onSwitchHub: (t: HubTabValue) => void;
  auth: AuthState;
  onLogout: () => void;
}

// LeftTab живёт в lib/workspaceState — там же, где список для восстановления из
// localStorage: держать union в двух местах уже приводило к потерянным вкладкам

// Иконки вкладок проекта для мобильного компакт-режима (Feather-стиль, как HubTabs)
const leftTabSvg = (children: React.ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{children}</svg>
);
const LEFT_TAB_ICONS: Record<LeftTab, React.ReactNode> = {
  sessions: leftTabSvg(<path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />),
  files: leftTabSvg(<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />),
  // Та же иконка, что у панели «Изменения» в рельсе (PANEL_META.changes): на мобиле
  // рельсы нет, и вкладка — единственный путь к git
  changes: <GitCompare size={18} strokeWidth={2} />,
  // База знаний проекта — иконка панели knowledge из того же реестра
  knowledge: <BookOpen size={18} strokeWidth={2} />,
  tasks: leftTabSvg(<><path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" /></>),
  personas: leftTabSvg(<><circle cx="12" cy="8" r="4" /><path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" /></>),
  // Пазл — та же метафора, что у панели «Навыки» в рельсе (PANEL_META.skills)
  skills: <Puzzle size={18} strokeWidth={2} />,
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
  onTerminalActivity, previewServices, activePreviewId, onStopPreview, onClosePreview, onBack,
}: {
  projectId: string
  toolsTab: 'terminal' | 'preview'
  terminals: terminalApi.TerminalInfo[]
  activeTerminalId: string | null
  activeTerminalName?: string
  terminalBusy: boolean
  onTerminalActivity: (busy: boolean) => void
  previewServices: ProjectService[]
  activePreviewId: string | null
  onStopPreview: (id: string) => void
  onClosePreview: () => void
  onBack?: () => void
}) {
  const activePreview = previewServices.find(s => s.id === activePreviewId);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Хедер контентной зоны — как у других панелей */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center',
        height: 52, padding: '0 14px',
        borderBottom: `1px solid ${C.divider}`, background: C.bgMain,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1, minWidth: 0 }}>
          {/* Мобилка: «назад» к сайдбару инструментов (двухуровневая навигация) */}
          {onBack && (
            <button onClick={onBack} title="К инструментам" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: 36, height: 36, marginLeft: -6, border: 'none', background: 'transparent', cursor: 'pointer', color: C.textSecondary, borderRadius: 8, flexShrink: 0 }}>
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M15 18l-6-6 6-6" /></svg>
            </button>
          )}
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
            {toolsTab === 'terminal' ? (activeTerminalName ?? 'Терминал') : 'Сервисы'}
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
            service={activePreview}
            projectId={projectId}
            onStop={onStopPreview}
            onClose={onClosePreview}
            services={previewServices}
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

// ── История открытых файлов (back/forward в тулбаре FileViewer) ──
// Запись — полный контекст открытия: путь + якорь/строка для скролла + режим diff/stage,
// чтобы навигация туда-обратно восстанавливала вид, в котором файл открывали.
interface FileHistoryEntry {
  path: string;
  anchor?: string;        // слаг раздела md («foo.md#раздел»)
  line?: number;          // строка кода (из графа / ссылок на строку)
  diffMode?: boolean;     // открытие на вкладке Diff
  gitStagePath?: string | null;  // unstaged-файл для зернистого stage
}

interface FileHistoryState { entries: FileHistoryEntry[]; cursor: number }

type FileHistoryAction =
  | { type: 'push'; entry: FileHistoryEntry }
  | { type: 'back' }
  | { type: 'forward' };

// Дедуп: подряд тот же путь в том же виде — не дублируем (двойной клик по той же ссылке,
// повторное открытие из дерева). Якорь/строка/diff/stage должны совпасть тоже.
function sameHistEntry(a: FileHistoryEntry, b: FileHistoryEntry): boolean {
  return a.path === b.path
    && (a.anchor ?? null) === (b.anchor ?? null)
    && (a.line ?? null) === (b.line ?? null)
    && !!a.diffMode === !!b.diffMode
    && (a.gitStagePath ?? null) === (b.gitStagePath ?? null);
}

function histReducer(s: FileHistoryState, a: FileHistoryAction): FileHistoryState {
  switch (a.type) {
    case 'push': {
      const cur = s.cursor >= 0 ? s.entries[s.cursor] : undefined;
      if (cur && sameHistEntry(cur, a.entry)) return s;
      // Обрезаем «форвард» после курсора — как в браузере: новая навигация
      // аннулирует всё, что было прокручено вперёд кнопкой «назад»
      const trimmed = s.entries.slice(0, s.cursor + 1);
      return { entries: [...trimmed, a.entry], cursor: trimmed.length };
    }
    case 'back':
      return s.cursor > 0 ? { ...s, cursor: s.cursor - 1 } : s;
    case 'forward':
      return s.cursor < s.entries.length - 1 ? { ...s, cursor: s.cursor + 1 } : s;
  }
}

export function WorkspacePage({ project, onGoToProjects, onSwitchHub, auth, onLogout }: Props) {
  // Восстанавливаем состояние окна для этого проекта (компонент перемонтируется при входе в проект)
  const [leftTab, setLeftTab] = useState<LeftTab>(() => {
    const savedRaw = loadWorkspaceState(project.id)?.leftTab;
    // Сохранённое 'agents' — ключ до переименования вкладки персон
    const saved = (savedRaw as string) === 'agents' ? 'personas' : savedRaw;
    if (!isLeftTab(saved)) return 'sessions';
    return saved;
  });
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
  // Файл открыт из git-панели «Изменения» → FileViewer стартует на вкладке Diff
  const [openFileDiffMode, setOpenFileDiffMode] = useState(false);
  // Путь unstaged-файла из git-«Изменений» — включает зернистый stage хунков в diff-вкладке
  const [gitStagePath, setGitStagePath] = useState<string | null>(null);
  // Номер строки для скролла при открытии файла (из графа) — сбрасывается после применения
  const [scrollToLine, setScrollToLine] = useState<number | undefined>(undefined);
  // Слаг раздела для скролла при открытии md по ссылке с якорем («foo.md#раздел»).
  // null — якоря нет; FileViewer сбрасывает потребление через ref, здесь только источник
  const [scrollToAnchor, setScrollToAnchor] = useState<string | null>(null);
  // История открытых файлов для back/forward. cursor = -1 — история пуста (файл ещё не открыт).
  const [hist, histDispatch] = useReducer(histReducer, { entries: [], cursor: -1 });
  const canFileBack = hist.cursor > 0;
  const canFileForward = hist.cursor >= 0 && hist.cursor < hist.entries.length - 1;
  // Коммит открыт из git-панели «История» → просмотр в контентной области
  const [openCommitSha, setOpenCommitSha] = useState<string | null>(null);
  // Файл коммита, на котором сразу открыть diff (клик по файлу в стеке «Изменения»); null — первый
  const [openCommitFile, setOpenCommitFile] = useState<string | null>(null);
  // Документ «Граф зависимостей» открыт в центре — та же модель «документ поверх чата»,
  // что и openFile: крестик возвращает центр к чату, открытие любого другого документа
  // (файл/задача/чат) закрывает граф. Открывается из панели «Граф» в рельсе.
  const [graphOpen, setGraphOpen] = useState(false);
  // Ридер ссылок: живёт как просмотр файла — сплит с чатом либо на всю контентную
  // зону (см. DesktopWorkspace). Один экземпляр состояния на страницу.
  const reader = useReaderPanel();
  // «История решений»: реветь панель по клику на файл в файловом менеджере — той
  // же точкой входа, что «Открыть изменения» у ProjectGitBar и тумблер «Оглавление»
  // у FileViewer (правим раскладку напрямую через стор зон)
  const { reveal: revealPanelKey } = wsPanels.use();
  // Режим просмотра файла — из ГЛОБАЛЬНОГО предпочтения (одно на все проекты), а не
  // из per-project стора: тумблер в шапке файла пишет предпочтение, точки открытия
  // его читают. См. loadFileFullscreenPref в lib/workspaceState.
  const [fileFullscreen, setFileFullscreen] = useState(loadFileFullscreenPref);
  const [workflowRunningFor, setWorkflowRunningFor] = useState<string | null>(null);
  // «Модели и расход» — единый раздел вместо прежних «Использование»/«Поставщики моделей»:
  // из мобильного меню «⋯» проекта и по диплинку «Подробная статистика» бейджа fal.ai
  const [showModelsSpend, setShowModelsSpend] = useState(false);
  useEffect(() => {
    const open = () => setShowModelsSpend(true);
    window.addEventListener('open-fal-stats', open);
    return () => window.removeEventListener('open-fal-stats', open);
  }, []);
  // Диплинк «Собрать цепочку…» из PresetOptions (RoutePicker/PersonaForm) — может
  // сработать в контексте проекта, где HubHeader не смонтирован (см. HubHeader.tsx)
  useEffect(() => subscribeModelProvidersNav(() => setShowModelsSpend(true)), []);
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

  // Плашка «Изменения сохранены» в чате (док-режим) → открыть просмотр коммита хода.
  // Сам обработчик — ниже (после объявления isMobile), эффект-подписка тоже там.

  const [editProjectOpen, setEditProjectOpen] = useState(false);
  const [projectForEdit, setProjectForEdit] = useState(project);
  type ToolsTab = 'terminal' | 'preview';
  const [toolsTab, setToolsTab] = useState<ToolsTab>('terminal');
  const [terminalBusy, setTerminalBusy] = useState(false);

  // Терминалы и сервисы — общие хуки (их же зовёт «Стена»); воркспейсная навигация
  // осталась здесь: старт сервиса переключает вкладку инструментов (onStarted)
  const {
    terminals, activeTerminalId, setActiveTerminalId,
    create: handleCreateTerminal, stop: handleStopTerminal, rename: handleRenameTerminal,
  } = useProjectTerminals(project.id);
  const activeTerminalName = terminals.find(t => t.id === activeTerminalId)?.name;

  const onServiceStarted = useCallback(() => setToolsTab('preview'), []);
  const {
    services: previewServices, activePreviewId, setActivePreviewId, activate: activatePreview,
    refresh: refreshServices, start: startService, stop: stopService,
  } = useProjectServices(project.id, { onStarted: onServiceStarted });

  // Зеркало активной сессии для колбэков эффектов (реконнект SignalR читает свежее значение)
  const activeSessionRef = useRef<Session | null>(null);
  useEffect(() => { activeSessionRef.current = activeSession; }, [activeSession]);

  // Стор персон — чтобы SessionList показал аватар/имя персоны у её сессий,
  // а вкладка «Команда» знала, есть ли персоны у проекта (для пустого стейта)
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Нужно для резолва контекста «в рамках какой задачи» в ArtifactsPanel/бэйджах чата
  // (плашка в ChatOriginBadge полагается на getTaskById из уже загруженного стора)
  useEffect(() => { void ensureTasksLoaded(); }, []);

  // Вкладка «Команда»: список персон — в сайдбаре, форма — в центральной зоне.
  // Состояние выбора поднято сюда, чтобы синхронизировать список ↔ форму.
  const personasMode = leftTab === 'personas';
  const [selectedPersonaId, setSelectedPersonaId] = useState<string | null>(null);
  const [personaCreating, setPersonaCreating] = useState(false);
  // Командный центр в центре нового режима (workspace-cc-panels): открывается
  // кнопкой «Команда» в панельке персон; в старом режиме не используется
  const [teamCenterOpen, setTeamCenterOpen] = useState(false);
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
  const [indexedFileNames, setIndexedFileNames] = useState<Set<string>>(new Set());
  const [knowledgeDocMap, setKnowledgeDocMap] = useState<Map<string, string>>(new Map()); // filename → docId
  const [indexingFiles, setIndexingFiles] = useState<Set<string>>(new Set());
  const [indexingFolders, setIndexingFolders] = useState<Set<string>>(new Set());
  const [skillsData, setSkillsData] = useState<SkillsData | null>(null);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const handleAttachToChat = useCallback((path: string) => {
    setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path]);
  }, []);

  // Ключуем по ПОЛНОМУ пути (doc.name = относительный путь файла): basename давал коллизии
  // одноимённых файлов в разных папках
  const loadKnowledgeStatus = useCallback(() => {
    api.knowledge.getStatus(project.id).then(s => {
      const names = new Set<string>();
      const docMap = new Map<string, string>();
      for (const d of s.documents) {
        names.add(d.name);
        docMap.set(d.name, d.id);
      }
      setIndexedFileNames(names);
      setKnowledgeDocMap(docMap);
    }).catch(() => {});
  }, [project.id]);

  useEffect(() => { loadKnowledgeStatus(); }, [loadKnowledgeStatus]);

  // Синк знаний на бэке (правка/удаление/перенос файла) шлёт knowledge_changed — обновляем пометки
  useEffect(() => onMessage(msg => {
    if (msg.type === 'knowledge_changed') loadKnowledgeStatus();
  }), [loadKnowledgeStatus]);

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

  // из git-панели «История»/«Изменения» → просмотр коммита в контентной области;
  // filePath — открыть diff сразу на этом файле (клик по файлу коммита), иначе первый
  const handleOpenCommit = useCallback((sha: string, filePath?: string) => {
    setOpenFile(null);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setFileFullscreen(false);
    setOpenCommitFile(filePath ?? null);
    setOpenCommitSha(sha);
    setGraphOpen(false);
    if (isMobile) setMobileView('chat');
  }, [isMobile]);

  // Плашка «Изменения сохранены» в чате (док-режим) → открыть просмотр коммита хода
  useEffect(() => {
    const openCommit = (e: Event) => {
      const d = (e as CustomEvent<{ projectId?: string; sha?: string }>).detail;
      if (d?.sha && d.projectId === project.id) handleOpenCommit(d.sha);
    };
    window.addEventListener('cc-open-commit', openCommit);
    return () => window.removeEventListener('cc-open-commit', openCommit);
  }, [project.id, handleOpenCommit]);
  // Выбранный в панельке «Сервисы» сервис — его окно живёт в центре нового режима
  const ccActivePreview = previewServices.find(s => s.id === activePreviewId) ?? null;

  // Задачи (за фич-флагом): вкладка «Задачи» в сайдбаре + карточка задачи в центре.
  // Открытая задача ведёт себя как открытый файл: переключение вкладок сайдбара
  // основную зону не трогает — карточка открывается кликом и закрывается крестиком.
  const allTasks = useTasks();
  // Числа-кружки на кнопках проекта в рельсе (changes/tasks/terminal/preview).
  // Все источники — уже подписанные сторы/стейт; git тянем тем же глобальным стором
  // (ensureGit — идемпотентная первичная загрузка, чтобы кружок был виден до открытия панели).
  const gitState = useGitState(project.id);
  useEffect(() => { ensureGit(project.id); }, [project.id]);
  const railCounts = useMemo(() => ({
    // Число изменённых файлов — дедуп по пути (файл может быть и staged, и unstaged)
    changes: gitState.status
      ? new Set([...gitState.status.staged, ...gitState.status.unstaged, ...gitState.status.untracked].map(f => f.path)).size
      : 0,
    tasks: allTasks.filter(t => t.projectId === project.id && t.status !== 'done').length,
    terminal: terminals.length,
    preview: previewServices.filter(s => s.status === 'started').length,
  }), [gitState.status, allTasks, project.id, terminals, previewServices]);
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
  // Группировка списка задач («Список»/«По дате») и фильтры подняты сюда: вид
  // задач управляет и центральной областью (доска), и переживает пересборку
  // панели при смене раскладки. Контролы рисует сама TasksPanel — в шапке своей
  // карточки (PanelHeaderSlot) либо в теле, если шапки нет.
  const { tab: projectGroupTab, setTab: setProjectGroupTab } = useTaskGroupTab(project.id);
  const { filters: taskListFilters, setFilters: setTaskListFilters } = useTaskFilters(project.id);

  const projectTasks = useMemo(
    () => allTasks.filter(t => t.projectId === project.id),
    [allTasks, project.id],
  );
  const projectBoardById = useMemo(() => new Map([[project.id, project]]), [project]);
  // Кастомные колонки доски проекта (правятся в редакторе, обновляются локально после сохранения)
  const [boardColumns, setBoardColumns] = useState<BoardColumn[] | undefined>(project.boardColumns);
  const [columnsDialog, setColumnsDialog] = useState(false);
  // Проект мог освежиться серверными данными (App refetch) — подхватываем колонки из пропа
  // eslint-disable-next-line react-hooks/set-state-in-effect -- синхронизация колонок доски с данными проекта
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
  const handleSelectTask = (task: Task, autoEdit?: boolean) => {
    setSelectedTaskId(task.id);
    setAutoEditTaskId(autoEdit ? task.id : null);
    // Открытый файл и граф уступают место карточке задачи
    setOpenFile(null);
    setGraphOpen(false);
    if (isMobile) {
      setMobileView('chat');
      navPush({ screen: 'project', project, view: 'chat', file: null, task: task.id });
    } else {
      navPush({ screen: 'project', project, view: mobileView, file: null, task: task.id });
    }
  };

  // «Открыть задачу» из записи истории решений: панель знает только id, карточка
  // задачи хочет объект целиком — ищем в уже загруженном списке задач проекта
  const handleOpenDossierTask = (taskId: string) => {
    const t = allTasks.find(x => x.id === taskId);
    if (t) handleSelectTask(t);
  };

  const ProjectBoardArea = (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {isMobile && (
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', borderBottom: `1px solid ${C.border}`, background: C.bgPanel }}>
          <IconButton size="md" variant="soft" onClick={() => { handleProjectBoard(false); if (isMobile) setMobileView('sidebar'); }} title="К списку задач">
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

  // Переход из карточки задачи в связанный диалог — объявлен ниже, после handleSelectSession

  const leftTabOptions: { value: LeftTab; label: string; icon?: ReactNode }[] = [
    { value: 'sessions', label: 'Чаты', icon: LEFT_TAB_ICONS.sessions },
    { value: 'files', label: 'Файлы', icon: LEFT_TAB_ICONS.files },
    // Порядок тот же, что у панелей в рельсе (PANEL_KEYS): файлы → их изменения →
    // задачи по ним, дальше справочное. На десктопе это панели, здесь рельсы нет —
    // иначе git и знания с телефона недоступны совсем
    { value: 'changes' as LeftTab, label: 'Изменения', icon: LEFT_TAB_ICONS.changes },
    { value: 'tasks', label: 'Задачи', icon: LEFT_TAB_ICONS.tasks },
    { value: 'knowledge' as LeftTab, label: 'Знания', icon: LEFT_TAB_ICONS.knowledge },
    { value: 'personas' as LeftTab, label: 'Команда', icon: LEFT_TAB_ICONS.personas },
    // На десктопе навыки живут панелью в рельсе; на мобиле рельсы панелей проекта нет,
    // поэтому им нужна своя вкладка — иначе доступ к ним с телефона пропадает совсем
    { value: 'skills' as LeftTab, label: 'Навыки', icon: LEFT_TAB_ICONS.skills },
    // На мобиле рельсы панелей проекта нет, и ящик рельсы не работает — поэтому
    // Терминал/Сервисы доступны только через эту вкладку (на десктопе они панелями)
    { value: 'tools' as LeftTab, label: 'Инструменты', icon: LEFT_TAB_ICONS.tools },
  ];

  // Мобильный таббар проекта: показываем столько вкладок, сколько влезает по ширине
  // шапки, остальное + «Использование» — в «⋯» (как «⋯ Разделы» в HubHeader). Количество
  // определяем динамически: скрытый эталон compact-пилюль (projectTabsProbeRef) мерим
  // относительно шапки (projectTabsHeaderRef), резервируя место под кнопку проекта и «⋯».
  const projectTabsHeaderRef = useRef<HTMLDivElement>(null);
  const projectTabsProbeRef = useRef<HTMLDivElement>(null);
  const projectTabsMoreRef = useRef<HTMLDivElement>(null);
  const [projectVisibleCount, setProjectVisibleCount] = useState(leftTabOptions.length);

  useLayoutEffect(() => {
    if (!isMobile) return;
    const header = projectTabsHeaderRef.current;
    const probe = projectTabsProbeRef.current;
    if (!header || !probe) return;
    const compute = () => {
      const pills = Array.from(probe.children) as HTMLElement[];
      if (!pills.length) return;
      const cs = getComputedStyle(header);
      const avail = header.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
      const moreW = projectTabsMoreRef.current?.offsetWidth ?? 40;
      // 64 — минимум под кнопку проекта (имя с эллипсисом), 14 — зазоры back|pills|⋯
      const budget = avail - 64 - moreW - 14;
      let used = 6; // внутренние отступы трека PillSwitch (padding 3×2)
      let fit = 0;
      for (let i = 0; i < pills.length; i++) {
        const w = pills[i].offsetWidth + (i > 0 ? 3 : 0);
        if (used + w <= budget) { used += w; fit++; } else break;
      }
      setProjectVisibleCount(Math.max(1, Math.min(pills.length, fit)));
    };
    compute();
    const ro = new ResizeObserver(compute);
    ro.observe(header);
    return () => ro.disconnect();
  }, [isMobile, leftTabOptions.length, leftTab]);

  // Видимые вкладки — первые projectVisibleCount; активную спрятанную подставляем
  // последней, чтобы подсветка была верной. Остальные + «Модели и расход» — в «⋯».
  const activeLeftIdx = leftTabOptions.findIndex(o => o.value === leftTab);
  const mobileLeftTabOptions = activeLeftIdx >= projectVisibleCount
    ? [...leftTabOptions.slice(0, Math.max(0, projectVisibleCount - 1)), leftTabOptions[activeLeftIdx]]
    : leftTabOptions.slice(0, projectVisibleCount);
  const mobileVisibleValues = new Set(mobileLeftTabOptions.map(o => o.value));
  const projectOverflowItems: OverflowItem[] = [
    ...leftTabOptions
      .filter(o => !mobileVisibleValues.has(o.value))
      .map(o => ({ key: o.value, icon: o.icon, label: o.label, onClick: () => handleTabSwitch(o.value) })),
    {
      key: 'graph', label: 'Граф',
      icon: <Network size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />,
      onClick: () => ensureGraphOpen(),
    },
    {
      key: 'models-spend', label: 'Модели и расход',
      icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2" /></svg>,
      onClick: () => setShowModelsSpend(true),
    },
  ];

  // «Поговорить» из проектной вкладки «Команда»: сессия персоны создаётся в этом
  // проекте — открываем её на месте (переключаемся на «Чаты» и выбираем).
  // Обработчик объявлен ниже, после handleSelectSession.

  // Диплинк файла: App положил «projectId|путь» в sessionStorage.
  // Значение чужого проекта не трогаем — его заберёт WorkspacePage нужного проекта.
  useEffect(() => {
    const raw = sessionStorage.getItem('cc_pending_file');
    if (!raw) return;
    const sep = raw.indexOf('|');
    const [pid, path] = sep === -1 ? [project.id, raw] : [raw.slice(0, sep), raw.slice(sep + 1)];
    if (pid !== project.id) return;
    sessionStorage.removeItem('cc_pending_file');
    // eslint-disable-next-line react-hooks/set-state-in-effect -- одноразовое потребление диплинка из sessionStorage
    setOpenFile(path);
    setFileFullscreen(loadFileFullscreenPref());
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

  // Диплинк на командный центр проекта (фича default-personas-onboarding, п.5.3):
  // «Назначить руководителя» в настройках проекта кладёт id проекта в sessionStorage —
  // тот же приём, что и pending-персона выше. teamCenterOpen — флаг центрального
  // оверлея в новом режиме панелей; leftTab='personas' без выбранной персоны даёт тот
  // же экран в одноколоночном режиме (mobile/tablet).
  useEffect(() => {
    const consumePendingTeam = () => {
      const pid = sessionStorage.getItem('cc_pending_team_center');
      if (pid !== project.id) return;
      sessionStorage.removeItem('cc_pending_team_center');
      setSelectedPersonaId(null);
      setPersonaCreating(false);
      setLeftTab('personas');
      setTeamCenterOpen(true);
      if (isMobile) setMobileView('chat');
    };
    consumePendingTeam();
    window.addEventListener('cc-pending-team-center', consumePendingTeam);
    return () => window.removeEventListener('cc-pending-team-center', consumePendingTeam);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  // Диплинк проектного чата (#/project/{id}/chat/{chatId}) из уведомления проактивности.
  // Эффект-подписка объявлена ниже, после handleSelectSession.
  // Сайдбар, его ширина и сплиттеры жили здесь, пока десктоп рисовался этим
  // компонентом. Теперь десктоп и планшет — DesktopWorkspace с рельсами панелей
  // (ширина и сворачивание в состоянии зон), а мобильная ветка сайдбара не имеет.

  // Документ «Граф»: крестик → центр к чату
  const handleGraphClose = useCallback(() => {
    setGraphOpen(false);
    if (isMobile) setMobileView('sidebar');
  }, [isMobile]);

  // Открыть документ «Граф» в центре (из панели «Граф»: смена режима при закрытом
  // документе открывает его снова). Как и при открытии файла/задачи — закрываем
  // остальные документы центра, граф остаётся единственным поверх чата.
  const ensureGraphOpen = useCallback(() => {
    setGraphOpen(true);
    setOpenFile(null);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setOpenCommitSha(null);
    setOpenCommitFile(null);
    setSelectedTaskId(null);
    setActivePreviewId(null);
    if (isMobile) setMobileView('chat');
  }, [isMobile, setActivePreviewId]);

  // «Построить граф» (empty-state документа и панели): явный POST-build на бэке,
  // стор сам переходит в 'building' и дожидается готовности polling'ом.
  const handleGraphBuild = useCallback(() => {
    void buildCodeGraph(project.id);
  }, [project.id]);

  // Панели сессии для МОБИЛЬНОЙ ветки (десктоп собирает их в DesktopWorkspace).
  // Раньше их строила правая зона внутри себя — теперь контент приходит снаружи.
  const mobileSessionPanels = useSessionPanels(activeSession, project.id, project.rootPath);

  const handleSelectSession = (session: Session, firstMessage?: string, autoSelect?: boolean) => {
    setActiveSession(session);
    setPendingMessage(firstMessage);
    // Открытый чат — прочитанный. Отмечаем и при autoSelect (восстановление чата
    // после перезагрузки): он тоже показан на экране. Без этого в проектных
    // списках метка непрочитанности не гасла никогда — markChatRead звался
    // только в глобальном ChatsPage
    markChatRead(session.id);
    if (!autoSelect) {
      // Файл, открытый В СПЛИТЕ, смену чата переживает: там чат и файл стоят рядом
      // двумя островами и друг другу не мешают — закрывать нечего. Так же ведёт себя
      // сосед по месту в центре, ридер (его этот обработчик не трогает вовсе).
      // Полноэкранный файл — наоборот, чат собой закрывает, и выбор чата его убирает;
      // на мобиле и планшете сплита нет вовсе (см. DesktopWorkspace), значит и там
      // файл уходит.
      const keepSplitFile = !isMobile && !isTablet && !!openFile && !fileFullscreen;
      // явный выбор — закрываем файл (кроме сплита), просмотр коммита, открытую задачу
      // и граф, показываем чат во весь экран
      if (!keepSplitFile) {
        setOpenFile(null);
        setOpenFileDiffMode(false);
      }
      setOpenCommitSha(null);
      setOpenCommitFile(null);
      setSelectedTaskId(null);
      setGraphOpen(false);
      // Пишем запись истории с chatId — для URL #/project/{id}/chat/{chatId}
      // и кнопки «назад/вперёд» браузера. Оставленный в сплите файл несём в том же
      // снимке: popstate восстанавливает file и chatId независимо, иначе «назад»
      // вернул бы чат без файла, который с экрана не уходил.
      const file = keepSplitFile ? openFile : null;
      if (isMobile) {
        setMobileView('chat');
        navPush({ screen: 'project', project, view: 'chat', file, chatId: session.id });
      } else {
        navPush({ screen: 'project', project, view: 'sidebar', file, chatId: session.id });
      }
    }
  };

  const handleSessionUpdated = (updated: Session) => {
    setActiveSession(prev => (prev?.id === updated.id ? updated : prev));
  };

  // Дефолт-персона проекта (руководитель, фича default-personas-onboarding): больше не
  // гейтует рабочее пространство (знакомство — приглашение из «Персон»/настроек проекта,
  // волна 5), но актуальное значение нужно колбэкам создания чата. Свежий дефолт проверяем
  // с сервера: project из localStorage может не знать о поле defaultPersonaId.
  const onboardingOn = useFeature(FLAGS.defaultPersonasOnboarding);
  const [projectDefaultId, setProjectDefaultId] = useState<string | null | undefined>(project.defaultPersonaId);
  // Актуальное значение для колбэков создания чата: у них deps осознанно сужены,
  // и без ref они держат дефолт на момент монтирования
  const projectDefaultIdRef = useRef(projectDefaultId);
  projectDefaultIdRef.current = projectDefaultId;
  useEffect(() => {
    if (project.defaultPersonaId !== undefined) setProjectDefaultId(project.defaultPersonaId);
  }, [project.defaultPersonaId]);
  useEffect(() => {
    if (!onboardingOn) return;
    let cancelled = false;
    api.projects.list()
      .then(list => {
        if (cancelled) return;
        const fresh = list.find(p => p.id === project.id);
        if (!fresh) return;
        setProjectDefaultId(fresh.defaultPersonaId ?? null);
      })
      .catch(() => { /* офлайн — работаем с тем, что знаем */ });
    return () => { cancelled = true; };
  }, [onboardingOn, project.id]);

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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- handleSelectSession немемоизирован: включение переустанавливало бы подписку каждый рендер; функция свежая на момент события (эффект после её объявления)
  }, [project.id]);

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

  // «Поговорить» из проектной вкладки «Команда»: сессия персоны создаётся в этом
  // проекте — открываем её на месте (переключаемся на «Чаты» и выбираем).
  const handleOpenPersonaChat = (session: Session) => {
    setLeftTab('sessions');
    handleSelectSession(session);
  };

  // Пока чат открыт, приходящие в него сообщения не копят непрочитанность —
  // следим за updatedAt, а не только за смену чата (приём из ChatsPage)
  const activeSessionId = activeSession?.id;
  const activeSessionUpdatedAt = activeSession?.updatedAt;
  useEffect(() => {
    if (activeSessionId) markChatRead(activeSessionId);
  }, [activeSessionId, activeSessionUpdatedAt]);

  // activeSession.updatedAt при ходе агента НЕ обновляется (status_changed несёт
  // только status, user_message/exited не трогают activeSession вовсе) — поэтому
  // эффект выше не срабатывает на новом ходе, и карточка помечалась непрочитанной,
  // хотя юзер прямо в этом чате. Гасим напрямую: любое событие хода в активном
  // чате → он прочитан. Переподписка при смене чата — нормально
  useEffect(() => {
    if (!activeSessionId) return;
    return onMessage(msg => {
      if (msg.sessionId !== activeSessionId) return;
      if (msg.type === 'user_message' || msg.type === 'exited' || msg.type === 'status_changed') {
        markChatRead(activeSessionId);
      }
    });
  }, [activeSessionId]);

  // Создание чата только по клику (кнопка в центре пустого состояния и «Новый чат»
  // в сайдбаре) — авто-создание при заходе убрано. Открываем созданный чат сразу;
  // SessionList подхватит его в список через activeSession.
  const [creatingSession, setCreatingSession] = useState(false);
  const handleCreateSession = useCallback(async () => {
    if (creatingSession) return;
    setCreatingSession(true);
    try {
      // Под флагом default-personas-onboarding — от лица дефолт-персоны проекта
      const s = await createChatWithContextPersona(
        { id: project.id, defaultPersonaId: projectDefaultIdRef.current ?? null }, { mode: 'auto' });
      handleSelectSession(s);
    } catch (e) {
      showToast('Чат', e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setCreatingSession(false);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id, creatingSession]);

  // Делегировать фиксацию изменений чату: в текущем чате или в новом (панель «Изменения»)
  const handleCommitVia = useCallback((where: 'chat' | 'newChat') => {
    const msg = 'Зафиксируй (сделай git commit) текущие изменения в проекте. Сам придумай осмысленное сообщение коммита по сути изменений.';
    void (async () => {
      try {
        if (where === 'chat' && activeSession) { handleSelectSession(activeSession, msg); return; }
        // Под флагом default-personas-onboarding — от лица дефолт-персоны проекта
        const s = await createChatWithContextPersona(
          { id: project.id, defaultPersonaId: projectDefaultIdRef.current ?? null }, { mode: 'auto' });
        handleSelectSession(s, msg);
      } catch (e) {
        showToast('Чат', e instanceof Error ? e.message : 'Не удалось открыть чат');
      }
    })();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id, activeSession]);

  // Список чатов опустел (удалён последний) — сбрасываем активную сессию в пустое состояние
  const handleClearSession = useCallback(() => {
    setActiveSession(null);
    setPendingMessage(undefined);
  }, []);

  // Возобновление прерванного (orphaned) чата живёт внутри ChatPanel: обычный ход
  // «Продолжи» в ту же сессию (--resume на бэкенде). Прежний путь «создать новый чат
  // с resumeSessionId + удалить старый» терял имя, связь с задачей-родителем и стирал
  // history.json общего ClaudeSessionId — чат выглядел только что созданным.

  // Запоминаем состояние окна (активный чат/файл, панели) для проекта
  useEffect(() => {
    saveWorkspaceState(project.id, { activeSession, openFile, leftTab });
  }, [project.id, activeSession, openFile, leftTab]);

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
      if (msg.type === 'status_changed') {
        setActiveSession(prev =>
          prev?.id === msg.sessionId
            ? { ...prev, status: msg.status as Session['status'] }
            : prev
        );
      }
      // Имя (и значок) чата уточнила модель — авто-заголовок нового чата или
      // «Обновить название» из AI-хаба. Обновляем открытый чат на лету: иначе
      // activeSession держит старое имя до переключения чата, и шапка врёт
      if (msg.type === 'chat_renamed') {
        setActiveSession(prev =>
          prev?.id === msg.sessionId
            ? { ...prev, name: msg.name, topic: msg.topic ?? prev.topic }
            : prev
        );
      }
      // Статусы и сообщения проектных чатов не доходят до агрегата точек (он в
      // user-группе, те — в session/project). Мы в project-группе и видим события —
      // пнём точку, иначе она догонит только поллингом через 15с. status_changed —
      // смена состояния (waiting/working/...), user_message/exited — новый ход, а
      // значит возможный unread у непрочитанного чата проекта
      if (msg.type === 'status_changed' || msg.type === 'user_message' || msg.type === 'exited') {
        refreshProjectActivity();
      }
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- WorkspacePage перемонтируется на смену проекта (key={project.id} в App), подписка на весь жизненный цикл инстанса
  }, []);

  // Командный центр активен → фиксируем в истории (persona: null), чтобы «назад» из
  // любого диплинка (задача/чат/персона) возвращал именно в командный центр
  useEffect(() => {
    if (leftTab === 'personas' && !selectedPersonaId && !personaCreating) {
      navReplace({ screen: 'project', project, view: isMobile ? mobileView : 'sidebar', file: null, task: null, persona: null });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- срабатывание только на ВХОД в командный центр; зависимости от project/mobileView плодили бы лишние записи истории при каждом переключении вида
  }, [leftTab, selectedPersonaId, personaCreating]);

  // из дерева файлов → режим по глобальному предпочтению; опциональная строка для скролла
  const handleOpenFileFromTree = (filePath: string, line?: number) => {
    reader.actions.closeReader();
    setOpenCommitSha(null);
    setOpenFile(filePath);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setFileFullscreen(loadFileFullscreenPref());
    setGraphOpen(false);
    setScrollToLine(line);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
    histDispatch({ type: 'push', entry: { path: filePath, line } });
  };

  // «История решений» из файлового менеджера: открываем файл (панель фильтрует
  // список по activeFilePath) и являем панель в её домашней зоне
  const handleOpenDossiers = (filePath: string) => {
    handleOpenFileFromTree(filePath);
    revealPanelKey('dossiers');
  };

  // Переход по md-ссылке из центрального FileViewer (клик по ссылке в открытом md).
  // В отличие от дерева, НЕ переключает режим просмотра (сплит/полный экран остаётся
  // прежним) — читатель остаётся в том же виде, где был. anchor — слаг раздела для скролла.
  const handleOpenDocLink = (filePath: string, anchor?: string) => {
    reader.actions.closeReader();
    setOpenCommitSha(null);
    setOpenFile(filePath);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setGraphOpen(false);
    setScrollToLine(undefined);
    setScrollToAnchor(anchor ?? null);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
    histDispatch({ type: 'push', entry: { path: filePath, anchor } });
  };

  // из чата → на десктопе режим по глобальному предпочтению; на планшете/мобайле
  // сплита нет — всегда fullscreen
  const handleOpenFileFromChat = (filePath: string) => {
    reader.actions.closeReader();
    setOpenCommitSha(null);
    setOpenFile(filePath);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setFileFullscreen(isMobile || isTablet ? true : loadFileFullscreenPref());
    setGraphOpen(false);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
    histDispatch({ type: 'push', entry: { path: filePath } });
  };

  // из git-панели «Изменения» → тот же FileViewer, но сразу на вкладке Diff;
  // для unstaged-диффа включаем зернистый stage хунков/строк
  const handleOpenGitDiff = (filePath: string, staged?: boolean) => {
    reader.actions.closeReader();
    setOpenCommitSha(null);
    setOpenFile(filePath);
    setOpenFileDiffMode(true);
    setGitStagePath(staged ? null : filePath);
    setFileFullscreen(true);
    setGraphOpen(false);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
    histDispatch({ type: 'push', entry: { path: filePath, diffMode: true, gitStagePath: staged ? null : filePath } });
  };

  // Back/Forward по истории открытых файлов. Восстанавливают контекст записи
  // (путь + режим diff/stage + скролл), НЕ трогая режима просмотра (split/fullscreen),
  // граф и читалку — это переключение файла, а не новое открытие. push при этом не идёт,
  // иначе каждая навигация плодила бы копии и кнопка «вперёд» обрезалась бы.
  const applyHistEntry = (e: FileHistoryEntry) => {
    setOpenFile(e.path);
    setOpenFileDiffMode(!!e.diffMode);
    setGitStagePath(e.gitStagePath ?? null);
    setScrollToLine(e.line);
    setScrollToAnchor(e.anchor ?? null);
    navPush({ screen: 'project', project, view: mobileView, file: e.path });
  };
  const handleFileBack = () => {
    if (hist.cursor <= 0) return;
    const entry = hist.entries[hist.cursor - 1];
    histDispatch({ type: 'back' });
    applyHistEntry(entry);
  };
  const handleFileForward = () => {
    if (hist.cursor < 0 || hist.cursor >= hist.entries.length - 1) return;
    const entry = hist.entries[hist.cursor + 1];
    histDispatch({ type: 'forward' });
    applyHistEntry(entry);
  };

  // Открыть URL в ридере (кнопка-компаньон у внешней ссылки в чате) — как открытие
  // файла: вытесняет открытый файл, ридер занимает то же место в центре
  const handleOpenReader = (url: string) => {
    setOpenFile(null);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setFileFullscreen(false);
    reader.actions.openUrl(url);
  };

  // из git-панели «История»/«Изменения» → просмотр коммита в контентной области —
  // сам обработчик объявлен выше (useCallback handleOpenCommit), здесь только закрытие
  const closeCommitView = () => {
    setOpenCommitSha(null);
    if (isMobile) setMobileView('sidebar');
  };

  // Смена git-скоупа/коммита в панели «Изменения» — центральную область сбрасываем
  // к чату: убираем открытый файл, просмотр коммита и открытую задачу (если что-то показано)
  const clearCenterToChat = () => {
    if (!openFile && !openCommitSha && !selectedTaskId) return;  // уже чат — ничего не делаем
    setOpenFile(null);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setFileFullscreen(false);
    setOpenCommitSha(null);
    setOpenCommitFile(null);
    setSelectedTaskId(null);
    if (isMobile) setMobileView('chat');
    navReplace({ screen: 'project', project, view: isMobile ? 'chat' : 'sidebar', file: null, task: null });
  };

  // Тулбар-«назад»: ДЕТЕРМИНИРОВАННЫЙ подъём к списку текущей вкладки внутри проекта.
  // НЕ history.back — при открытии приложения сразу на глубоком месте (восстановление/диплинк)
  // история браузера пуста, и history.back() ничего не делает. Всегда ведём на уровень выше.
  const backFromFile = () => {
    setOpenFile(null);
    setOpenFileDiffMode(false);
    setGitStagePath(null);
    setFileFullscreen(false);
    if (isMobile) setMobileView('sidebar');
    navReplace({ screen: 'project', project, view: 'sidebar', file: null, task: selectedTaskId ?? null });
  };
  const backFromTask = () => {
    setSelectedTaskId(null);
    if (isMobile) setMobileView('sidebar');
    navReplace({ screen: 'project', project, view: 'sidebar', file: null, task: null });
  };
  const backFromChat = () => {
    if (isMobile) setMobileView('sidebar');
    navReplace({ screen: 'project', project, view: 'sidebar', file: null, task: null });
  };
  // Инструменты (терминал/preview): выбор в сайдбаре → на мобиле уходим в контент;
  // «назад» в шапке контента вернёт к сайдбару инструментов (двухуровневая навигация).
  const handleSelectTerminal = (id: string | null) => { setActiveTerminalId(id); if (isMobile && id) setMobileView('chat'); };
  const handleSelectPreview = async (id: string | null) => {
    setToolsTab('preview');
    // activate сам назначает сервис активным на бэкенде и лишь потом открывает окно
    await activatePreview(id);
    if (isMobile && id) setMobileView('chat');
  };

  // Тумблер в шапке файла — теперь это выбор ГЛОБАЛЬНОГО предпочтения: меняем не только
  // текущий вид, но и запоминаем его для всех последующих открытий (см. saveFileFullscreenPref)
  const handleToggleFileFullscreen = () => setFileFullscreen(v => {
    const next = !v;
    saveFileFullscreenPref(next);
    return next;
  });

  // Пропорция split-режима «чат | файл» и её сплиттер живут в DesktopWorkspace —
  // мобильная ветка split не показывает

  const handleTabSwitch = (tab: LeftTab) => {
    setLeftTab(tab);
    if (isMobile) setMobileView('sidebar');
    // Синхронизируем историю с активной вкладкой. Уходя с «Команды», убираем persona из
    // записи — иначе «назад» (например из открытого файла) по устаревшей записи с
    // persona:null восстановит командный центр вместо возврата к списку вкладки.
    if (tab !== 'personas') {
      navReplace({ screen: 'project', project, view: 'sidebar', file: openFile ?? null, task: null });
    }
  };

  const handleAddToKnowledge = useCallback(async (relativePath: string) => {
    setIndexingFiles(prev => new Set([...prev, relativePath]));
    try {
      const result = await api.knowledge.indexFile(project.id, relativePath);
      setIndexedFileNames(prev => new Set([...prev, result.document.name]));
      setKnowledgeDocMap(prev => new Map(prev).set(result.document.name, result.document.id));
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
        for (const doc of result.documents) next.add((doc as { name: string }).name);
        return next;
      });
      setKnowledgeDocMap(prev => {
        const next = new Map(prev);
        for (const doc of result.documents) {
          const d = doc as { id: string; name: string };
          next.set(d.name, d.id);
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
    const docId = knowledgeDocMap.get(relativePath);
    if (!docId) return;
    try {
      await api.knowledge.deleteDocument(project.id, docId);
      setIndexedFileNames(prev => { const n = new Set(prev); n.delete(relativePath); return n; });
      setKnowledgeDocMap(prev => { const n = new Map(prev); n.delete(relativePath); return n; });
    } catch {
      // игнорируем
    }
  }, [project.id, knowledgeDocMap]);

  // Пропсы для вынесенного ToolsPaneView (стабильный module-level компонент)
  const toolsPaneProps = {
    projectId: project.id, toolsTab, terminals, activeTerminalId, activeTerminalName, terminalBusy,
    onTerminalActivity: setTerminalBusy, previewServices, activePreviewId,
    onStopPreview: stopService,
    onClosePreview: () => setActivePreviewId(null),
  };


  // Пустое состояние центра (нет активного чата) — единый вид для мобилки и десктопа.
  // Создание чата только по клику: авто-создание при заходе убрано.
  const NoSession = (
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
          variant="primary" size="md" glow loading={creatingSession}
          onClick={handleCreateSession} style={{ marginTop: 10 }}
          leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}
        >
          Новый чат
        </Button>
      </div>
    </div>
  );

  if (isMobile) {
    return (
      // Дудл-фон и на мобиле: виден под лентой чата и в пустых состояниях.
      // Высота — измеренная viewportH, а не 100dvh: см. комментарий при viewportH
      <PageCanvas project={project} style={{ height: viewportH }}>
        {/* Верхняя шапка — только в режиме списка (sidebar). В режиме чата своя
            самодостаточная шапка ChatHeaderBar с кнопкой «назад»; у файла — шапка FileViewer */}
        {!openFile && mobileView === 'sidebar' && (
          <div ref={projectTabsHeaderRef} style={{ position: 'relative', padding: '10px 14px', borderBottom: `1px solid ${C.border}`, background: C.bgPanel, display: 'flex', alignItems: 'center', gap: 10, flexShrink: 0 }}>
            {/* Скрытый эталон: compact-пилюли всех вкладок — по ним меряем, сколько влезает */}
            <div ref={projectTabsProbeRef} aria-hidden style={{ position: 'absolute', visibility: 'hidden', pointerEvents: 'none', top: 0, left: 0, display: 'flex', gap: 3, whiteSpace: 'nowrap' }}>
              {leftTabOptions.map((opt, i) => (
                <span key={i} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, boxSizing: 'border-box', minHeight: 40, padding: opt.value === leftTab ? '0 12px' : '0 11px', fontSize: 12, fontWeight: 600 }}>
                  {opt.value === leftTab ? opt.label : opt.icon}
                </span>
              ))}
            </div>
            <BackButton onClick={onGoToProjects} title={project.name} style={{ flex: 1, minHeight: 40 }}>
              <span style={{ fontWeight: 700, fontSize: 15, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{project.name}</span>
            </BackButton>
            <PillSwitch<LeftTab>
              value={leftTab}
              options={mobileLeftTabOptions}
              onChange={handleTabSwitch}
              isMobile
              compact
            />
            {/* Не поместившиеся вкладки + «Использование» — в «⋯» (как «⋯ Разделы» в HubHeader) */}
            <div ref={projectTabsMoreRef} style={{ flexShrink: 0, display: 'inline-flex' }}>
              <ToolbarOverflowMenu isMobile title="Ещё" items={projectOverflowItems} />
            </div>
          </div>
        )}
        {/* Sidebar — ВСЕГДА в DOM: FileExplorer не теряет текущий путь при смене вида */}
        <div style={{ flex: 1, display: !openFile && mobileView === 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
            {leftTab === 'sessions'
              ? <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} onCleared={handleClearSession} isMobile={isMobile} workflowRunningFor={workflowRunningFor ?? undefined} />
              : leftTab === 'changes'
              // onScopeChange не передаём: в одноколоночной раскладке он уводил бы
              // экран в чат на каждую смену скоупа
              ? <GitChangesRail project={project} onOpenDiff={handleOpenGitDiff} onOpenFile={handleOpenFileFromTree} onOpenCommit={handleOpenCommit} activeFilePath={openFile ?? openCommitFile} activeCommitSha={openCommitSha} onCommit={handleCommitVia} />
              : leftTab === 'tasks'
              ? <TasksPanel project={project} selectedTaskId={selectedTaskId} onSelect={handleSelectTask} isMobile={isMobile} boardMode={projectBoard} onBoardMode={handleProjectBoard} onEditColumns={openColumnsEditor} groupTab={projectGroupTab} onGroupTab={setProjectGroupTab} filters={taskListFilters} onFilters={setTaskListFilters} />
              : leftTab === 'personas'
              ? <ProjectPersonasPanel project={project} selectedId={personaCreating ? null : selectedPersonaId} onSelect={handlePersonaSelect} onNew={handlePersonaNew} onShowTeam={handleShowTeam} teamActive={!selectedPersonaId && !personaCreating} />
              : leftTab === 'skills'
              ? <SkillsPanel projectId={project.id} onChanged={setSkillsData} />
              : leftTab === 'tools'
              ? <ToolsSidebar projectId={project.id} activeTab={toolsTab} onTabChange={setToolsTab}
                  terminals={terminals} onCreateTerminal={handleCreateTerminal}
                  onStopTerminal={handleStopTerminal} onRenameTerminal={handleRenameTerminal}
                  activeTerminalId={activeTerminalId} onSelectTerminal={handleSelectTerminal}
                  activePreviewId={activePreviewId} previewServices={previewServices}
                  onRefreshServices={refreshServices} onStartService={startService}
                  onStopService={stopService} onSelectPreview={handleSelectPreview}
                  terminalBusy={terminalBusy} />
              : leftTab === 'knowledge'
              ? <KnowledgePanel project={project} isMobile={isMobile} alwaysShowIcons={isTablet} />
              : (
                <div style={{ flex: 1, overflow: 'hidden' }}>
                  {/* onOpenDossiers не передаём: «История решений» на телефоне недоступна
                      совсем (как «Документация») — панелей рельсы здесь нет, пункт
                      меню вёл бы в никуда */}
                  <FileExplorer project={project} activeFilePath={openFile} isMobile={isMobile} alwaysShowIcons={isTablet} onOpenFile={handleOpenFileFromTree} onAddToKnowledge={handleAddToKnowledge} onAddFolderToKnowledge={handleAddFolderToKnowledge} onRemoveFromKnowledge={handleRemoveFromKnowledge} indexedFileNames={indexedFileNames} indexingFiles={indexingFiles} indexingFolders={indexingFolders} onAttachToChat={activeSession && !fileFullscreen ? handleAttachToChat : undefined} />
                </div>
              )
            }
          </div>
        </div>
        {/* Мягкое приглашение к знакомству с проектом (фича default-personas-onboarding).
            На мобиле появляется только в режиме sidebar (как headerBar выше); не гейт —
            workspace остаётся полностью доступным. */}
        {!openFile && mobileView === 'sidebar' && (
          <ProjectIntroCard
            projectId={project.id}
            projectOwnerId={project.ownerId}
            defaultPersonaId={projectDefaultId}
            isMobile={isMobile}
          />
        )}
        {/* Чат (или карточка задачи в режиме «Задачи») — ВСЕГДА в DOM */}
        <div style={{ flex: 1, display: !openFile && mobileView !== 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          {leftTab === 'tools'
            ? <ToolsPaneView {...toolsPaneProps} onBack={() => setMobileView('sidebar')} />
            : personasMode
            ? ((selectedPersonaId || personaCreating)
                ? <ProjectPersonaPane project={project} personaId={personaCreating ? null : selectedPersonaId} creating={personaCreating} initialView={pendingPersonaView} onOpenChat={handleOpenPersonaChat} onSelectPersona={handlePersonaSelectAfterCreate} onCleared={handlePersonaCleared} onBack={handlePersonaCleared} />
                : <TeamCommandCenter project={project} onOpenPersona={handlePersonaSelect} onNewPersona={handlePersonaNew} onOpenSession={handleOpenPersonaChat} onOpenSessionById={handleOpenTaskSession} />)
            : tasksMode
            ? (selectedTask
                ? <TaskDetailsPane key={selectedTask.id} task={selectedTask} project={project} isMobile startInEdit={selectedTask.id === autoEditTaskId} onBack={backFromTask} onOpenSession={handleOpenTaskSession} onOpenFile={handleOpenFileFromTree} onDeleted={backFromTask} />
                : showProjectBoard
                ? ProjectBoardArea
                : <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontSize: 14 }}>Выберите задачу</div>)
            : activeSession
            ? (
              // Чат + сессионная рельса в одной строке (пейн колоночный — нужна row-обёртка)
              <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
                  <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onBack={backFromChat} onWorkflowRunning={handleWorkflowRunning} skills={composerSkills} agents={skillsData?.agents} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} />
                </div>
                <PanelZone side="right" allowedKeys={SESSION_KEYS} hideWhenEmpty compact panels={{}} sessionPanels={mobileSessionPanels} />
              </div>
            )
            : NoSession
          }
        </div>
        {/* Просмотр файла — FileViewer имеет свою шапку */}
        {openFile && (
          <div style={{ flex: 1, overflow: 'hidden' }}>
            <FileViewer project={project} filePath={openFile} isMobile onClose={backFromFile} initialTab={openFileDiffMode ? 'diff' : undefined} gitStagePath={gitStagePath ?? undefined} scrollToLine={scrollToLine} onOpenFile={handleOpenDocLink} scrollToAnchor={scrollToAnchor} onFileBack={handleFileBack} onFileForward={handleFileForward} canFileBack={canFileBack} canFileForward={canFileForward} />
          </div>
        )}
        {/* Просмотр коммита из git-«Истории» */}
        {!openFile && openCommitSha && mobileView === 'chat' && (
          <div style={{ position: 'absolute', inset: 0, zIndex: 800, display: 'flex' }}>
            <GitCommitView project={project} sha={openCommitSha} initialPath={openCommitFile} onClose={closeCommitView} isMobile />
          </div>
        )}
        {/* Документ «Граф зависимостей» — на весь экран, как открытый файл/коммит */}
        {!openFile && graphOpen && (
          <div style={{ position: 'absolute', inset: 0, zIndex: 800, display: 'flex', background: C.bgMain }}>
            <CodeGraphDocument projectId={project.id} isMobile onClose={handleGraphClose} onOpenFile={handleOpenFileFromTree} onBuild={handleGraphBuild} />
          </div>
        )}
        {columnsDialogEl}
        {showModelsSpend && <ModelsSpendModal onClose={() => setShowModelsSpend(false)} />}
        {editProjectOpen && (
          <EditDialog
            project={projectForEdit}
            onSuccess={updated => { setProjectForEdit(updated); setEditProjectOpen(false); }}
            onIconUpdated={setProjectForEdit}
            onProjectUpdated={setProjectForEdit}
            onClose={() => setEditProjectOpen(false)}
          />
        )}
      </PageCanvas>
    );
  }

  return (
    <PageCanvas project={project}>
      {/* Единый верхний хаб-хедер на всю ширину (симметрия с разделом «Чаты») */}
      <HubHeader value="projects" onTab={onSwitchHub} auth={auth} onLogout={onLogout} project={projectForEdit} onOpenProjectSettings={() => setEditProjectOpen(true)} />

      {/* Мягкое приглашение к знакомству с проектом (фича default-personas-onboarding).
          Появляется между HubHeader и DesktopWorkspace как горизонтальная полоска,
          DesktopWorkspace под ней получает оставшуюся высоту через flex:1. */}
      <ProjectIntroCard
        projectId={project.id}
        projectOwnerId={project.ownerId}
        defaultPersonaId={projectDefaultId}
        isMobile={isMobile}
      />

      {/* Тело: сайдбар + контент. position:relative — чтобы drawer/overlay легли под хедер.
          overflow — clip с запасом, а не hidden: тени островов и попапа-превью панели
          выходят за верхнюю кромку тела, и hidden срезал их ровной полосой под шапкой.
          Запас берёт только тени: сам контент по-прежнему обрезается по границе.
          40px — по модальной тени попапа (разлёт 60 при сдвиге 24 → вверх ~36). */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'clip', overflowClipMargin: 40, position: 'relative' }}>

      {/* Тело десктопа и планшета: рельсы панелей по краям, центр между ними */}
        <DesktopWorkspace
          isTablet={isTablet}
          project={project}
          projectForEdit={projectForEdit}
          onOpenWall={() => onSwitchHub('wall')}
          railCounts={railCounts}
          onOpenProjectSettings={() => setEditProjectOpen(true)}
          activeSession={activeSession}
          onSelectSession={handleSelectSession}
          onSessionUpdated={handleSessionUpdated}
          onCreateSession={handleCreateSession}
          onClearSession={handleClearSession}
          creatingSession={creatingSession}
          workflowRunningFor={workflowRunningFor ?? undefined}
          pendingMessage={pendingMessage}
          onPendingMessageSent={() => setPendingMessage(undefined)}
          onWorkflowRunning={handleWorkflowRunning}
          skills={composerSkills}
          agents={skillsData?.agents}
          attachedFiles={attachedFiles}
          onAttachedFilesChange={setAttachedFiles}
          openFile={openFile}
          openFileDiffMode={openFileDiffMode}
          gitStagePath={gitStagePath}
          fileFullscreen={fileFullscreen}
          onToggleFullscreen={handleToggleFileFullscreen}
          onOpenDocLink={handleOpenDocLink}
          scrollToAnchor={scrollToAnchor}
          onFileBack={handleFileBack}
          onFileForward={handleFileForward}
          canFileBack={canFileBack}
          canFileForward={canFileForward}
          openCommitSha={openCommitSha}
          openCommitFile={openCommitFile}
          onCloseCommit={closeCommitView}
          onOpenFileFromChat={handleOpenFileFromChat}
          onCloseFile={backFromFile}
          readerState={reader.state}
          readerActions={reader.actions}
          selectedTask={selectedTask}
          autoEditTaskId={autoEditTaskId}
          onOpenTaskSession={handleOpenTaskSession}
          onOpenFileFromTree={handleOpenFileFromTree}
          onCloseTask={backFromTask}
          selectedPersonaId={selectedPersonaId}
          personaCreating={personaCreating}
          onOpenPersonaChat={handleOpenPersonaChat}
          onPersonaSelectAfterCreate={handlePersonaSelectAfterCreate}
          onPersonaCleared={handlePersonaCleared}
          teamCenterOpen={teamCenterOpen}
          onCloseTeamCenter={() => setTeamCenterOpen(false)}
          teamCenterArea={<TeamCommandCenter project={project} onOpenPersona={handlePersonaSelect} onNewPersona={handlePersonaNew} onOpenSession={handleOpenPersonaChat} onOpenSessionById={handleOpenTaskSession} onClose={() => setTeamCenterOpen(false)} />}
          boardOpen={projectBoard}
          boardArea={ProjectBoardArea}
          previewOpen={!!ccActivePreview}
          previewArea={ccActivePreview ? <PreviewView service={ccActivePreview} projectId={project.id} onStop={stopService} onClose={() => setActivePreviewId(null)} services={previewServices} /> : null}
          onClosePreview={() => setActivePreviewId(null)}
          graphOpen={graphOpen}
          graphArea={<CodeGraphDocument projectId={project.id} isMobile={false} onClose={handleGraphClose} onOpenFile={handleOpenFileFromTree} onBuild={handleGraphBuild} />}
          onOpenReader={handleOpenReader}
          panels={{
            files: <FileExplorer project={project} activeFilePath={openFile} isMobile={false} onOpenFile={handleOpenFileFromTree} onAddToKnowledge={handleAddToKnowledge} onAddFolderToKnowledge={handleAddFolderToKnowledge} onRemoveFromKnowledge={handleRemoveFromKnowledge} indexedFileNames={indexedFileNames} indexingFiles={indexingFiles} indexingFolders={indexingFolders} onAttachToChat={activeSession && !fileFullscreen ? handleAttachToChat : undefined} onOpenDossiers={handleOpenDossiers} />,
            knowledge: <KnowledgePanel project={project} isMobile={false} />,
            // Документация проекта: превью и навигация — в панели, крупное чтение —
            // «развернуть» тем же путём, что открываются остальные файлы
            docs: <DocsPanel project={project} onOpenFile={handleOpenFileFromTree} onAttachToChat={handleAttachToChat} activeFilePath={openFile} onCloseFile={backFromFile} />,
            // «История решений» (change-dossiers, этап 1): гейт по флагу — внутри самой
            // панели (мокап требует видимый вход даже при выключенной фиче — она сама
            // показывает empty-state с кнопкой «Открыть настройки»)
            dossiers: <DossierHistoryPanel project={project} auth={auth} activeFilePath={openFile ?? openCommitFile} chatExcludedFromDossiers={!!activeSession?.excludeFromDossiers} onOpenChat={handleOpenTaskSession} onOpenTask={handleOpenDossierTask} onOpenCommit={handleOpenCommit} />,
            changes: <GitChangesRail project={project} onOpenDiff={handleOpenGitDiff} onOpenFile={handleOpenFileFromTree} onOpenCommit={handleOpenCommit} activeFilePath={openFile ?? openCommitFile} activeCommitSha={openCommitSha} onCommit={handleCommitVia} onScopeChange={clearCenterToChat} />,
            tasks: <TasksPanel project={project} selectedTaskId={selectedTaskId} onSelect={handleSelectTask} isMobile={false} boardMode={projectBoard} onBoardMode={handleProjectBoard} onEditColumns={openColumnsEditor} groupTab={projectGroupTab} onGroupTab={setProjectGroupTab} filters={taskListFilters} onFilters={setTaskListFilters} />,
            team: <ProjectPersonasPanel project={project} selectedId={personaCreating ? null : selectedPersonaId} onSelect={handlePersonaSelect} onNew={handlePersonaNew} onShowTeam={() => { handlePersonaCleared(); setTeamCenterOpen(true); }} teamActive={teamCenterOpen && !selectedPersonaId && !personaCreating} />,
            graph: <CodeGraphPanel projectId={project.id} graphOpen={graphOpen} onEnsureGraphOpen={ensureGraphOpen} onCollapseGraph={handleGraphClose} onOpenFile={handleOpenFileFromTree} onBuild={handleGraphBuild} />,
            // Навыки и агенты рабочей папки. onChanged кладёт свежий состав в тот же
            // skillsData, откуда композер берёт «/»-команды: установка навыка в панели
            // видна в подсказке сразу, без перезагрузки страницы
            skills: <SkillsPanel projectId={project.id} onChanged={setSkillsData} />,
            terminal: <TerminalPanelContent terminals={terminals} activeTerminalId={activeTerminalId} onSelect={handleSelectTerminal} onCreate={handleCreateTerminal} onStop={handleStopTerminal} onActivity={setTerminalBusy} />,
            preview: <PreviewPanelContent projectId={project.id} services={previewServices} activePreviewId={activePreviewId} onSelect={handleSelectPreview} onStart={startService} onStop={stopService} onRefresh={refreshServices} />,
          }}
        />
      </div>

      {columnsDialogEl}
      {showModelsSpend && <ModelsSpendModal onClose={() => setShowModelsSpend(false)} />}
      {editProjectOpen && (
        <EditDialog
          project={projectForEdit}
          onSuccess={updated => { setProjectForEdit(updated); setEditProjectOpen(false); }}
          onIconUpdated={setProjectForEdit}
          onProjectUpdated={setProjectForEdit}
          onClose={() => setEditProjectOpen(false)}
        />
      )}
    </PageCanvas>
  );
}
