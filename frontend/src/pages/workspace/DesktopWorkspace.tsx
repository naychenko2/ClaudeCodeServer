// Тело нового интерфейса проекта «как десктопный Claude Code» (флаг workspace-cc-panels,
// только десктоп ≥1200): слева — панель ТОЛЬКО с чатами проекта, в центре — чат
// (или файл/задача/персона/доска/коммит), справа — рельса рабочих инструментов
// со стеком панелей (RightPanelStack): План, Файлы, Задачи, Команда, Терминал, Preview.
// WorkspacePage остаётся владельцем состояния и обработчиков — сюда всё приходит
// пропсами (контент панелек тоже собирается там); HubHeader и диалоги тоже там.
import { useState, useRef, useEffect, type ReactNode, type PointerEvent as ReactPointerEvent } from 'react';
import { Plus, MessageCircle } from 'lucide-react';
import type { Project, Session, Task, SkillInfo, AgentInfo, ChangedBySession } from '../../types';
import { C, FONT, ISLAND, CHAT_COLUMN_W, SPLASH_W } from '../../lib/design';
import { useCenterOffset } from '../../lib/centerOffset';
import { Button, Island } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { SessionList } from '../../components/SessionList';
import { ChatPanel } from '../../components/ChatPanel';
import { showToast } from '../../lib/toast';
import { addChatSafe } from '../../features/wall/wallStore';
import { WallDock } from '../../features/wall/WallDock';
import { FileViewer } from '../../components/FileViewer';
import { GitCommitView } from '../../components/GitCommitView';
import { TaskDetailsPane } from '../../features/tasks/TaskDetailsPane';
import { ProjectPersonaPane } from '../../features/personas/ProjectPersonasPanel';
import { ProjectRail } from '../../features/projects/ProjectRail';
import { PanelZone } from './PanelZone';
import { TocPanel } from './TocPanel';
import { useSessionPanels } from './useSessionPanels';
import type { DocToc } from '../../hooks/useHeadings';
import { startPointerDrag } from '../../lib/pointerDrag';
import type { PanelKey, RailBadgeInfo } from './panelCatalog';
import { ReaderHeaderBar } from './reader/ReaderHeaderBar';
import { ReaderBody } from './reader/ReaderBody';
import type { ReaderPanelActions, ReaderPanelState } from './reader/useReaderPanel';
import { wsPanels } from './panelStackState';
import { VideoCenter } from '../../features/video/VideoCenter';
import { setVideoCenterBlocked, useVideoCenter, VIDEO_PANEL_EVENT } from '../../lib/videoStage';

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
  // Ридер ссылок — открывается по кнопке-компаньону у внешней ссылки в чате, живёт
  // в центре как просмотр файла: split с чатом либо на всю контентную зону
  // («Развернуть»/«Свернуть» в собственной шапке — см. ReaderHeaderBar). Открытие
  // ридера и открытие файла взаимно вытесняют друг друга — состояние обоих
  // держит WorkspacePage.
  readerState: ReaderPanelState;
  readerActions: ReaderPanelActions;
  selectedTask: Task | null;
  // Задача открыта ИЗ ЛЕНТЫ чата (карточка доклада о выполнении): она встаёт не на всю
  // центральную зону, а split'ом СПРАВА от чата — разговор остаётся перед глазами.
  // Владелец состояния — WorkspacePage (asideTaskId), сюда приходит готовым признаком.
  taskAside?: boolean;
  onOpenTaskAside?: (task: Task) => void;
  autoEditTaskId: string | null;
  onOpenTaskSession: (sessionId: string) => void;
  onOpenFileFromTree: (path: string, line?: number) => void;
  // Переход по md-ссылке из FileViewer (клик в открытом md) — открыть файл на месте,
  // не меняя режим просмотра. anchor — слаг раздела для скролла к заголовку.
  onOpenDocLink?: (path: string, anchor?: string) => void;
  scrollToAnchor?: string | null;
  // Back/Forward по истории открытых файлов (кнопки в тулбаре FileViewer)
  onFileBack?: () => void;
  onFileForward?: () => void;
  canFileBack?: boolean;
  canFileForward?: boolean;
  // Путь git status → чужие чаты, менявшие файл — строка «Также меняли» в шапке диффа
  // открытого файла (тот же источник, что бейдж панели «Изменения»)
  changedBy?: Map<string, ChangedBySession[]>;
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
  // Панели проекта: готовый контент панелек. Контент общий для обеих зон — панель
  // рисует та зона, в которой она лежит.
  panels: Partial<Record<PanelKey, ReactNode>>;
  // Открыть URL в панели «Чтение» — из кнопки-компаньона у внешней ссылки в чате
  onOpenReader?: (url: string) => void;
  // Кружки-индикаторы на кнопках проекта в рельсе (changes/tasks/terminal/preview):
  // primary (основной), secondary (серый; незапушенные), hint (расшифровка в тултипе)
  railBadges?: Partial<Record<PanelKey, RailBadgeInfo>>;
  // Открыть режим «Стена» (док стены под доком проектов; вкладки в таббаре у стены нет)
  onOpenWall?: () => void;
}

