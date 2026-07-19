import { useState, useEffect, useRef } from 'react'
import type { Project, AuthState } from './types'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { ChatsPage } from './pages/ChatsPage'
import { WorkspacePage } from './pages/WorkspacePage'
import type { HubTab } from './components/HubTabs'
import { UpdatePrompt } from './components/UpdatePrompt'
import { NotificationToasts } from './components/NotificationToasts'
import { ProductHistory } from './components/ProductHistory'
import { GlobalSearch } from './components/GlobalSearch'
import { AiLauncher } from './components/ai/AiLauncher'
import { OPEN_GLOBAL_SEARCH_EVENT } from './lib/ai/actions'
import { PRODUCT_HISTORY_EVENT, productHistorySeenKey } from './components/HubHeader'
import { initConnectivity } from './lib/offline'
import { C } from './lib/design'
import { useOnline } from './hooks/useOnline'
import { runOfflineSnapshot, syncProjectFiles, drainOfflineQueues } from './lib/sync'
import { onFilesChanged } from './lib/signalr'
import { loadWorkspaceState } from './lib/workspaceState'
import { navPush, navReplace, parseHash, getNav, type NavSnapshot } from './lib/nav'
import { api } from './lib/api'
import { idbClear } from './lib/idb'
import { setAllFlags } from './lib/featureFlags'
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

