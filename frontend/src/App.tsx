import { useState, useEffect, useRef } from 'react'
import type { Project, AuthState } from './types'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { ChatsPage } from './pages/ChatsPage'
import { TeamPage } from './pages/TeamPage'
import { WorkspacePage } from './pages/WorkspacePage'
import type { HubTab } from './components/HubTabs'
import { UpdatePrompt } from './components/UpdatePrompt'
import { initConnectivity } from './lib/offline'
import { useOnline } from './hooks/useOnline'
import { runOfflineSnapshot, syncProjectFiles } from './lib/sync'
import { onFilesChanged } from './lib/signalr'
import { loadWorkspaceState } from './lib/workspaceState'
import { navPush, navReplace, type NavSnapshot } from './lib/nav'
import { api } from './lib/api'
import { idbClear } from './lib/idb'
import { setAllFlags, useFeature, FLAGS } from './lib/featureFlags'
import { setCtxThresholdsFromServer } from './lib/contextPrefs'

const OPEN_PROJECT_KEY = 'cc_open_project'
const HUB_TAB_KEY = 'cc_hub_tab'

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

  // Активная вкладка хаба (Чаты | Команда | Проекты) — вне открытого проекта.
  // По умолчанию открываются «Чаты» (первая вкладка).
  const [hubTab, setHubTab] = useState<HubTab>(() => {
    const saved = localStorage.getItem(HUB_TAB_KEY)
    return saved === 'projects' || saved === 'team' ? saved : 'chats'
  })

  // «Команда» за фич-флагом roles: если флаг выключен, а вкладка сохранена — откат на «Чаты»
  const rolesEnabled = useFeature(FLAGS.roles)
  useEffect(() => {
    if (!rolesEnabled && hubTab === 'team') {
      localStorage.setItem(HUB_TAB_KEY, 'chats')
      setHubTab('chats')
    }
  }, [rolesEnabled, hubTab])

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
    navReplace({ screen: hubTab === 'projects' ? 'projects' : hubTab })
    // Запись уровня проекта пушим только когда активен именно раздел «Проекты» с открытым
    // проектом — при hubTab==='chats' проект «спит» и в истории не отражается.
    if (hubTab === 'projects' && project) {
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
      } else if (s?.screen === 'chats' || s?.screen === 'team') {
        // Разделы «Чаты»/«Команда» — открытый проект «спит», его не сбрасываем (навигационная память)
        if (hubTab !== s.screen) { localStorage.setItem(HUB_TAB_KEY, s.screen); setHubTab(s.screen) }
      } else if (s?.screen === 'projects') {
        // Список проектов — явный выход из проекта
        if (project) { localStorage.removeItem(OPEN_PROJECT_KEY); setProject(null) }
        if (hubTab !== 'projects') { localStorage.setItem(HUB_TAB_KEY, 'projects'); setHubTab('projects') }
      }
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [project, hubTab])

  // Прогрев/синхронизация: при входе и при возврате в онлайн — все проекты, начиная с текущего
  useEffect(() => {
    if (auth && online) runOfflineSnapshot(projectIdRef.current)
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
        if (!list.some(p => p.id === project.id)) {
          localStorage.removeItem(OPEN_PROJECT_KEY)
          navReplace({ screen: 'projects' })
          setProject(null)
        }
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
  // Переключатель раздела «Чаты | Команда | Проекты». НЕ сбрасывает открытый проект — он
  // «спит» при уходе из «Проектов» и восстанавливается при возврате (навигационная память).
  const switchHubTab = (t: HubTab) => {
    localStorage.setItem(HUB_TAB_KEY, t)
    setHubTab(t)
    navReplace({ screen: t === 'projects' ? 'projects' : t })
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
      {authChecking
        ? <div style={{ minHeight: '100vh', background: '#F4F0E8' }} />
        : !auth
          ? <LoginPage onConnect={setAuth} />
          : hubTab === 'chats'
            ? <ChatsPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
            : hubTab === 'team'
              ? <TeamPage auth={auth} onLogout={logout} onHubTab={switchHubTab} />
              : project
                ? <WorkspacePage project={project} onGoToProjects={goToProjects} onSwitchHub={switchHubTab} auth={auth} onLogout={logout} />
                : <ProjectListPage onOpen={openProject} onLogout={logout} auth={auth} onHubTab={switchHubTab} />
      }
    </>
  )
}
