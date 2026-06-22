import { useState, useEffect, useRef } from 'react'
import type { Project, AuthState } from './types'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { WorkspacePage } from './pages/WorkspacePage'
import { initConnectivity } from './lib/offline'
import { useOnline } from './hooks/useOnline'
import { runOfflineSnapshot, syncProjectFiles } from './lib/sync'
import { onFilesChanged } from './lib/signalr'

const OPEN_PROJECT_KEY = 'cc_open_project'

export default function App() {
  // Авторизация — сразу из localStorage (без вспышки логина при рефреше)
  const [auth, setAuth] = useState<AuthState | null>(() => {
    const url = localStorage.getItem('cc_server_url')
    const key = localStorage.getItem('cc_api_key')
    return url && key ? { serverUrl: url, apiKey: key } : null
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

  // Прогрев/синхронизация: при входе и при возврате в онлайн — все проекты, начиная с текущего
  useEffect(() => {
    if (auth && online) runOfflineSnapshot(projectIdRef.current)
  }, [auth, online])

  // Watcher: сервер уведомил об изменении файлов проекта → инкрементальный ре-синк офлайн-кэша
  useEffect(() => onFilesChanged(({ projectId }) => { syncProjectFiles(projectId) }), [])

  const openProject = (p: Project) => {
    localStorage.setItem(OPEN_PROJECT_KEY, JSON.stringify(p))
    setProject(p)
  }
  const closeProject = () => {
    localStorage.removeItem(OPEN_PROJECT_KEY)
    setProject(null)
  }
  const logout = () => {
    localStorage.removeItem('cc_server_url')
    closeProject()
    setAuth(null)
  }

  if (!auth) return <LoginPage onConnect={setAuth} />
  if (project) return <WorkspacePage project={project} onBack={closeProject} />
  return <ProjectListPage onOpen={openProject} onLogout={logout} />
}