const OPEN_PROJECT_KEY = 'cc_open_project'
const HUB_TAB_KEY = 'cc_hub_tab'

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
  const [hubTab, setHubTab] = useState<HubTab>(() => {
    if (initialHash?.screen === 'home') return 'home'
    if (initialHash?.screen === 'calendar') return 'calendar'
    if (initialHash?.screen === 'chats') return 'chats'
    if (initialHash?.screen === 'notes') return 'notes'
    if (initialHash?.screen === 'personas') return 'personas'
    if (initialHash?.screen === 'knowledge') return 'knowledge'
    if (initialHash?.screen === 'notifications') return 'notifications'
    if (initialHash?.screen === 'projects' || initialHash?.screen === 'project') return 'projects'
    return 'home'
  })
  const effectiveHubTab: HubTab = hubTab

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
    const open = () => { localStorage.setItem(HUB_TAB_KEY, 'notes'); setHubTab('notes'); navReplace({ screen: 'notes' }) }
    window.addEventListener('cc-open-note', open)
    return () => window.removeEventListener('cc-open-note', open)
  }, [])

  // Форк чата от лица другой персоны (кнопка «Сменить персону» в чате) для глобальной
  // персоны: переключаемся в раздел «Чаты», где ChatsPage откроет новый чат по id.
  useEffect(() => {
    const open = (e: Event) => {
      const chatId = (e as CustomEvent<{ chatId?: string }>).detail?.chatId
      if (chatId) localStorage.setItem('cc_open_chat', chatId)
      localStorage.setItem(HUB_TAB_KEY, 'chats')
      setHubTab('chats')
      navReplace({ screen: 'chats', chatId })
    }
    window.addEventListener('cc-open-chat', open)
    return () => window.removeEventListener('cc-open-chat', open)
  }, [])

  // Диплинк #/project/{id}/chat/{chatId} при полной загрузке страницы (клик по пушу
  // из service worker). Если проект ещё не открыт — загружаем и открываем.
  useEffect(() => {
    if (!initialHash || initialHash.screen !== 'project' || !initialHash.chatId) return;
    const pid = initialHash.projectId;
    if (!pid) return;
    if (project?.id === pid) {
      // Проект уже открыт — WorkspacePage сам подхватит sessionStorage
      window.dispatchEvent(new Event('cc-pending-project-chat'));
      return;
    }
    api.projects.list()
      .then(list => {
        const p = list.find(x => x.id === pid);
        if (p) openProject(p);
      })
      .catch(() => {});
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

  const online = useOnline()
  // Текущий проект — приоритет для снапшота при выходе из офлайна (без ре-триггера при смене проекта)
  const projectIdRef = useRef<string | undefined>(undefined)
  projectIdRef.current = project?.id

  useEffect(() => { initConnectivity() }, [])

  // При наличии сохранённых credentials — немедленно зондируем сервер, чтобы _online
  // выставился правильно ещё до первого рендера страниц (navigator.onLine ≠ «сервер доступен»)
  useEffect(() => {
    if (!auth) { setAuthChecking(false); return }
    // Максимум 3 секунды на проверку доступности сервера.
    // Если не ответил — показываем приложение в текущем (возможно офлайн) состоянии.
    const timer = setTimeout(() => setAuthChecking(false), 3_000)
    api.auth.me()
      .then(me => {
        if (me?.featureFlags) setAllFlags(me.featureFlags)
        setCtxThresholdsFromServer(me?.contextThresholds)
        // Имя могли поправить в профиле после логина — подхватываем без перевхода
        const fresh = me?.displayName?.trim() || undefined
        setAuth(prev => (prev && prev.displayName !== fresh ? { ...prev, displayName: fresh } : prev))
        if (fresh) localStorage.setItem('cc_display_name', fresh)
        else localStorage.removeItem('cc_display_name')
        loadModels() // актуальный список моделей Claude (fire-and-forget, есть fallback)
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
    const seed: NavSnapshot = { screen: hubTab === 'home' ? 'home' : hubTab === 'chats' ? 'chats' : hubTab === 'calendar' ? 'calendar' : hubTab === 'notes' ? 'notes' : hubTab === 'personas' ? 'personas' : hubTab === 'knowledge' ? 'knowledge' : hubTab === 'notifications' ? 'notifications' : 'projects' }
    // Диплинк #/notes/{id}: сохраняем заметку в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'notes' && initialHash?.screen === 'notes') seed.note = initialHash.noteId ?? null
    // Диплинк #/personas/{id}: сохраняем персону в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'personas' && initialHash?.screen === 'personas') seed.persona = initialHash.personaId ?? null
    // Диплинк #/knowledge/{id}: сохраняем базу знаний в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'knowledge' && initialHash?.screen === 'knowledge') seed.knowledge = initialHash.knowledgeId ?? null
    // Диплинк #/calendar/board: сохраняем доску, чтобы URL пережил перезагрузку
    if (seed.screen === 'calendar' && initialHash?.screen === 'calendar' && initialHash.board) seed.board = true
    // Диплинк #/history: сид не должен затирать открытый overlay «Что нового» —
    // иначе адрес уезжает на #/home, а страница остаётся открытой
    if (!initialHash?.history) navReplace(seed)
    // Диплинк #/chats/{id}: сохраняем чат в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'chats' && initialHash?.screen === 'chats' && initialHash.chatId) seed.chatId = initialHash.chatId
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
      } else if (s?.screen === 'notifications') {
        // Раздел «Уведомления» — проект «спит»
        if (hubTab !== 'notifications') { localStorage.setItem(HUB_TAB_KEY, 'notifications'); setHubTab('notifications') }
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

  // Прогрев/синхронизация: при входе и при возврате в онлайн — все проекты, начиная с текущего
  useEffect(() => {
    if (auth && online) {
      // Сперва проигрываем офлайн-очереди (задачи/заметки) на сервер, затем прогреваем кэш —
      // независимо от того, какой раздел открыт (подписки в разделах заводятся при монтировании)
      void drainOfflineQueues()
      runOfflineSnapshot(projectIdRef.current)
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
  }, [auth, online, project?.id])

  // Watcher: сервер уведомил об изменении файлов проекта → инкрементальный ре-синк офлайн-кэша
  useEffect(() => onFilesChanged(({ projectId }) => { syncProjectFiles(projectId) }), [])

  // Подписка на уведомления через SignalR (даже если раздел ещё не открыт)
  useEffect(() => { ensureNotificationsSubscribed(); }, []);

  const openProject = (p: Project) => {
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
  // Переключатель раздела «Чаты | Проекты». НЕ сбрасывает открытый проект — он «спит»
  // при уходе в «Чаты» и восстанавливается при возврате в «Проекты» (навигационная память).
  const switchHubTab = (t: HubTab) => {
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
    const dest: NavSnapshot = { screen: t === 'home' ? 'home' : t === 'chats' ? 'chats' : t === 'calendar' ? 'calendar' : t === 'notes' ? 'notes' : t === 'personas' ? 'personas' : t === 'knowledge' ? 'knowledge' : t === 'notifications' ? 'notifications' : 'projects' }
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
    // Диплинк на раздел без глубокой цели — просто переключаемся на него
    if (target) {
      switchHubTab(target.screen === 'project' ? 'projects' : target.screen)
      return
    }
    // Не диплинк (абсолютный внешний URL) — полная загрузка, как раньше
    window.location.assign(url)
  }
  // Открытие задачи по её hash-URL из любого раздела (вкладка «Задачи» персоны и т.п.) —
  // переиспуем ту же навигацию, что у кликов по уведомлениям (календарь/проект, монтированный или нет).
  // Listener ставится один раз; свежее замыкание openNotificationUrl — через ref.
  const openUrlRef = useRef(openNotificationUrl)
  openUrlRef.current = openNotificationUrl
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
    navReplace({ screen: 'projects' })
    setProject(null)
    setAuth(null)
  }

  return (
    <>
      <UpdatePrompt />
      {auth && !authChecking && <NotificationToasts onNavigate={openNotificationUrl} />}
      {authChecking
        ? <div style={{ minHeight: '100vh', background: C.bgMain }} />
        : !auth
          ? <LoginPage onConnect={setAuth} />
          : effectiveHubTab === 'home'
            ? <HomePage auth={auth} onLogout={logout} onHubTab={switchHubTab} onOpenProject={openProjectFromHome} />
          : effectiveHubTab === 'chats'
            ? <ChatsPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'calendar'
              ? <CalendarPage auth={auth} onLogout={logout} onHubTab={switchHubTab} onOpenTask={openTaskInProject} />
            : effectiveHubTab === 'notes'
              ? <NotesPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'personas'
              ? <PersonasPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'knowledge'
              ? <KnowledgePage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
              : effectiveHubTab === 'notifications'
                ? <NotificationsPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
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
      {auth && !authChecking && <AiLauncher />}
      {auth && aiSearchOpen && <GlobalSearch onClose={() => setAiSearchOpen(false)} />}
    </>
  )
}
