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
import { loadModels } from './lib/models'
import { CalendarPage } from './features/tasks/CalendarPage'
import { NotesPage } from './features/notes/NotesPage'
import { PersonasPage } from './features/personas/PersonasPage'

const OPEN_PROJECT_KEY = 'cc_open_project'
const HUB_TAB_KEY = 'cc_hub_tab'

// Диплинк из hash-URL (#/calendar, #/project/{id}/task/{tid}…) — читаем один раз
// при загрузке страницы, до первого рендера (WorkspacePage заберёт pending-значения)
const initialHash = parseHash()
if (initialHash?.screen === 'project' && initialHash.projectId) {
  // Формат «projectId|taskId» — WorkspacePage чужого проекта не заберёт значение
  if (initialHash.taskId) sessionStorage.setItem('cc_pending_task', `${initialHash.projectId}|${initialHash.taskId}`)
  if (initialHash.file) sessionStorage.setItem('cc_pending_file', `${initialHash.projectId}|${initialHash.file}`)
}
// Диплинк #/calendar/task/{id} — личная задача, модал деталей поверх календаря
if (initialHash?.screen === 'calendar' && initialHash.taskId) {
  sessionStorage.setItem('cc_pending_calendar_task', initialHash.taskId)
}

export default function App() {
  // Авторизация — из localStorage (постоянно) или sessionStorage (saveKey=false)
  const [auth, setAuth] = useState<AuthState | null>(() => {
    const token = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token')
    if (!token) return null
    const url = localStorage.getItem('cc_server_url') || window.location.origin
    const username = localStorage.getItem('cc_username') || ''
    const role = localStorage.getItem('cc_role') || sessionStorage.getItem('cc_role') || undefined
    const id = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id') || undefined
    return { serverUrl: url, token, username, role, id }
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

  // Активная вкладка хаба (Чаты | Проекты | Календарь) — вне открытого проекта.
  // По умолчанию открываются «Чаты» (первая вкладка).
  const [hubTab, setHubTab] = useState<HubTab>(() => {
    // Hash-диплинк приоритетнее сохранённой вкладки
    if (initialHash?.screen === 'calendar') return 'calendar'
    if (initialHash?.screen === 'chats') return 'chats'
    if (initialHash?.screen === 'notes') return 'notes'
    if (initialHash?.screen === 'personas') return 'personas'
    if (initialHash?.screen === 'projects' || initialHash?.screen === 'project') return 'projects'
    const saved = localStorage.getItem(HUB_TAB_KEY)
    // Сохранённое 'agents' — ключ до переименования раздела в «Персоны»
    if (saved === 'agents') return 'personas'
    return saved === 'projects' || saved === 'calendar' || saved === 'notes' || saved === 'personas' ? saved : 'chats'
  })
  const effectiveHubTab: HubTab = hubTab

  // «Что нового» — продуктовая история по всем проектам. Overlay на верхнем уровне,
  // открывается из HubHeader (событие) из любого раздела.
  const [historyOpen, setHistoryOpen] = useState(false)
  useEffect(() => {
    const open = () => {
      setHistoryOpen(true)
      // Фиксируем момент просмотра — от него отсчитывается бейдж новых изменений.
      // Ключ per-user (актуальный id на момент открытия), чтобы на одном устройстве
      // у разных аккаунтов была своя отметка.
      try {
        const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id') || undefined
        localStorage.setItem(productHistorySeenKey(uid), new Date().toISOString())
      } catch { /* ignore */ }
    }
    window.addEventListener(PRODUCT_HISTORY_EVENT, open)
    return () => window.removeEventListener(PRODUCT_HISTORY_EVENT, open)
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
  const isMobileView = typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches

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
    const seed: NavSnapshot = { screen: hubTab === 'chats' ? 'chats' : hubTab === 'calendar' ? 'calendar' : hubTab === 'notes' ? 'notes' : hubTab === 'personas' ? 'personas' : 'projects' }
    // Диплинк #/notes/{id}: сохраняем заметку в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'notes' && initialHash?.screen === 'notes') seed.note = initialHash.noteId ?? null
    // Диплинк #/personas/{id}: сохраняем персону в снимок, иначе сид затрёт id в URL
    if (seed.screen === 'personas' && initialHash?.screen === 'personas') seed.persona = initialHash.personaId ?? null
    // Диплинк #/calendar/board: сохраняем доску, чтобы URL пережил перезагрузку
    if (seed.screen === 'calendar' && initialHash?.screen === 'calendar' && initialHash.board) seed.board = true
    navReplace(seed)
    // Запись уровня проекта пушим только когда активен именно раздел «Проекты» с открытым
    // проектом — при hubTab==='chats' проект «спит» и в истории не отражается.
    // Если hash-диплинк указывает на ДРУГОЙ проект — восстановленный не пушим,
    // его откроет эффект диплинка (иначе гонка перетирает URL).
    const hashOtherProject = initialHash?.screen === 'project'
      && !!initialHash.projectId && initialHash.projectId !== project?.id
    if (hubTab === 'projects' && project && !hashOtherProject) {
      navPush({ screen: 'project', project, view: 'sidebar', file: null })
      const ws = loadWorkspaceState(project.id)
      if (ws?.openFile) navPush({ screen: 'project', project, view: 'sidebar', file: ws.openFile })
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
          navPush({ screen: 'project', project: p, view: 'sidebar', file: null })
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

  const openProject = (p: Project) => {
    localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(p))
    navPush({ screen: 'project', project: p, view: 'sidebar', file: null })
    setProject(p)
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
    const dest: NavSnapshot = { screen: t === 'chats' ? 'chats' : t === 'calendar' ? 'calendar' : t === 'notes' ? 'notes' : t === 'personas' ? 'personas' : 'projects' }
    // Если на текущем табе открыто «глубокое» состояние (заметка/файл/задача/персона) — уходя,
    // сохраняем его в истории (navPush), чтобы Back вернул именно к нему. Иначе латеральное
    // переключение табов — replace (без разрастания истории).
    const cur = getNav()
    if (cur && (cur.note || cur.file || cur.task || cur.persona)) navPush(dest)
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
    const hashIdx = url.indexOf('#')
    const target = hashIdx === -1 ? null : parseHash(url.slice(hashIdx))
    if (target?.screen === 'calendar' && target.taskId) {
      sessionStorage.setItem('cc_pending_calendar_task', target.taskId)
      if (effectiveHubTab === 'calendar') window.dispatchEvent(new Event('cc-pending-task'))
      else switchHubTab('calendar')
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
    // Не задачный диплинк — фолбэк на полную загрузку, как раньше
    window.location.assign(url)
  }
  const logout = () => {
    localStorage.removeItem('cc_token')
    localStorage.removeItem('cc_username')
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
          : effectiveHubTab === 'chats'
            ? <ChatsPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'calendar'
              ? <CalendarPage auth={auth} onLogout={logout} onHubTab={switchHubTab} onOpenTask={openTaskInProject} />
            : effectiveHubTab === 'notes'
              ? <NotesPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : effectiveHubTab === 'personas'
              ? <PersonasPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
              : project
                ? <WorkspacePage project={project} onGoToProjects={goToProjects} onSwitchHub={switchHubTab} auth={auth} onLogout={logout} />
                : <ProjectListPage onOpen={openProject} onLogout={logout} auth={auth} onHubTab={switchHubTab} />
      }
      {auth && historyOpen && (
        <ProductHistory isMobile={isMobileView} onClose={() => setHistoryOpen(false)} />
      )}
    </>
  )
}
