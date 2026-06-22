import { useState, useEffect } from 'react'
import type { Project, AuthState } from './types'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { WorkspacePage } from './pages/WorkspacePage'
import { OfflineBanner } from './components/OfflineBanner'
import { SyncIndicator } from './components/SyncIndicator'
import { initConnectivity } from './lib/offline'
import { useOnline } from './hooks/useOnline'
import { runOfflineSnapshot } from './lib/sync'

export default function App() {
  const [auth, setAuth] = useState<AuthState | null>(null)
  const [project, setProject] = useState<Project | null>(null)
  const online = useOnline()

  useEffect(() => {
    initConnectivity()
    const url = localStorage.getItem('cc_server_url')
    const key = localStorage.getItem('cc_api_key')
    if (url && key) setAuth({ serverUrl: url, apiKey: key })
  }, [])

  // Прогрев офлайн-снапшота: при входе и при возврате в онлайн (после reconnect)
  useEffect(() => {
    if (auth && online) runOfflineSnapshot()
  }, [auth, online])

  return (
    <>
      <OfflineBanner />
      <SyncIndicator />
      {!auth
        ? <LoginPage onConnect={setAuth} />
        : project
          ? <WorkspacePage project={project} onBack={() => setProject(null)} />
          : <ProjectListPage onOpen={setProject} onLogout={() => { localStorage.removeItem('cc_server_url'); setAuth(null) }} />}
    </>
  )
}
