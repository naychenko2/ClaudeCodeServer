import { useState, useEffect, useRef, lazy, Suspense } from 'react'
import type { Project, AuthState, Session } from './types'
import { C } from './lib/design'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { ChatsPage } from './pages/ChatsPage'
import { WorkspacePage } from './pages/WorkspacePage'
import { ArchivePage } from './pages/ArchivePage'
import type { HubTabValue } from './components/HubTabs'
import { moduleIdOf } from './components/HubTabs'
import { ModuleScreen } from './components/modules/ModuleScreen'
import { loadModules } from './lib/modules'
import { UpdatePrompt } from './components/UpdatePrompt'
import { NotificationToasts } from './components/NotificationToasts'
import { ProductHistory } from './components/ProductHistory'
import { GlobalSearch } from './components/GlobalSearch'
import { AiLauncher } from './components/ai/AiLauncher'
import { OPEN_GLOBAL_SEARCH_EVENT } from './lib/ai/actions'
import { resetAiAwaiting } from './lib/ai/awaiting'
import { PRODUCT_HISTORY_EVENT, productHistorySeenKey } from './components/HubHeader'
import { initConnectivity } from './lib/offline'
import { installSelectionScopes } from './lib/selectionScope'
import { LoadingScreen } from './components/ui/LoadingScreen'
import { recordRecentProject } from './lib/pinnedProjects'
import { useOnline } from './hooks/useOnline'
import { useThemeColor } from './hooks/useThemeColor'
import { projectMainColor } from './features/projects/projectUtil'
import { runOfflineSnapshot, syncProjectFiles, drainOfflineQueues } from './lib/sync'
import { onFilesChanged, onMessage } from './lib/signalr'
import { onProjectIconBackfilled } from './features/projects/useAllProjects'
import { loadWorkspaceState } from './lib/workspaceState'
import { navPush, navReplace, parseHash, getNav, type NavSnapshot } from './lib/nav'
import { api } from './lib/api'
import { idbClear } from './lib/idb'
import { setAllFlags } from './lib/featureFlags'
import { setMeFromServer, clearMe, useMe } from './lib/defaultPersona'
import { IntroChatPage, ProjectIntroChatPage, OPEN_INTRO_EVENT } from './features/onboarding/OnboardingPage'
import { getWallEntry, getWallReturn, isWallActive, setWallActive, setWallEntry, setWallReturn } from './lib/wallMode'
import { useWallFocusProject } from './features/wall/wallStore'
import { setCtxThresholdsFromServer } from './lib/contextPrefs'
import { useIsMobile } from './lib/breakpoints'
import { loadModels } from './lib/models'
import { CalendarPage } from './features/tasks/CalendarPage'
import { NotesPage } from './features/notes/NotesPage'
import { PersonasPage } from './features/personas/PersonasPage'
import { ensureNotificationsSubscribed } from './lib/notifications'
import { KnowledgePage } from './features/knowledge/KnowledgePage'
import { NotificationsPage } from './features/notifications/NotificationsPage'
import { HomePage } from './pages/HomePage'
import { WallPage } from './pages/WallPage'
import { SpendPage } from './features/spend/SpendPage'
import { TelemetryPage } from './features/telemetry/TelemetryPage'
import { setPendingIncident, INCIDENT_OPEN_EVENT } from './features/telemetry/incidentLink'
import { OPEN_SPEND_EVENT, type SpendOpenContext } from './lib/spend'
import { useUiInspector, setUiInspectorAdmin, wireUiInspectorHotkey } from './lib/uiInspector'
import { UiInspectorOverlay } from './features/inspector/UiInspectorOverlay'

const OPEN_PROJECT_KEY = 'cc_open_project'
const HUB_TAB_KEY = 'cc_hub_tab'

// Витрина дизайн-системы — dev-only. Открывается по #/ui-kit, в обход авторизации
// и обычной навигации. import.meta.env.DEV → Vite DCE вычищает и компонент, и роут
// из production-бандла (в prod здесь просто null).
const UiKitPage = import.meta.env.DEV
  ? lazy(() => import('./dev/UiKitPage').then(m => ({ default: m.UiKitPage })))
  : null;

// Симуляция паузы планирования (индикатор «Команда готовит план…») — тоже dev-only
const TeamPlanSimPage = import.meta.env.DEV
  ? lazy(() => import('./dev/TeamPlanSimPage').then(m => ({ default: m.TeamPlanSimPage })))
  : null;

function isDevUiKitHash(): boolean {
  return window.location.hash === '#/ui-kit';
}

function isDevTeamPlanSimHash(): boolean {
  return window.location.hash === '#/team-plan-sim';
}

// Dev-only витрины служебных экранов, которые иначе видно лишь при стечении
// обстоятельств: #/boom роняет рендер намеренно (заглушка ErrorBoundary),
// #/boot показывает заставку старта (обычно она мелькает доли секунды и лишь
// на медленной загрузке). Признак снимается ЗДЕСЬ, при загрузке модуля:
// навигация ниже приводит незнакомый hash к известному экрану ещё до первого
// рендера, и проверка внутри компонента его уже не увидела бы.
// В prod обе ветки вырезаются вместе с DEV.
const devBoom = import.meta.env.DEV && window.location.hash === '#/boom';
const devBoot = import.meta.env.DEV && window.location.hash === '#/boot';

// Диплинк из hash-URL (#/calendar, #/project/{id}/task/{tid}…) — читаем один раз
// при загрузке страницы, до первого рендера (WorkspacePage заберёт pending-значения)
const initialHash = parseHash()
if (initialHash?.screen === 'project' && initialHash.projectId) {
  // Формат «projectId|taskId» — WorkspacePage чужого проекта не заберёт значение
  if (initialHash.taskId) sessionStorage.setItem('cc_pending_task', `${initialHash.projectId}|${initialHash.taskId}`)
  if (initialHash.file) sessionStorage.setItem('cc_pending_file', `${initialHash.projectId}|${initialHash.file}`)
  // Диплинк на чат внутри проекта: #/project/{id}/chat/{chatId}
  if (initialHash.chatId) sessionStorage.setItem('cc_pending_project_chat', `${initialHash.projectId}|${initialHash.chatId}`)
}
// Диплинк #/calendar/task/{id} — личная задача, модал деталей поверх календаря
if (initialHash?.screen === 'calendar' && initialHash.taskId) {
  sessionStorage.setItem('cc_pending_calendar_task', initialHash.taskId)
}
// Диплинк #/chats/{id} — конкретный чат: ChatsPage читает активный чат
// из nav-снимка или localStorage при монтировании
if (initialHash?.screen === 'chats' && initialHash.chatId) {
  localStorage.setItem('cc_open_chat', initialHash.chatId)
}
// Диплинк #/telemetry/incident/{fingerprint} — карточка инцидента из уведомления
// об алерте: TelemetryPage забирает отпечаток при монтировании
if (initialHash?.screen === 'telemetry' && initialHash.incidentFingerprint) {
  setPendingIncident(initialHash.incidentFingerprint)
}