export function DesktopWorkspace(p: Props) {
  // Подсветка активного сплиттера: сайдбар или split чат|файл
  const [dragging, setDragging] = useState<'sidebar' | 'split' | null>(null);

  // Панели текущей сессии (План/Агенты/Персона). Раньше их собирала правая зона
  // внутри себя — и потому они были прибиты к ней; теперь это часть общего набора,
  // и они переносятся между рельсами наравне с остальными.
  const sessionPanels = useSessionPanels(p.activeSession, p.project.id, p.project.rootPath);

  // Оглавление документа, открытого в центре: его отдаёт сам FileViewer (см. DocToc),
  // а панель «Оглавление» только показывает. null — оглавления сейчас нет: панель
  // исчезает вместе со своей кнопкой, но МЕСТО В РАСКЛАДКЕ за собой сохраняет — этим
  // и держится нужное поведение: открытая панель возвращается сама на следующем md,
  // а закрытая пользователем остаётся закрытой (см. keyAvailable в PanelZone).
  const [toc, setToc] = useState<DocToc | null>(null);

  // Пропорция чат/файл в split-режиме (как chatFlex в старой ветке; не персистится)
  const [chatFlex, setChatFlex] = useState(1);
  const splitContainerRef = useRef<HTMLDivElement>(null);

  // Эксклюзив боковых сторон на планшете: гейтит режим флагом exclusive в сторе
  // (его читают обе PanelZone + reveal из FileViewer/GitBar). При входе в планшет
  // активной становится левая («Чаты») — её stash разворачивается, правая compact
  // чистится эффектом в PanelZone. При выходе/unmount флаг снимается. Сейчас
  // его поднимают проектный воркспейс (этот файл) и раздел «Чаты» (ChatsPage) —
  // у обоих есть PanelZone с compact={isTablet}. WallPage и остальные разделы
  // хаба compact не передают, и эксклюзив у них не действует.
  const { setExclusive, markActive, closeCompactStack, reveal } = wsPanels.use();
  // Канал выбрали в КАТАЛОГЕ — эфир идёт в боковой панели, а та могла быть закрыта
  // или лежать в ящике рельсы. Раскладка — епархия страницы, поэтому являет панель
  // она, а стор только просит об этом событием.
  useEffect(() => {
    const show = () => reveal('video');
    window.addEventListener(VIDEO_PANEL_EVENT, show);
    return () => window.removeEventListener(VIDEO_PANEL_EVENT, show);
  }, [reveal]);

  useEffect(() => {
    setExclusive(!!p.isTablet);
    if (p.isTablet) markActive('left');
    return () => setExclusive(false);
    // setExclusive/markActive стабильны (useCallback в сторе) — в deps не нужны
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.isTablet]);

  // Планшет: смена активной сессии закрывает открытые tablet-дроверы обеих зон —
  // после навигации пользователю нужен чат, а не список поверх него. Триггер —
  // ЛЮБОЙ источник смены id (список, уведомление, диплинк), не отдельные клики:
  // навигация ВНУТРИ контентных панелей («открыть файл») дровер не закрывает,
  // а вот смена активного чата — закрывает. closeCompactStack в сторе — счётчик,
  // обе PanelZone слушают его и чистят свой локальный tabletPanels.
  useEffect(() => {
    if (!p.isTablet) return;
    closeCompactStack();
    // closeCompactStack стабилен (useCallback в сторе) — в deps не нужен
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.isTablet, p.activeSession?.id]);

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
    // Эфир трогать не надо: развёрнутый кадр занимает центр вместо ленты, но
    // выбор чата его не гасит — кадр сворачивают сами, кнопкой или крестиком,
    // а насильный возврат прервал бы просмотр на полуслове.
    p.onSelectSession(s, firstMessage, autoSelect);
  };

  const personaOpen = !!p.selectedPersonaId || p.personaCreating;

  // Задача рядом с чатом: тот же split, что у файла и «Чтения». На планшете сплита нет —
  // там задача, как и раньше, занимает центр целиком.
  const taskSplit = !!p.selectedTask && !!p.taskAside && !p.isTablet
    && !p.openFile && !p.readerState.open && !p.openCommitSha;

  // «Стена»: док и пункт меню карточек чата. Планшету она доступна (колонок туда
  // влезает одна-две), отсекается только телефон — но мобильной ветки тут и нет
  const wallOn = !!p.onOpenWall;
  const handleAddToWall = (s: Session) => {
    void addChatSafe(s).then(() => showToast('Стена', `«${s.name?.trim() || 'Чат'}» на стене`));
  };

  // Контент панели «Чаты» левой рельсы. Заголовок панели рисует PanelShell, поэтому
  // здесь только содержимое — список чатов на белом фоне контентной зоны, как у
  // панелей правой рельсы. Переключатель проектов жил сначала шапкой внутри этой
  // панели, потом отдельной панелью «Проекты»; теперь это док второй левой рельсы.
  // Панель есть ВСЕГДА, даже когда чатов ноль: иначе её кнопка пропадала из рельсы
  // (нет контента → keyAvailable=false), край рельсы дёргался и вход в чаты терялся.
  // Пустой список сам показывает empty-state с кнопкой «Новый чат» (см. SessionList).
  const chatsPanel = (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
      <SessionList project={p.project} activeSession={p.activeSession} onSelect={handleSelectSession} onSessionUpdated={p.onSessionUpdated} onCleared={p.onClearSession} isMobile={false} workflowRunningFor={p.workflowRunningFor} onAddToWall={wallOn ? handleAddToWall : undefined} />
    </div>
  );

  // ОБЩИЙ набор контента панелей: обе зоны получают его целиком и рисуют только
  // те панели, что лежат именно в них. Чаты собираются здесь, инструменты проекта
  // приходят из WorkspacePage, панели сессии — из useSessionPanels.
  const zonePanels: Partial<Record<PanelKey, ReactNode>> = {
    chats: chatsPanel,
    ...p.panels,
    // Оглавление живёт ровно столько, сколько открытый в центре md: контент null —
    // и панель с кнопкой уходят с экрана сами
    toc: toc ? <TocPanel toc={toc} /> : undefined,
  };

  const videoCenter = useVideoCenter();

  // Фабрика центра-чата: одиночный режим — чат без рамки с шапкой-островом
  // (headerIsland), в split рядом с файлом — обычный вид внутри своего острова.
  const chatPanel = (headerIsland: boolean) => p.activeSession ? (
    <ChatPanel
      session={p.activeSession} project={p.project} onOpenFile={p.onOpenFileFromChat} onOpenReader={p.onOpenReader}
      // Задача рядом с лентой — только там, где для split'а есть ширина: на планшете
      // центр отдан одному режиму целиком, и карточка доклада откроет детали модалкой
      onOpenTaskAside={p.isTablet ? undefined : p.onOpenTaskAside}
      pendingMessage={p.pendingMessage} onPendingMessageSent={p.onPendingMessageSent}
      onSessionUpdated={p.onSessionUpdated} isMobile={false} onWorkflowRunning={p.onWorkflowRunning}
      skills={p.skills} agents={p.agents}
      attachedFiles={p.attachedFiles} onAttachedFilesChange={p.onAttachedFilesChange}
      headerIsland={headerIsland}
      // Действия чата из шапки, которыми владеет экран: набор стены и уход из
      // удалённого чата (тот же колбэк, что чистит выбор после удаления в списке)
      onAddToWall={wallOn ? () => handleAddToWall(p.activeSession!) : undefined}
      onChatDeleted={p.onClearSession}
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
  // остаётся сверх ПОЛНОЙ потребности ленты (колонка + полоса прокрутки + отступ).
  // Но ленты может и не быть: пока чат не выбран, в центре стоит заставка вдвое уже,
  // и её потребность — SPLASH_W. Дашь тут CHAT_COLUMN_W — на окне ноутбука запаса
  // не останется вовсе, компенсация выродится в ноль и заставку перекосит.
  const centerFree = !p.openFile && !p.readerState.open && !p.openCommitSha && !p.selectedTask && !personaOpen
    && !p.teamCenterOpen && !p.boardOpen && !p.previewOpen && !p.graphOpen;
  // Эфир и каталог занимают тот же остров, что файл и задача, — значит уступают им.
  // Уступка живёт ОДНИМ эффектом, а не колбэком в каждом из девяти открывателей
  // центра: те раскиданы по WorkspacePage, и один забытый вызов дал бы два острова
  // в одном месте раскладки.
  const videoInCenter = !!videoCenter && centerFree;
  const chatOnly = centerFree && !videoInCenter;
  const centerContentW = chatOnly ? (p.activeSession ? CHAT_COLUMN_W : SPLASH_W) : undefined;
  const { rootRef: offsetRootRef, centerRef: offsetCenterRef } = useCenterOffset(centerContentW);

  // Занятость центра публикуем в стор видео: он и освободит остров от эфира, и
  // погасит кнопки «развернуть в центре», пока место занято. Гасить эфир прямо
  // отсюда было мало — центр, занятый ЗАРАНЕЕ, признак не менял, эффект молчал,
  // а кадр, отправленный в такой центр, исчезал и с экрана, и из панели.
  useEffect(() => {
    setVideoCenterBlocked(!centerFree);
    // Уходя со страницы, запрет снимаем: в «Чатах» центр видео доступен всегда
    return () => setVideoCenterBlocked(false);
  }, [centerFree]);

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
        // Планшет: левая рельса тоже уходит в компакт-режим, иначе панель чатов шириной
        // ~300px на 768px окна съедает 39% ленты и ломает чтение. Гейт drawer'а по
        // windowWidth × PANEL_INLINE_MAX_SHARE (см. PanelZone), на широком планшете
        // (≥1000px) стек встаёт inline, как у правой зоны (строка 459).
        compact={p.isTablet}
        panels={zonePanels}
        railBadges={p.railBadges}
        sessionPanels={sessionPanels}
        railFooter={
          // Вертикаль капсул у края окна: док проектов, под ним — док стены (вход в
          // режим «Стена»: клик или дроп карточки чата из панели «Чаты»)
          // flex: 1 — по высоте этой обёртки док проектов считает число видимых
          // иконок; без неё они все уезжали бы под лупу «ещё N»
          <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
            <ProjectRail project={p.projectForEdit} onOpenSettings={p.onOpenProjectSettings} />
            {wallOn && <WallDock onOpenWall={p.onOpenWall!} />}
          </div>
        }
      />

      {/* === Центр: коммит → задача → персона → доска → файл/ридер (split/fullscreen) → чат === */}
      {!p.openFile && !p.readerState.open && p.openCommitSha && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex' }}>
          <GitCommitView project={p.project} sha={p.openCommitSha} initialPath={p.openCommitFile} onClose={p.onCloseCommit} />
        </div>
      )}

      {!p.openFile && !p.readerState.open && !p.openCommitSha && p.selectedTask && !taskSplit && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <TaskDetailsPane key={p.selectedTask.id} task={p.selectedTask} project={p.project} startInEdit={p.selectedTask.id === p.autoEditTaskId} onOpenSession={p.onOpenTaskSession} onOpenFile={p.onOpenFileFromTree} onClose={p.onCloseTask} onDeleted={p.onCloseTask} />
        </div>
      )}

      {/* Split чат|задача — задача открыта из карточки доклада в ленте: разговор остаётся
          слева, детали встают справа (тот же приём и тот же сплиттер, что у файла и ридера) */}
      {taskSplit && (
        <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0, margin: `0 ${ISLAND.centerGap}px` }}>
          <Island bg={C.bgMain} style={{ flex: chatFlex, minWidth: 200 }}>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              {chatPanel(false)}
            </div>
          </Island>
          <IslandSplitter orientation="v" active={dragging === 'split'} onMouseDown={handleSplitDrag} />
          <Island bg={C.bgMain} style={{ flex: 1, minWidth: 200 }}>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              <TaskDetailsPane key={p.selectedTask!.id} task={p.selectedTask!} project={p.project} onOpenSession={p.onOpenTaskSession} onOpenFile={p.onOpenFileFromTree} onClose={p.onCloseTask} onDeleted={p.onCloseTask} />
            </div>
          </Island>
        </div>
      )}

      {/* Студия персоны из панельки «Команда»: закрытие — крестиком справа
          (левой стрелки «назад» на десктопе нет) */}
      {!p.openFile && !p.readerState.open && !p.openCommitSha && !p.selectedTask && personaOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <ProjectPersonaPane project={p.project} personaId={p.personaCreating ? null : p.selectedPersonaId} creating={p.personaCreating} onOpenChat={p.onOpenPersonaChat} onSelectPersona={p.onPersonaSelectAfterCreate} onCleared={p.onPersonaCleared} onClose={p.onPersonaCleared} />
        </div>
      )}

      {/* Командный центр (кнопка «Команда» в панельке персон) */}
      {!p.openFile && !p.readerState.open && !p.openCommitSha && !p.selectedTask && !personaOpen && p.teamCenterOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {p.teamCenterArea}
        </div>
      )}

      {/* Доска задач (вкладка «Доска» в панельке задач) */}
      {!p.openFile && !p.readerState.open && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && p.boardOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {p.boardArea}
        </div>
      )}

      {/* Превью dev-сервиса (выбран в панельке «Preview») */}
      {!p.openFile && !p.readerState.open && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && p.previewOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {p.previewArea}
        </div>
      )}

      {/* Документ «Граф зависимостей» (открыт из панельки «Граф») */}
      {!p.openFile && !p.readerState.open && !p.openCommitSha && !p.selectedTask && !personaOpen && !p.teamCenterOpen && !p.boardOpen && !p.previewOpen && p.graphOpen && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {p.graphArea}
        </div>
      )}

      {/* Видео занимает центр ЦЕЛИКОМ — и кадр, и каталог. Кадр пробовали ставить
          вторым островом рядом с лентой, как файл: в узкой половине от него мало
          толку, а разворачивают его как раз тогда, когда хотят смотреть, а не
          переписываться. Фоновый просмотр закрывает панель рельсы, она рядом. */}
      {videoInCenter && centerIsland(<VideoCenter />)}

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
              <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onToggleFullscreen={p.onToggleFullscreen} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} onOpenFile={p.onOpenDocLink} scrollToAnchor={p.scrollToAnchor} onFileBack={p.onFileBack} onFileForward={p.onFileForward} canFileBack={p.canFileBack} canFileForward={p.canFileForward} onTocChange={setToc} changedBy={p.changedBy?.get(p.openFile ?? '')} onOpenChat={p.onOpenTaskSession} />
            </div>
          </Island>
        </div>
      )}

      {p.openFile && (p.fileFullscreen || p.isTablet) && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {/* На планшете сплита нет — тумблер режима не показываем */}
          <FileViewer project={p.project} filePath={p.openFile} onClose={p.onCloseFile} onToggleFullscreen={p.isTablet ? undefined : p.onToggleFullscreen} fullscreen={p.fileFullscreen} initialTab={p.openFileDiffMode ? 'diff' : undefined} gitStagePath={p.gitStagePath ?? undefined} onOpenFile={p.onOpenDocLink} scrollToAnchor={p.scrollToAnchor} onFileBack={p.onFileBack} onFileForward={p.onFileForward} canFileBack={p.canFileBack} canFileForward={p.canFileForward} onTocChange={setToc} changedBy={p.changedBy?.get(p.openFile ?? '')} onOpenChat={p.onOpenTaskSession} />
        </div>
      )}

      {/* Split чат|ридер — тот же приём, что у файла: два острова, общий сплиттер
          (файл и ридер взаимно вытесняют друг друга — одновременно не бывают) */}
      {!p.openFile && p.readerState.open && !p.readerState.expanded && !p.isTablet && (
        <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0, margin: `0 ${ISLAND.centerGap}px` }}>
          <Island bg={C.bgMain} style={{ flex: chatFlex, minWidth: 200 }}>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              {chatPanel(false)}
            </div>
          </Island>
          <IslandSplitter orientation="v" active={dragging === 'split'} onMouseDown={handleSplitDrag} />
          <Island bg={C.bgMain} style={{ flex: 1, minWidth: 200 }}>
            <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
              <ReaderHeaderBar state={p.readerState} actions={p.readerActions} onClose={p.readerActions.closeReader} />
              <ReaderBody state={p.readerState} actions={p.readerActions} onClose={p.readerActions.closeReader} />
            </div>
          </Island>
        </div>
      )}

      {!p.openFile && p.readerState.open && (p.readerState.expanded || p.isTablet) && centerIsland(
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {/* На планшете сплита нет — тумблер режима не показываем */}
          <ReaderHeaderBar state={p.readerState} actions={p.readerActions} onClose={p.readerActions.closeReader} isTablet={p.isTablet} />
          <ReaderBody state={p.readerState} actions={p.readerActions} onClose={p.readerActions.closeReader} />
        </div>
      )}

      {/* === Справа: стек рабочих панелей + рельса иконок === */}
      <PanelZone
        side="right"
        compact={p.isTablet}
        panels={zonePanels}
        railBadges={p.railBadges}
        sessionPanels={sessionPanels}
        centerFileOpen={!!p.openFile}
      />
    </div>
  );
}
