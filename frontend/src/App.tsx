import { useState, useEffect, useRef } from 'react'
import type { Project, AuthState } from './types'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { WorkspacePage } from './pages/WorkspacePage'
import { initConnectivity } from './lib/offline'
import { useOnline } from './hooks/useOnline'
import { runOfflineSnapshot, syncProjectFiles } from './lib/sync'
import { onFilesChanged } from './lib/signalr'
import { loadWorkspaceState } from './lib/workspaceState'
import { navPush, navReplace, type NavSnapshot } from './lib/nav'
import { api } from './lib/api'
import { idbClear } from './lib/idb'

const OPEN_PROJECT_KEY = 'cc_open_project'

export default function App() {
  // Авторизация — из localStorage (постоянно) или sessionStorage (saveKey=false)
  const [auth, setAuth] = useState<AuthState | null>(() => {
    const token = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token')
    if (!token) return null
    const url = localStorage.getItem('cc_server_url') || window.location.origin
    const username = localStorage.getItem('cc_username') || ''
    return { serverUrl: url, token, username }
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

  const online = useOnline()
  // Текущий проект — приоритет для снапшота при выходе из офлайна (без ре-триггера при смене проекта)
  const projectIdRef = useRef<string | undefined>(undefined)
  projectIdRef.current = project?.id

  useEffect(() => { initConnectivity() }, [])

  // При наличии сохранённых credentials — немедленно зондируем сервер, чтобы _online
  // выставился правильно ещё до первого рендера страниц (navigator.onLine ≠ «сервер доступен»)
  useEffect(() => {
    if (!auth) { setAuthChecking(false); return }
    api.auth.me()
      .catch(() => { /* результат отразится в _online */ })
      .finally(() => setAuthChecking(false))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auth?.serverUrl])

  // Сервер отверг API-ключ (401) → разлогиниваем и уводим на экран входа
  useEffect(() => {
    const onUnauthorized = () => {
      localStorage.removeItem('cc_token')
      localStorage.removeItem('cc_username')
      localStorage.removeItem('cc_server_url')
      localStorage.removeItem(OPEN_PROJECT_KEY)
      sessionStorage.removeItem('cc_token')
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
    navReplace({ screen: 'projects' })
    if (project) {
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
        if (project?.id !== s.project.id) {
          localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(s.project))
          setProject(s.project)
        }
      } else if (project) {
        localStorage.removeItem(OPEN_PROJECT_KEY)
        setProject(null)
      }
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [project])

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
  const logout = () => {
    localStorage.removeItem('cc_token')
    localStorage.removeItem('cc_username')
    localStorage.removeItem('cc_server_url')
    localStorage.removeItem(OPEN_PROJECT_KEY)
    sessionStorage.removeItem('cc_token')
    idbClear() // чистим кэш при смене аккаунта/сервера
    navReplace({ screen: 'projects' })
    setProject(null)
    setAuth(null)
  }

  if (authChecking) return <div style={{ minHeight: '100vh', background: '#F4F0E8' }} />
  if (!auth) return <LoginPage onConnect={setAuth} />
  // onBack ведёт через историю — едино с кнопкой «назад» браузера
  if (project) return <WorkspacePage project={project} onBack={() => window.history.back()} />
  return <ProjectListPage onOpen={openProject} onLogout={logout} />
}