export default function App() {
  // Авторизация — из localStorage (постоянно) или sessionStorage (saveKey=false)
  const [auth, setAuth] = useState<AuthState | null>(() => {
    const token = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token')
    if (!token) return null
    const url = localStorage.getItem('cc_server_url') || window.location.origin
    const username = localStorage.getItem('cc_username') || ''
    const displayName = localStorage.getItem('cc_display_name') || undefined
    const role = localStorage.getItem('cc_role') || sessionStorage.getItem('cc_role') || undefined
    const id = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id') || undefined
    return { serverUrl: url, token, username, displayName, role, id }
  })
  // Если токен восстановлен из localStorage — ждём ответа сервера перед показом контента,
  // чтобы не было flash рабочего экрана с последующим переключением на пустой фон.
  const [authChecking, setAuthChecking] = useState<boolean>(() => {
    return !!(localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
  })
  // Открытый проект — восстанавливаем из localStorage, чтобы рефреш возвращал туда, где был.
  // Состояние внутри проекта (активный чат/файл/панели) восстанавливает сама WorkspacePage.
  const [project, setProject] = useState<Project | null>(() => {
    try {
      const raw = localStorage.getItem(OPEN_PROJECT_KEY)
      return raw ? (JSON.parse(raw) as Project) : null
    } catch {
      return null
    }
  })

  // Активная вкладка хаба — вне открытого проекта. Стартовый экран — дашборд «Домой»:
  // hash-диплинк приоритетнее, а без hash всегда открывается 'home'. Ключ HUB_TAB_KEY
  // теперь write-only (старт его НЕ читает) — записи по коду оставлены как навигационная
  // память для будущего, но на выбор стартового экрана не влияют.
  const [hubTab, setHubTab] = useState<HubTabValue>(() => {
    if (initialHash?.screen === 'home') return 'home'
    if (initialHash?.screen === 'calendar') return 'calendar'
    if (initialHash?.screen === 'chats') return 'chats'
    // 'archive' явно не в NavSnapshot['screen'] (тип не наш) — каст через as string
    if ((initialHash?.screen as string) === 'archive') return 'archive'
    if (initialHash?.screen === 'wall') return 'wall'
    if (initialHash?.screen === 'notes') return 'notes'
    if (initialHash?.screen === 'personas') return 'personas'
    if (initialHash?.screen === 'knowledge') return 'knowledge'
    if (initialHash?.screen === 'spend') return 'spend'
    if (initialHash?.screen === 'telemetry') return 'telemetry'
    if (initialHash?.screen === 'notifications') return 'notifications'
    if (initialHash?.screen === 'module' && initialHash.moduleId) return `module:${initialHash.moduleId}` as HubTabValue
    if (initialHash?.screen === 'projects' || initialHash?.screen === 'project') return 'projects'
    return 'home'
  })
  const effectiveHubTab: HubTabValue = hubTab

  // Цвет титлбара окна (Chromium: meta[name=theme-color]): внутри открытого
  // проекта — фирменный цвет проекта, вне — акцент текущей темы. «Спящий»
  // проект (уход в «Чаты»/«Заметки» без выхода) НЕ красит: красим только когда
  // WorkspacePage реально на экране, т.е. вкладка хаба — 'projects'.
  // На «Стене» шапка идёт за ФОКУСНОЙ колонкой (её проект); внепроектный чат в
  // фокусе — акцент, как и везде вне проекта. Хук зовём отсюда, а не из WallPage:
  // meta один на документ, второй useThemeColor за него дрался бы (эффекты детей
  // отрабатывают раньше родителя, и App затирал бы цвет стены).
  const inProjectScreen = effectiveHubTab === 'projects' && !!project;
  const wallFocusProject = useWallFocusProject();
  const wallProject = effectiveHubTab === 'wall' ? wallFocusProject : null;
  useThemeColor(
    inProjectScreen ? projectMainColor(project!)
      : wallProject ? projectMainColor(wallProject)
        : C.accent
  );

  // Витрина дизайн-системы #/ui-kit — переключается по hash без перезагрузки,
  // работает без авторизации (на экране входа тоже). В prod UiKitPage === null,
  // условие всегда ложно и режим не активируется.
  const [uiKitMode, setUiKitMode] = useState(() => isDevUiKitHash())
  const [teamPlanSimMode, setTeamPlanSimMode] = useState(() => isDevTeamPlanSimHash())

  // Демо экрана ошибки #/boom (dev). Начальное значение — из константы модуля:
  // на старте hash успевают нормализовать до первого рендера. Обратно режим не
  // выключается: из упавшего дерева возвращают кнопки самой заглушки.
  const [boomMode, setBoomMode] = useState(devBoom)

  // Демо заставки старта #/boot (dev). В отличие от #/boom выключается сам при
  // смене hash: заставка своих кнопок не имеет, и выходом служит любая навигация
  // (клик по ней, «назад» браузера).
  const [bootMode, setBootMode] = useState(devBoot)

  // «Что нового» — продуктовая история по всем проектам. Overlay на верхнем уровне,
  // открывается из HubHeader (событие) из любого раздела.
  const [historyOpen, setHistoryOpen] = useState(false)
  useEffect(() => {
    const open = () => {
      setHistoryOpen(true)
      // Вписываем открытие в browser history (#/history поверх текущего снимка с флагом):
      // Back закрывает overlay и возвращает на исходную страницу, «вперёд» — открывает снова
      if (!(window.history.state as { historyOverlay?: boolean } | null)?.historyOverlay) {
        window.history.pushState({ ...(window.history.state ?? {}), historyOverlay: true }, '', '#/history')
      }
      // Фиксируем момент просмотра — от него отсчитывается бейдж новых изменений.
      // Ключ per-user (актуальный id на момент открытия), чтобы на одном устройстве
      // у разных аккаунтов была своя отметка.
      try {
        const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id') || undefined
        localStorage.setItem(productHistorySeenKey(uid), new Date().toISOString())
      } catch { /* ignore */ }
    }
    window.addEventListener(PRODUCT_HISTORY_EVENT, open)
    // Диплинк #/history при полной загрузке страницы — открываем overlay штатным путём
    if (initialHash?.history) open()
    return () => window.removeEventListener(PRODUCT_HISTORY_EVENT, open)
  }, [])

  // Синхронизация overlay «Что нового» с кнопками «назад/вперёд»: состояние открытости
  // повторяет флаг historyOverlay в снимке истории (Back — закрыть, Forward — открыть)
  useEffect(() => {
    const onPop = (e: PopStateEvent) =>
      setHistoryOpen(!!(e.state as { historyOverlay?: boolean } | null)?.historyOverlay)
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  // Знакомство — overlay поверх обычной навигации, план §4, п.4.5. Без projectId —
  // личное, с { projectId } в detail — проектное (в паре с 4.3 это единственный способ
  // показать интервью: гейта, который его открывал бы автоматически, больше нет —
  // только приглашение, волна 5).
  const [introCtx, setIntroCtx] = useState<{ projectId?: string } | null>(null)
  useEffect(() => {
    const open = (e: Event) => {
      const detail = (e as CustomEvent<{ projectId?: string }>).detail ?? {}
      setIntroCtx(detail)
      if (!(window.history.state as { introOverlay?: boolean } | null)?.introOverlay) {
        window.history.pushState({ ...(window.history.state ?? {}), introOverlay: true }, '', '#/intro')
      }
    }
    window.addEventListener(OPEN_INTRO_EVENT, open)
    if (initialHash?.intro) open(new Event(OPEN_INTRO_EVENT))
    return () => window.removeEventListener(OPEN_INTRO_EVENT, open)
  }, [])

  // Синхронизация overlay знакомства с кнопками «назад/вперёд» — тот же приём, что у «Что нового»
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      if (!(e.state as { introOverlay?: boolean } | null)?.introOverlay) setIntroCtx(null)
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])
  const closeIntro = () => {
    if ((window.history.state as { introOverlay?: boolean } | null)?.introOverlay) window.history.back()
    else setIntroCtx(null)
  }

  // Уход в раздел из «глубокого» места (открытый проект, заметка, файл, задача, персона,
  // база знаний) добавляет запись в историю, а не затирает текущую: иначе снимок того, откуда
  // ушли, пропадает и Back уводит мимо. Латеральные переходы с плоского экрана — replace.
  const navToSection = (dest: NavSnapshot) => {
    const cur = getNav()
    const deep = !!cur && (cur.screen === 'project' || !!cur.note || !!cur.file || !!cur.task || !!cur.persona || !!cur.knowledge)
    if (deep) navPush(dest)
    else navReplace(dest)
  }

  // Раздел «Аналитика токенов» — полноценная вкладка хаба (вход через меню аватара,
  // как «Знания»), поэтому главная шапка остаётся сверху. Контекст открытия
  // (фильтр/день/паспорт хода) несем в spendCtx и пробрасываем в экран.
  const [spendCtx, setSpendCtx] = useState<SpendOpenContext | null>(null)
  useEffect(() => {
    const open = (e: Event) => {
      const detail = (e as CustomEvent<SpendOpenContext>).detail ?? {}
      setSpendCtx(detail)
      localStorage.setItem(HUB_TAB_KEY, 'spend')
      setHubTab('spend')
      navToSection({ screen: 'spend' })
    }
    window.addEventListener(OPEN_SPEND_EVENT, open)
    return () => window.removeEventListener(OPEN_SPEND_EVENT, open)
  }, [])

  // Единый поиск, открытый из AI-палитры (App-уровневый оверлей, независимый от шапки)
  const [aiSearchOpen, setAiSearchOpen] = useState(false)
  useEffect(() => {
    const open = () => setAiSearchOpen(true)
    window.addEventListener(OPEN_GLOBAL_SEARCH_EVENT, open)
    return () => window.removeEventListener(OPEN_GLOBAL_SEARCH_EVENT, open)
  }, [])

  // Переход в раздел «Заметки» по клику на [[wikilink]] из файлов/чата.
  // Целевая заметка передаётся через sessionStorage (cc_pending_note_title),
  // NotesPage подхватывает её при монтировании и по тому же событию.
  useEffect(() => {
    const open = () => { localStorage.setItem(HUB_TAB_KEY, 'notes'); setHubTab('notes'); navToSection({ screen: 'notes' }) }
    window.addEventListener('cc-open-note', open)
    return () => window.removeEventListener('cc-open-note', open)
  }, [])

  // Форк чата от лица другой персоны (кнопка «Сменить персону» в чате) для глобальной
  // персоны: переключаемся в раздел «Чаты», где ChatsPage откроет новый чат по id.
  // Канал общий с архивом (ArchivePage.onOpenChat) и уведомлениями проактивных персон
  // (#/chats/{id}) — все они зовут openChatById с одним и тем же контрактом.
  useEffect(() => {
    const open = (e: Event) => {
      const chatId = (e as CustomEvent<{ chatId?: string }>).detail?.chatId
      if (chatId) openChatById(chatId)
    }
    window.addEventListener('cc-open-chat', open)
    return () => window.removeEventListener('cc-open-chat', open)
  }, [])

  // Диплинк #/project/{id}/chat/{chatId} при полной загрузке страницы (клик по пушу
  // из service worker), когда нужный проект УЖЕ восстановлен из localStorage: WorkspacePage
  // смонтирован, сам он ничего не перечитает — будим его событием, чат лежит в sessionStorage.
  // Если проект другой или не открыт, его грузит и пушит в историю эффект диплинка ниже
  // (он ждёт авторизации и кладёт chatId в снимок) — здесь этого делать НЕ надо, иначе
  // получаются два api.projects.list() и две записи истории на один диплинк.
  useEffect(() => {
    if (!initialHash || initialHash.screen !== 'project' || !initialHash.chatId) return;
    if (initialHash.projectId && project?.id === initialHash.projectId) {
      window.dispatchEvent(new Event('cc-pending-project-chat'));
    }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Переход к чату проектной персоны из раздела «Персоны»: открываем её проект.
  // Стартовую сессию PersonasPage кладёт в sessionStorage (cc_pending_session) — её
  // подхватывает WorkspacePage при монтировании.
  useEffect(() => {
    const open = (e: Event) => {
      const p = (e as CustomEvent<{ project?: Project }>).detail?.project
      if (!p) return
      localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(p))
      localStorage.setItem(HUB_TAB_KEY, 'projects')
      navPush({ screen: 'project', project: p, view: 'sidebar', file: null })
      setProject(p)
      setHubTab('projects')
    }
    window.addEventListener('cc-open-session', open)
    return () => window.removeEventListener('cc-open-session', open)
  }, [])
  const isMobileView = useIsMobile()

  // Стор дефолт-персоны/онбординга (фича default-personas-onboarding): обязательного
  // гейта первого входа больше нет (знакомство — приглашение из раздела «Персоны»,
  // см. п.5), но стор нужен ниже — оверлей знакомства (п.4.5) ждёт me.loaded.
  const me = useMe()

  const online = useOnline()
  const onlineRef = useRef(online)
  useEffect(() => { onlineRef.current = online }, [online])
  const useOnlineRef = useRef(() => onlineRef.current)
  // Текущий проект — приоритет для снапшота при выходе из офлайна (без ре-триггера при смене проекта)
  const projectIdRef = useRef<string | undefined>(undefined)
  useEffect(() => { projectIdRef.current = project?.id }, [project?.id])

  // Инвалидация DTO открытого проекта при смене дефолт-персоны: бэк шлёт
  // personas_changed action='default' из PersonasController.MakeDefault — и при
  // онбординге проекта (любой путь: из сессии и «из разговора»), и при смене
  // руководителя в настройках. Перечитываем проект, иначе defaultPersonaId в
  // клиентском стейте протухает и «Новый чат» требует перезагрузку страницы.
  useEffect(() => onMessage(msg => {
    if (msg.type !== 'personas_changed' || msg.action !== 'default') return
    const pid = projectIdRef.current
    if (!pid) return
    api.projects.list()
      .then(list => {
        const fresh = list.find(p => p.id === pid)
        if (!fresh) return
        localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(fresh))
        setProject(fresh)
      })
      .catch(() => { /* офлайн — доосвежится эффектом сверки по project.id */ })
  }), [])

  // Догоняющая генерация дорисовала иконку открытого проекта — подменяем DTO, иначе
  // рабочее пространство держит инициалы до перезагрузки (в списке это делает ProjectListPage)
  useEffect(() => onProjectIconBackfilled(fresh => {
    if (fresh.id !== projectIdRef.current) return
    localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(fresh))
    setProject(fresh)
  }), [])

  // Связь: возврат offline → online теперь тихий. Маркер у аватарки
  // (useConnectionDisplayState) сам показывает состояние с гистерезисом
  // ~3с, об офлайне уже сообщает заглушка композера. Тост «Связь восстановлена»
  // убран — на нестабильном WiFi он вылетал на каждый блип и раздражал.
  useEffect(() => { initConnectivity() }, [])

  // UI-инспектор (admin-only): хоткей Ctrl+Alt+I регистрируется один раз, admin-флаг
  // стора следует за ролью (logout гасит и флаг, и включённый режим)
  const uiInspectorOn = useUiInspector()
  useEffect(() => { wireUiInspectorHotkey() }, [])
  useEffect(() => { setUiInspectorAdmin(auth?.role === 'admin') }, [auth?.role])

  // Ctrl+A/Ctrl+C по «активному документу» (файл, заметка, последний ответ в чате)
  useEffect(() => installSelectionScopes(), [])

  // При наличии сохранённых credentials — немедленно зондируем сервер, чтобы _online
  // выставился правильно ещё до первого рендера страниц (navigator.onLine ≠ «сервер доступен»)
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- одноразовый зонд доступности сервера по сохранённым credentials
    if (!auth) { setAuthChecking(false); return }
    // Максимум 3 секунды на проверку доступности сервера.
    // Если не ответил — показываем приложение в текущем (возможно офлайн) состоянии.
    const timer = setTimeout(() => setAuthChecking(false), 3_000)
    api.auth.me()
      .then(me => {
        if (me?.featureFlags) setAllFlags(me.featureFlags)
        setCtxThresholdsFromServer(me?.contextThresholds)
        // Стор дефолт-персоны/онбординга — от него живут приглашение первого входа
        // и резолвер аватаров
        if (me) setMeFromServer(me)
        // Имя могли поправить в профиле после логина — подхватываем без перевхода
        const fresh = me?.displayName?.trim() || undefined
        setAuth(prev => (prev && prev.displayName !== fresh ? { ...prev, displayName: fresh } : prev))
        if (fresh) localStorage.setItem('cc_display_name', fresh)
        else localStorage.removeItem('cc_display_name')
        // Каталог моделей + резолвнутые назначения агентных мест (за ними стоит пункт
        // «По умолчанию» в пикерах) — одним запросом, fire-and-forget, есть fallback
        loadModels()
        void loadModules() // список внешних модулей платформы для вкладок оболочки (R6)
        // Таймзона устройства — серверу для напоминаний (fire-and-forget)
        const tz = Intl.DateTimeFormat().resolvedOptions().timeZone
        if (tz) api.auth.setTimeZone(tz).catch(() => {})
      })
      .catch(() => { /* результат отразится в _online */ })
      .finally(() => {
        clearTimeout(timer)
        setAuthChecking(false)
      })
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auth?.serverUrl])

  // Сервер отверг API-ключ (401) → разлогиниваем и уводим на экран входа
  useEffect(() => {
    const onUnauthorized = () => {
      localStorage.removeItem('cc_token')
      localStorage.removeItem('cc_username')
    localStorage.removeItem('cc_display_name')
      localStorage.removeItem('cc_server_url')
      localStorage.removeItem('cc_role')
      localStorage.removeItem('cc_user_id')
      localStorage.removeItem(OPEN_PROJECT_KEY)
      sessionStorage.removeItem('cc_token')
      sessionStorage.removeItem('cc_role')
      sessionStorage.removeItem('cc_user_id')
      idbClear() // чистим кэш, чтобы данные не утекли к следующей сессии
      clearMe()
      // Раздел сбрасываем вместе с адресом: initialHash читается один раз при загрузке
      // модуля, поэтому вход без перезагрузки страницы оставил бы hubTab прошлого
      // пользователя — при смене аккаунта человек видел бы чужой раздел
      localStorage.setItem(HUB_TAB_KEY, 'projects')
      setHubTab('projects')
      navReplace({ screen: 'projects' })
      setProject(null)
      setAuth(null)
    }
    window.addEventListener('cc-unauthorized', onUnauthorized)
    return () => window.removeEventListener('cc-unauthorized', onUnauthorized)
  }, [])

  // Сидируем стек истории под восстановленное состояние, чтобы кнопки «назад/вперёд»
  // работали и после перезагрузки/диплинка (а не выкидывали из приложения сразу).
  useEffect(() => {
    // Витрина дизайн-системы #/ui-kit живёт вне навигации хаба: её parseHash() === null,
    // поэтому стандартный seed ниже сбросил бы URL на дефолт (#/home) → hashchange listener
    // погасил бы uiKitMode, и витрина закрылась бы сразу после открытия.
    if (isDevUiKitHash()) return;
    if (isDevTeamPlanSimHash()) return;
    const seed: NavSnapshot = { screen: hubTab === 'home' ? 'home' : hubTab === 'chats' ? 'chats' : hubTab === 'wall' ? 'wall' : hubTab === 'calendar' ? 'calendar' : hubTab === 'notes' ? 'notes' : hubTab === 'personas' ? 'personas' : hubTab === 'knowledge' ? 'knowledge' : hubTab === 'spend' ? 'spend' : hubTab === 'telemetry' ? 'telemetry' : hubTab === 'notifications' ? 'notifications' : 'projects' }
    // Диплинк #/notes/{id}: сохраняем заметку в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'notes' && initialHash?.screen === 'notes') seed.note = initialHash.noteId ?? null
    // Диплинк #/personas/{id}: сохраняем персону в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'personas' && initialHash?.screen === 'personas') seed.persona = initialHash.personaId ?? null
    // Диплинк #/knowledge/{id}: сохраняем базу знаний в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'knowledge' && initialHash?.screen === 'knowledge') seed.knowledge = initialHash.knowledgeId ?? null
    // Диплинк #/calendar/board: сохраняем доску, чтобы URL пережил перезагрузку
    if (seed.screen === 'calendar' && initialHash?.screen === 'calendar' && initialHash.board) seed.board = true
    // Диплинк #/chats/{id}: сохраняем чат в снимок, иначе сид затрёт id в URL
    // (присваиваем ДО navReplace — иначе снимок уже записан и адрес схлопывается в #/chats)
    if (seed.screen === 'chats' && initialHash?.screen === 'chats' && initialHash.chatId) seed.chatId = initialHash.chatId
    // Диплинк #/history: сид не должен затирать открытый overlay «Что нового» —
    // иначе адрес уезжает на #/home, а страница остаётся открытой
    if (!initialHash?.history) navReplace(seed)
    // Запись уровня проекта пушим только когда активен именно раздел «Проекты» с открытым
    // проектом — при hubTab==='chats' проект «спит» и в истории не отражается.
    // Если hash-диплинк указывает на ДРУГОЙ проект — восстановленный не пушим,
    // его откроет эффект диплинка (иначе гонка перетирает URL).
    const hashOtherProject = initialHash?.screen === 'project'
      && !!initialHash.projectId && initialHash.projectId !== project?.id
    if (hubTab === 'projects' && project && !hashOtherProject) {
      const chatFromHash = initialHash?.screen === 'project' && initialHash.chatId ? initialHash.chatId : undefined
      navPush({ screen: 'project', project, view: chatFromHash ? undefined : 'sidebar', file: null, chatId: chatFromHash })
      const ws = loadWorkspaceState(project.id)
      if (ws?.openFile && !chatFromHash) navPush({ screen: 'project', project, view: 'sidebar', file: ws.openFile })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Кнопки «назад/вперёд» браузера: восстанавливаем уровень проекта из снимка истории.
  // Вложенную навигацию (sidebar/chat/file) обрабатывает WorkspacePage из того же popstate.
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null
      // Уход из зоны проектов кнопкой «назад» — та же память режима, что при клике
      // по пилюле (switchHubTab): клик «Проекты» из другого раздела вернёт туда,
      // где были до ухода. Внутренняя навигация зоны ('project'/'projects') не считается
      if (s && s.screen !== 'project' && s.screen !== 'projects' && (hubTab === 'projects' || hubTab === 'wall')) {
        setWallReturn(hubTab === 'wall' ? 'wall' : project ? 'workspace' : 'list')
      }
      if (s?.screen === 'project' && s.project) {
        // Возврат в открытый проект
        if (project?.id !== s.project.id) {
          localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(s.project))
          setProject(s.project)
        }
        if (hubTab !== 'projects') { localStorage.setItem(HUB_TAB_KEY, 'projects'); setHubTab('projects') }
      } else if (s?.screen === 'home') {
        // Дашборд «Домой» — проект «спит»
        if (hubTab !== 'home') { localStorage.setItem(HUB_TAB_KEY, 'home'); setHubTab('home') }
      } else if (s?.screen === 'chats') {
        // Раздел «Чаты» — открытый проект «спит», его не сбрасываем (навигационная память)
        if (hubTab !== 'chats') { localStorage.setItem(HUB_TAB_KEY, 'chats'); setHubTab('chats') }
      } else if ((s?.screen as string) === 'archive') {
        // Раздел «Архив» — проект «спит». 'archive' явно не в NavSnapshot['screen'],
        // записан кастом из в source: сравниваем через as string.
        if (hubTab !== 'archive') { localStorage.setItem(HUB_TAB_KEY, 'archive'); setHubTab('archive') }
      } else if (s?.screen === 'wall') {
        // «Стена» — проект «спит», как в остальных разделах хаба
        if (hubTab !== 'wall') { localStorage.setItem(HUB_TAB_KEY, 'wall'); setHubTab('wall') }
      } else if (s?.screen === 'calendar') {
        // Раздел «Календарь» — проект тоже «спит»
        if (hubTab !== 'calendar') { localStorage.setItem(HUB_TAB_KEY, 'calendar'); setHubTab('calendar') }
      } else if (s?.screen === 'notes') {
        // Раздел «Заметки» — проект «спит»
        if (hubTab !== 'notes') { localStorage.setItem(HUB_TAB_KEY, 'notes'); setHubTab('notes') }
      } else if (s?.screen === 'personas') {
        // Раздел «Персоны» — проект «спит»
        if (hubTab !== 'personas') { localStorage.setItem(HUB_TAB_KEY, 'personas'); setHubTab('personas') }
      } else if (s?.screen === 'knowledge') {
        // Раздел «Знания» — проект «спит»
        if (hubTab !== 'knowledge') { localStorage.setItem(HUB_TAB_KEY, 'knowledge'); setHubTab('knowledge') }
      } else if (s?.screen === 'spend') {
        // Раздел «Аналитика токенов» — проект «спит»
        if (hubTab !== 'spend') { localStorage.setItem(HUB_TAB_KEY, 'spend'); setHubTab('spend') }
      } else if (s?.screen === 'telemetry') {
        // Раздел «Телеметрия» — проект «спит»
        if (hubTab !== 'telemetry') { localStorage.setItem(HUB_TAB_KEY, 'telemetry'); setHubTab('telemetry') }
      } else if (s?.screen === 'notifications') {
        // Раздел «Уведомления» — проект «спит»
        if (hubTab !== 'notifications') { localStorage.setItem(HUB_TAB_KEY, 'notifications'); setHubTab('notifications') }
      } else if (s?.screen === 'module' && s.moduleId) {
        // Раздел внешнего модуля — проект «спит»
        const tab = `module:${s.moduleId}` as HubTabValue
        if (hubTab !== tab) { localStorage.setItem(HUB_TAB_KEY, tab); setHubTab(tab) }
      } else if (s?.screen === 'projects') {
        // Список проектов — явный выход из проекта
        if (project) { localStorage.removeItem(OPEN_PROJECT_KEY); setProject(null) }
        if (hubTab !== 'projects') { localStorage.setItem(HUB_TAB_KEY, 'projects'); setHubTab('projects') }
      }
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [project, hubTab])

  // Диплинк #/project/{id}: открываем указанный проект после авторизации
  // (если он отличается от восстановленного из localStorage)
  useEffect(() => {
    if (!auth || initialHash?.screen !== 'project' || !initialHash.projectId) return
    if (project?.id === initialHash.projectId) return
    api.projects.list()
      .then(list => {
        const p = list.find(x => x.id === initialHash.projectId)
        if (p) {
          localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(p))
          setProject(p)
          // Пишем проект из диплинка в историю (сид его пропустил из-за расхождения)
          navPush({ screen: 'project', project: p, view: 'sidebar', file: null, chatId: initialHash.chatId || undefined })
        }
      })
      .catch(() => { /* офлайн/нет доступа — остаёмся на восстановленном состоянии */ })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auth?.serverUrl])

  // Снэпшот/drain — только при устойчивом онлайне. На мобиле связь часто «мигает»
  // (online → offline → online за секунды), и каждое возвращение дёргать полную
  // синхронизацию — перегружать канал и снова ронять связь. Задержка 5с + потолок
  // раз в минуту оставляют только осмысленные возвраты.
  const lastHeavySyncRef = useRef(0)
  const stableOnlineTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  useEffect(() => {
    if (stableOnlineTimerRef.current !== null) {
      clearTimeout(stableOnlineTimerRef.current)
      stableOnlineTimerRef.current = null
    }
    if (!auth || !online) return
    const now = Date.now()
    const sinceLast = now - lastHeavySyncRef.current
    if (lastHeavySyncRef.current > 0 && sinceLast < 60_000) return
    stableOnlineTimerRef.current = setTimeout(() => {
      stableOnlineTimerRef.current = null
      // Проверяем, что за 5с нас не унесло обратно в офлайн.
      if (!useOnlineRef.current()) return
      lastHeavySyncRef.current = Date.now()
      void drainOfflineQueues()
      runOfflineSnapshot(projectIdRef.current)
    }, 5_000)
    return () => {
      if (stableOnlineTimerRef.current !== null) {
        clearTimeout(stableOnlineTimerRef.current)
        stableOnlineTimerRef.current = null
      }
    }
  }, [auth, online])

  // Восстановленный из localStorage проект мог быть удалён на сервере (или список очищен).
  // Сверяемся со списком проектов и, если «призрака» там нет, выходим к списку.
  // Только онлайн: офлайн полагаемся на кэш и не выкидываем пользователя.
  useEffect(() => {
    if (!auth || !online || !project) return
    let cancelled = false
    api.projects.list()
      .then(list => {
        if (cancelled) return
        const fresh = list.find(p => p.id === project.id)
        if (!fresh) {
          localStorage.removeItem(OPEN_PROJECT_KEY)
          navReplace({ screen: 'projects' })
          setProject(null)
          return
        }
        // Освежаем объект проекта серверными данными (в т.ч. boardColumns) — кэш мог устареть
        localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(fresh))
        setProject(fresh)
      })
      .catch(() => { /* сервер недоступен — остаёмся в проекте, не трогаем состояние */ })
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- реагируем на смену id, а не объекта: эффект сам зовёт setProject(fresh), включение project дало бы цикл перезапросов
  }, [auth, online, project?.id])

  // Watcher: сервер уведомил об изменении файлов проекта → инкрементальный ре-синк офлайн-кэша
  useEffect(() => onFilesChanged(({ projectId }) => { syncProjectFiles(projectId) }), [])

  // Подписка на уведомления через SignalR (даже если раздел ещё не открыт)
  useEffect(() => { ensureNotificationsSubscribed(); }, []);

  // Dev-витрина #/ui-kit — переключение hash (вход/выход из режима) без перезагрузки.
  // Тем же слушателем ловим #/boom: иначе демо экрана ошибки открывалось бы только
  // с полной перезагрузкой, а вписанный в адресную строку hash ничего не делал.
  useEffect(() => {
    const onHash = () => {
      setUiKitMode(isDevUiKitHash());
      if (import.meta.env.DEV && window.location.hash === '#/boom') setBoomMode(true);
      if (import.meta.env.DEV) setBootMode(window.location.hash === '#/boot');
    };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  // Dev-симуляция паузы планирования #/team-plan-sim — тот же механизм
  useEffect(() => {
    const onHash = () => setTeamPlanSimMode(isDevTeamPlanSimHash());
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  // Синхронизация раздела с URL при внешней смене hash. Внутренняя навигация (navPush/
  // navReplace) идёт через pushState/replaceState и сама НЕ диспатчит hashchange → рекурсии
  // нет. Этот обработчик ловит остальные источники смены URL — ручную вставку hash в
  // адресную строку, location.hash = '…' из консоли, клик по якорной ссылке (#/personas и
  // т.п.). Без него hubTab остаётся прежним и React продолжает рендерить предыдущий раздел
  // даже после смены URL (находка N2/F8 — на 390 помогал только клик через меню «Ещё»).
  useEffect(() => {
    const onHash = () => {
      const target = parseHash()
      if (!target) return
      // Overlay'ы (#/history, #/intro) — собственная логика выше в onPop
      if (target.history || target.intro) return
      if (target.screen === 'project' && target.projectId) {
        // Диплинк на проект из внешнего источника (вставка URL в адресную строку).
        // Уже открытый этот же проект — выходим, чтобы не гонять api.projects.list.
        if (project?.id !== target.projectId) {
          api.projects.list()
            .then(list => {
              const p = list.find(x => x.id === target.projectId)
              if (p) openProjectFromHome(p)
            })
            .catch(() => { /* офлайн/нет доступа — оставляем текущий экран */ })
        }
        return
      }
      if (target.screen === 'projects') {
        // #/projects — закрыть открытый проект и уйти к списку (та же логика, что
        // у switchHubTab('projects') при повторном клике по активной пилюле «Проекты»).
        if (hubTab !== 'projects' || project) switchHubTab('projects')
        return
      }
      // Маппинг экранов хаба → HubTabValue. 'archive' сюда не попадает:
      // parseHash не выдаёт такой screen (тип NavSnapshot не наш), раздел открывается
      // только кодом из HubTabs/App — пользовательский ввод URL его не активирует.
      let next: HubTabValue | null = null
      switch (target.screen) {
        case 'home': next = 'home'; break
        case 'chats': next = 'chats'; break
        case 'wall': next = 'wall'; break
        case 'calendar': next = 'calendar'; break
        case 'notes': next = 'notes'; break
        case 'personas': next = 'personas'; break
        case 'knowledge': next = 'knowledge'; break
        case 'spend': next = 'spend'; break
        case 'telemetry': next = 'telemetry'; break
        case 'notifications': next = 'notifications'; break
        case 'module':
          if (target.moduleId) next = `module:${target.moduleId}` as HubTabValue
          break
      }
      if (next && next !== hubTab) switchHubTab(next)
    };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- нужен свежий hubTab/project при срабатывании; switchHubTab/openProjectFromHome стабильны между рендерами
  }, [hubTab, project]);

  const openProject = (p: Project) => {
    recordRecentProject(p.id)
    localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(p))
    navPush({ screen: 'project', project: p, view: 'sidebar', file: null })
    setProject(p)
  }
  // Открыть проект с дашборда «Домой»: переключаем раздел на «Проекты» + открываем проект.
  // Снимок дашборда в истории не подменяем — Back с проекта вернёт на дашборд.
  const openProjectFromHome = (p: Project) => {
    localStorage.setItem(HUB_TAB_KEY, 'projects')
    setHubTab('projects')
    openProject(p)
  }
  // Явный выход из открытого проекта к списку проектов (кнопка «К проектам» в сайдбаре)
  const goToProjects = () => {
    localStorage.removeItem(OPEN_PROJECT_KEY)
    localStorage.setItem(HUB_TAB_KEY, 'projects')
    setHubTab('projects')
    navReplace({ screen: 'projects' })
    setProject(null)
  }
  // Выход со стены её собственной кнопкой (рельса стены, заглушка на узком экране):
  // возвращает В ПРОЕКТ, из которого на стену вошли, — он всё это время «спал» в
  // state и восстанавливается как при возврате из любого другого раздела. К СПИСКУ
  // проектов уводит другой жест — клик по подсвеченной пилюле «Проекты» (switchHubTab).
  // Проекта нет (пришли по диплинку #/wall) — остаётся список.
  const exitWall = () => {
    // Метку входа читаем ДО setWallActive(false): он её и стирает
    const entry = getWallEntry()
    setWallActive(false)
    // Вошли с дашборда — туда и возвращаемся: зона проектов тут ни при чём,
    // человек в ней не был
    if (entry === 'home') {
      localStorage.setItem(HUB_TAB_KEY, 'home')
      setHubTab('home')
      navPush({ screen: 'home' })
      return
    }
    localStorage.setItem(HUB_TAB_KEY, 'projects')
    setHubTab('projects')
    navPush(project ? { screen: 'project', project, view: 'sidebar', file: null } : { screen: 'projects' })
  }
  // Переключатель раздела «Чаты | Проекты». НЕ сбрасывает открытый проект — он «спит»
  // при уходе в «Чаты» и восстанавливается при возврате в «Проекты» (навигационная память).
  const switchHubTab = (t: HubTabValue) => {
    // Уход ИЗ зоны проектов (стена/воркспейс/список — все три живут на пилюле
    // «Проекты») в другой раздел — запоминаем режим: клик «Проекты» из другого
    // раздела вернёт именно туда, где были до ухода. Пишем до всех early-return
    // ниже, чтобы покрыть каждый путь ухода.
    if (t !== 'projects' && (hubTab === 'projects' || hubTab === 'wall')) {
      setWallReturn(hubTab === 'wall' ? 'wall' : project ? 'workspace' : 'list')
    }
    // Вход НА стену: запоминаем раздел-источник, чтобы «Выйти со стены» вернуло
    // именно туда (exitWall). Пишем здесь, а не в точке входа, — так покрыты все
    // пути разом: виджет дашборда, док воркспейса, AI-хаб.
    if (t === 'wall' && hubTab !== 'wall') {
      setWallEntry(hubTab === 'home' ? 'home' : 'projects')
      // Вход с дашборда оставил бы wallReturn пустым, а ветка «Проекты при активной
      // стене» ниже трактует пустоту как «вернуть на стену» — и пилюля уводила бы
      // туда человека, который в зоне проектов вообще не был. Пишем то же, что
      // записал бы уход с дашборда в любой другой раздел.
      if (hubTab === 'home') setWallReturn(project ? 'workspace' : 'list')
    }
    // Покидаем «Аналитику токенов» — чистим контекст открытия, чтобы следующий
    // вход через меню/таб открыл чистый обзор (виджет/бейдж выставят свежий ctx)
    if (hubTab === 'spend' && t !== 'spend') setSpendCtx({})
    // Уход в раздел закрывает overlay «Что нового» ЗАМЕНОЙ записи #/history, а не Back'ом:
    // history.back() асинхронен, и его popstate прилетел бы уже ПОСЛЕ смены раздела, вернув
    // hubTab на снимок, из которого overlay открывали (клик по «Персонам» кидал в «Проекты»).
    // Снимаем флаг с текущей записи — дальше switchHubTab работает от чистого снимка.
    if (historyOpen) {
      setHistoryOpen(false)
      const st = window.history.state as (NavSnapshot & { historyOverlay?: boolean }) | null
      if (st?.historyOverlay) {
        const rest: NavSnapshot & { historyOverlay?: boolean } = { ...st }
        delete rest.historyOverlay
        navReplace(rest)
      }
    }
    // Тот же приём для overlay знакомства: клик по разделу хаба закрывает его тоже
    // (HubHeader интервью зовёт onHubTab тем же switchHubTab) — навигация и есть выход.
    if (introCtx) {
      setIntroCtx(null)
      const st = window.history.state as (NavSnapshot & { introOverlay?: boolean }) | null
      if (st?.introOverlay) {
        const rest: NavSnapshot & { introOverlay?: boolean } = { ...st }
        delete rest.introOverlay
        navReplace(rest)
      }
    }
    // Клик по «Проектам» с самой стены (там эта пилюля подсвечена как активная) —
    // явный выход из режима стены к списку проектов: тот же жест, что повторный
    // клик по активному разделу с открытым проектом ниже
    if (t === 'projects' && hubTab === 'wall') {
      setWallActive(false)
      localStorage.removeItem(OPEN_PROJECT_KEY)
      localStorage.setItem(HUB_TAB_KEY, 'projects')
      setProject(null)
      setHubTab('projects')
      navPush({ screen: 'projects' })
      return
    }
    // Возврат в зону проектов из другого раздела: пока стена «активна», пилюля
    // «Проекты» возвращает в режим, где были до ухода, — на стену, если уходили
    // с неё, либо в «спящий» воркспейс/список, если уходили из них, но по пути
    // заглянули на стену (клик по проекту в доке стены уходит в воркспейс, не
    // гася режим). Явный выход из режима — «К проектам» на самой стене или
    // повторный клик по подсвеченным «Проектам» прямо со стены.
    if (t === 'projects' && hubTab !== 'wall' && isWallActive()) {
      const ret = getWallReturn()
      if (ret === 'workspace' && project) {
        localStorage.setItem(HUB_TAB_KEY, 'projects')
        setHubTab('projects')
        navPush({ screen: 'project', project, view: 'sidebar', file: null })
        return
      }
      if (ret === 'list') {
        localStorage.removeItem(OPEN_PROJECT_KEY)
        localStorage.setItem(HUB_TAB_KEY, 'projects')
        setProject(null)
        setHubTab('projects')
        navPush({ screen: 'projects' })
        return
      }
      localStorage.setItem(HUB_TAB_KEY, 'wall')
      setHubTab('wall')
      navPush({ screen: 'wall' })
      return
    }
    // Повторный клик по активному разделу «Проекты» с открытым проектом — выход к списку.
    if (t === 'projects' && hubTab === 'projects' && project) {
      localStorage.removeItem(OPEN_PROJECT_KEY)
      localStorage.setItem(HUB_TAB_KEY, 'projects')
      setProject(null)
      setHubTab('projects')
      navPush({ screen: 'projects' })
      return
    }
    localStorage.setItem(HUB_TAB_KEY, t)
    setHubTab(t)
    const moduleId = moduleIdOf(t)
    const dest: NavSnapshot = moduleId
      ? { screen: 'module', moduleId }
      : ({ screen: t === 'home' ? 'home' : t === 'chats' ? 'chats' : t === 'archive' ? 'archive' : t === 'wall' ? 'wall' : t === 'calendar' ? 'calendar' : t === 'notes' ? 'notes' : t === 'personas' ? 'personas' : t === 'knowledge' ? 'knowledge' : t === 'spend' ? 'spend' : t === 'telemetry' ? 'telemetry' : t === 'notifications' ? 'notifications' : 'projects' } as NavSnapshot)
    // Если на текущем табе открыто «глубокое» состояние (заметка/файл/задача/персона/база) — уходя,
    // сохраняем его в истории (navPush), чтобы Back вернул именно к нему. Уход С дашборда
    // «Домой» — тоже push: дашборд — хаб-центр, Back с любого раздела возвращает на него.
    // Остальные латеральные переключения табов — replace (без разрастания истории).
    const cur = getNav()
    if (cur && (cur.note || cur.file || cur.task || cur.persona || cur.knowledge)) navPush(dest)
    else if (cur?.screen === 'home' && t !== 'home') navPush(dest)
    else navReplace(dest)
  }
  // Из календаря: открыть задачу во вкладке «Задачи» её проекта.
  // Задача передаётся через sessionStorage — WorkspacePage подхватывает при монтировании.
  const openTaskInProject = (p: Project, taskId: string) => {
    sessionStorage.setItem('cc_pending_task', `${p.id}|${taskId}`)
    localStorage.setItem(HUB_TAB_KEY, 'projects')
    setHubTab('projects')
    openProject(p)
  }
  // Клик по тосту уведомления: SPA-переход по hash-диплинку без перезагрузки страницы.
  // Пишем pending в sessionStorage (тот же канал, что и диплинк при загрузке) и либо
  // переключаем экран (страница заберёт pending при монтировании), либо — если целевой
  // экран уже смонтирован — будим его событием cc-pending-task.
  const openNotificationUrl = (url: string) => {
    // Отправители шлют диплинки в двух видах: «#/notes/x» и относительный «/chats/x»
    // (без решётки) — нормализуем к hash-виду и разбираем одним parseHash
    const hashIdx = url.indexOf('#')
    const hash = hashIdx !== -1 ? url.slice(hashIdx) : (url.startsWith('/') ? '#' + url : null)
    const target = hash ? parseHash(hash) : null
    if (target?.screen === 'calendar' && target.taskId) {
      sessionStorage.setItem('cc_pending_calendar_task', target.taskId)
      if (effectiveHubTab === 'calendar') window.dispatchEvent(new Event('cc-pending-task'))
      else switchHubTab('calendar')
      return
    }
    // Диплинк на конкретный чат (#/chats/{id}) — уведомления проактивных персон.
    // Тот же канал, что и форк чата: событие cc-open-chat + localStorage для монтирования.
    if (target?.screen === 'chats' && target.chatId) {
      localStorage.setItem('cc_open_chat', target.chatId)
      if (effectiveHubTab === 'chats') {
        window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: target.chatId } }))
      } else {
        switchHubTab('chats')
      }
      return
    }
    if (target?.screen === 'project' && target.projectId && target.taskId) {
      const pid = target.projectId
      sessionStorage.setItem('cc_pending_task', `${pid}|${target.taskId}`)
      if (effectiveHubTab === 'projects' && project?.id === pid) {
        // WorkspacePage этого проекта уже на экране
        window.dispatchEvent(new Event('cc-pending-task'))
      } else if (project?.id === pid) {
        // Проект «спит» в другой вкладке — возврат в «Проекты» смонтирует WorkspacePage
        localStorage.setItem(HUB_TAB_KEY, 'projects')
        setHubTab('projects')
      } else {
        api.projects.list()
          .then(list => {
            const p = list.find(x => x.id === pid)
            if (p) {
              localStorage.setItem(HUB_TAB_KEY, 'projects')
              setHubTab('projects')
              openProject(p)
            }
          })
          .catch(() => {})
      }
      return
    }
    // Диплинк на конкретный чат внутри проекта (#/project/{id}/chat/{chatId}) —
    // уведомления проактивных персон в проектных чатах.
    if (target?.screen === 'project' && target.projectId && target.chatId) {
      const pid = target.projectId
      sessionStorage.setItem('cc_pending_project_chat', `${pid}|${target.chatId}`)
      if (effectiveHubTab === 'projects' && project?.id === pid) {
        window.dispatchEvent(new Event('cc-pending-project-chat'))
      } else if (project?.id === pid) {
        localStorage.setItem(HUB_TAB_KEY, 'projects')
        setHubTab('projects')
      } else {
        api.projects.list()
          .then(list => {
            const p = list.find(x => x.id === pid)
            if (p) {
              localStorage.setItem(HUB_TAB_KEY, 'projects')
              setHubTab('projects')
              openProject(p)
            }
          })
          .catch(() => {})
      }
      return
    }
    // Диплинк на персону внутри проекта (#/project/{id}/persona/{personaId}) — бэйдж
    // автоматизации в чате проектной персоны. Тот же канал, что у задачи (cc_pending_task).
    if (target?.screen === 'project' && target.projectId && target.personaId) {
      const pid = target.projectId
      sessionStorage.setItem('cc_pending_persona', `${pid}|${target.personaId}`)
      if (target.personaView) sessionStorage.setItem('cc_pending_persona_view', target.personaView)
      else sessionStorage.removeItem('cc_pending_persona_view')
      if (effectiveHubTab === 'projects' && project?.id === pid) {
        window.dispatchEvent(new Event('cc-pending-persona'))
      } else if (project?.id === pid) {
        localStorage.setItem(HUB_TAB_KEY, 'projects')
        setHubTab('projects')
      } else {
        api.projects.list()
          .then(list => {
            const p = list.find(x => x.id === pid)
            if (p) {
              localStorage.setItem(HUB_TAB_KEY, 'projects')
              setHubTab('projects')
              openProject(p)
            }
          })
          .catch(() => {})
      }
      return
    }
    // Диплинк на конкретную персону в глобальном разделе «Персоны» (#/personas/{id}) —
    // бэйдж автоматизации в чате глобальной персоны. Тот же канал, что у заметок ниже.
    if (target?.screen === 'personas' && target.personaId) {
      sessionStorage.setItem('cc_pending_persona_id', target.personaId)
      if (target.personaView) sessionStorage.setItem('cc_pending_persona_view', target.personaView)
      else sessionStorage.removeItem('cc_pending_persona_view')
      if (effectiveHubTab === 'personas') window.dispatchEvent(new Event('cc-open-persona'))
      else switchHubTab('personas')
      return
    }
    // Диплинк на заметку (#/notes/{id}) — бриф дня, итог сессии.
    // Тот же канал, что у «открыть в заметках» из чата: cc_pending_note_id + cc-open-note.
    if (target?.screen === 'notes' && target.noteId) {
      sessionStorage.setItem('cc_pending_note_id', target.noteId)
      if (effectiveHubTab === 'notes') window.dispatchEvent(new Event('cc-open-note'))
      else switchHubTab('notes')
      return
    }
    // Диплинк на базу знаний (#/knowledge/{id}) — событие knowledge_changed в ленте
    // активности командного центра. Канал cc_pending_knowledge + cc-open-knowledge.
    if (target?.screen === 'knowledge' && target.knowledgeId) {
      sessionStorage.setItem('cc_pending_knowledge', target.knowledgeId)
      if (effectiveHubTab === 'knowledge') window.dispatchEvent(new Event('cc-open-knowledge'))
      else switchHubTab('knowledge')
      return
    }
    // Диплинк в раздел «Телеметрия»: карточка инцидента (#/telemetry/incident/{fp}) или
    // просто вкладка «Инциденты» (#/telemetry/incidents — сводное уведомление о лавине).
    // Раздел может быть УЖЕ открыт: switchHubTab его не перемонтирует, поэтому там, где
    // соседи шлют событие, шлём и мы — иначе тап по уведомлению не делал бы ничего.
    if (target?.screen === 'telemetry' && (target.incidentFingerprint || target.telemetryIncidents)) {
      if (target.incidentFingerprint) setPendingIncident(target.incidentFingerprint)
      if (effectiveHubTab === 'telemetry') window.dispatchEvent(new Event(INCIDENT_OPEN_EVENT))
      else switchHubTab('telemetry')
      return
    }
    // Диплинк #/history — это overlay «Что нового», а не раздел: parseHash отдаёт его как
    // screen:'home' с флагом history, и без этой ветки ссылка внутри приложения молча
    // уводила на дашборд (overlay открывался только при полной загрузке страницы)
    if (target?.history) {
      window.dispatchEvent(new Event(PRODUCT_HISTORY_EVENT))
      return
    }
    // Диплинк на СПИСОК проектов (#/projects) — явный выход из открытого проекта к списку.
    // Просто switchHubTab сбрасывает проект лишь когда мы уже в разделе «Проекты»; с
    // дашборда проект бы остался и показался его воркспейс вместо списка.
    if (target?.screen === 'projects') {
      localStorage.removeItem(OPEN_PROJECT_KEY)
      localStorage.setItem(HUB_TAB_KEY, 'projects')
      setProject(null)
      setHubTab('projects')
      navPush({ screen: 'projects' })
      return
    }
    // Диплинк на раздел без глубокой цели — просто переключаемся на него
    if (target) {
      const dest: HubTabValue = target.screen === 'project' ? 'projects'
        : target.screen === 'module' ? `module:${target.moduleId ?? ''}` as HubTabValue
        : target.screen
      switchHubTab(dest)
      return
    }
    // Не диплинк (абсолютный внешний URL) — полная загрузка, как раньше
    window.location.assign(url)
  }
  // Переход из карточки инцидента в затронутый чат. Каналы разные, и подменять их
  // нельзя: раздел «Чаты» показывает ТОЛЬКО внепроектные (ChatsController.GetAll →
  // GetProjectlessChats), поэтому проектный чат, открытый через него, просто не нашёлся
  // бы в списке и экран остался бы пустым. Проектный идём открывать штатным каналом
  // диплинка #/project/{id}/chat/{chatId}.
  const openChatFromIncident = (chatId: string, projectId?: string | null) => {
    if (!projectId) {
      localStorage.setItem('cc_open_chat', chatId)
      if (effectiveHubTab === 'chats') {
        window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId } }))
      } else {
        switchHubTab('chats')
      }
      return
    }
    sessionStorage.setItem('cc_pending_project_chat', `${projectId}|${chatId}`)
    if (effectiveHubTab === 'projects' && project?.id === projectId) {
      window.dispatchEvent(new Event('cc-pending-project-chat'))
    } else if (project?.id === projectId) {
      localStorage.setItem(HUB_TAB_KEY, 'projects')
      setHubTab('projects')
    } else {
      api.projects.list()
        .then(list => {
          const p = list.find(x => x.id === projectId)
          if (p) {
            localStorage.setItem(HUB_TAB_KEY, 'projects')
            setHubTab('projects')
            openProject(p)
          }
        })
        .catch(() => {})
    }
  }
  // Открытие внепроектного чата по id: переключаем раздел на «Чаты» и кладём id в
  // cc_open_chat — ChatsPage подхватит при монтировании. Канал общий с форком чата
  // (cc-open-chat) и ArchivePage.onOpenChat: архивный чат — это тот же внепроектный,
  // раздел «Чаты» показывает его без отдельной ветки рельса.
  const openChatById = (chatId: string) => {
    localStorage.setItem('cc_open_chat', chatId)
    localStorage.setItem(HUB_TAB_KEY, 'chats')
    setHubTab('chats')
    navToSection({ screen: 'chats', chatId })
  }
  // Открыть архивный чат из ArchivePage — обёртка над openChatById, чтобы скрыть
  // деталь хранения от страницы архива.
  const openArchivedChat = (chat: Session) => openChatById(chat.id)
  // Открытие задачи по её hash-URL из любого раздела (вкладка «Задачи» персоны и т.п.) —
  // переиспуем ту же навигацию, что у кликов по уведомлениям (календарь/проект, монтированный или нет).
  // Listener ставится один раз; свежее замыкание openNotificationUrl — через ref.
  const openUrlRef = useRef(openNotificationUrl)
  useEffect(() => { openUrlRef.current = openNotificationUrl })
  useEffect(() => {
    const onOpenUrl = (e: Event) => {
      const url = (e as CustomEvent<{ url: string }>).detail?.url
      if (url) openUrlRef.current(url)
    }
    window.addEventListener('cc-open-url', onOpenUrl as EventListener)
    return () => window.removeEventListener('cc-open-url', onOpenUrl as EventListener)
  }, [])
  const logout = () => {
    localStorage.removeItem('cc_token')
    localStorage.removeItem('cc_username')
    localStorage.removeItem('cc_display_name')
    localStorage.removeItem('cc_server_url')
    localStorage.removeItem('cc_role')
    localStorage.removeItem('cc_user_id')
    localStorage.removeItem(OPEN_PROJECT_KEY)
    sessionStorage.removeItem('cc_token')
    sessionStorage.removeItem('cc_role')
    sessionStorage.removeItem('cc_user_id')
    idbClear() // чистим кэш при смене аккаунта/сервера
    clearMe()
    resetAiAwaiting() // имена ждущих чатов прежнего пользователя не живут в памяти вкладки
    // Раздел сбрасываем вместе с адресом — см. тот же комментарий в обработчике
    // cc-unauthorized: иначе следующий вход поднимет раздел прошлого пользователя
    localStorage.setItem(HUB_TAB_KEY, 'projects')
    setHubTab('projects')
    navReplace({ screen: 'projects' })
    setProject(null)
    setAuth(null)
  }

  // Намеренное падение для просмотра экрана ошибки (см. devBoom выше)
  if (boomMode) throw new Error('Демо экрана ошибки: #/boom в dev-режиме');

  // Заставка старта напоказ (см. devBoot выше). display:contents у обёртки —
  // чтобы клик-выход не добавлял лишний бокс поверх раскладки заставки
  if (bootMode) {
    return (
      <div style={{ display: 'contents' }} onClick={() => { window.location.hash = ''; }}>
        {/* С hint — в демо ждать некуда, и строку состояния надо показать */}
        <LoadingScreen hint="Проверяю вход" />
      </div>
    );
  }

  // Early-return в режиме #/ui-kit: показываем витрину раньше UpdatePrompt/authChecking.
  // В prod UiKitPage === null → ветка недостижима и вырезается компилятором.
  if (uiKitMode && UiKitPage) {
    return (
      <Suspense fallback={<LoadingScreen hint="Загружаю витрину" />}>
        <UiKitPage />
      </Suspense>
    );
  }

  // Early-return в режиме #/team-plan-sim — та же механика, что у витрины UI-кита
  if (teamPlanSimMode && TeamPlanSimPage) {
    return (
      <Suspense fallback={<div style={{ minHeight: '100vh', background: C.bgMain }} />}>
        <TeamPlanSimPage />
      </Suspense>
    );
  }

  return (
    <>
      <UpdatePrompt />
      {auth && !authChecking && <NotificationToasts onNavigate={openNotificationUrl} />}
      {authChecking
        ? <LoadingScreen hint="Проверяю вход" />
        : !auth
          ? <LoginPage onConnect={setAuth} />
          : effectiveHubTab === 'home'
            ? <HomePage auth={auth} onLogout={logout} onHubTab={switchHubTab} onOpenProject={openProjectFromHome} />
          : effectiveHubTab === 'chats'
            ? <ChatsPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
          : effectiveHubTab === 'archive'
            // Раздел «Архив» живёт в условной вкладке хаба: открывается через onHubTab('archive')
            // (HubTabs вставит вкладку в таббар, пока раздел активен). Клик по карточке архивного
            // чата идёт в openArchivedChat — общий канал с форком чата и уведомлениями.
            ? <ArchivePage auth={auth} onLogout={logout} onHubTab={switchHubTab} onOpenChat={openArchivedChat} />
          : effectiveHubTab === 'wall'
            ? <WallPage auth={auth} onLogout={logout} onHubTab={switchHubTab} onExitWall={exitWall} />
            : effectiveHubTab === 'calendar'
              ? <CalendarPage auth={auth} onLogout={logout} onHubTab={switchHubTab} onOpenTask={openTaskInProject} />
            : effectiveHubTab === 'notes'
              ? <NotesPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'personas'
              ? <PersonasPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'knowledge'
              ? <KnowledgePage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'spend'
              ? <SpendPage auth={auth} onLogout={logout} onHubTab={switchHubTab} ctx={spendCtx ?? {}} onClose={() => switchHubTab('home')} />
            : effectiveHubTab === 'telemetry'
              ? <TelemetryPage auth={auth} onLogout={logout} onHubTab={switchHubTab} onClose={() => switchHubTab('home')} onOpenChat={openChatFromIncident} />
              : effectiveHubTab === 'notifications'
                ? <NotificationsPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
              : moduleIdOf(effectiveHubTab)
                // Внешний модуль платформы (generic module-screen, ТЗ R6)
                ? <ModuleScreen key={effectiveHubTab} moduleId={moduleIdOf(effectiveHubTab)!} auth={auth} onLogout={logout} onHubTab={switchHubTab} />
              : project
                // key: прямой переход проект→проект (back/forward) обязан перемонтировать
                // WorkspacePage — иначе useState-инициализаторы не перечитают состояние
                // нового проекта и на экране остаётся чат/файл/вкладка старого
                ? <WorkspacePage key={project.id} project={project} onGoToProjects={goToProjects} onSwitchHub={switchHubTab} auth={auth} onLogout={logout} />
                : <ProjectListPage onOpen={openProject} onLogout={logout} auth={auth} onHubTab={switchHubTab} />
      }
      {auth && historyOpen && (
        <ProductHistory
          isMobile={isMobileView}
          auth={auth}
          onLogout={logout}
          onHubTab={switchHubTab}
          // Overlay вписан в history — закрытие крестиком идёт через Back, чтобы не копить
          // запись #/history (иначе следующий Back открыл бы overlay заново)
          onClose={() => {
            if ((window.history.state as { historyOverlay?: boolean } | null)?.historyOverlay) window.history.back()
            else setHistoryOpen(false)
          }}
        />
      )}
      {/* Overlay знакомства (план §4, п.4.5) — проектное только когда introCtx.projectId
          совпадает с открытым проектом: событие всегда шлётся со страницы этого же
          проекта (настройки/«Команда»), другого объекта тут взять негде. */}
      {auth && !authChecking && me.loaded && introCtx && (
        introCtx.projectId
          ? (project && project.id === introCtx.projectId && (
              <ProjectIntroChatPage project={project} auth={auth} onLogout={logout} onHubTab={switchHubTab} onDone={closeIntro} />
            ))
          : <IntroChatPage auth={auth} onLogout={logout} onHubTab={switchHubTab} onDone={closeIntro} />
      )}
      {/* В разделе «Телеметрия» iframe SigNoz занимает весь экран, и плавающая
          AI-кнопка перекрывала бы его контролы в правом нижнем углу — прячем её там */}
      {auth && !authChecking && effectiveHubTab !== 'telemetry' && <AiLauncher />}
      {auth && aiSearchOpen && <GlobalSearch onClose={() => setAiSearchOpen(false)} />}
      {/* Оверлей UI-инспектора — в общем хвосте, ПОСЛЕ ранних return'ов (#/ui-kit,
          #/boot, #/boom, #/team-plan-sim): на dev-витринах режим осознанно не работает */}
      {uiInspectorOn && <UiInspectorOverlay />}
    </>
  )
}
