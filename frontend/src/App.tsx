import { useState, useEffect } from 'react'
import type { Project, AuthState } from './types'
import { LoginPage } from './pages/LoginPage'
import { ProjectListPage } from './pages/ProjectListPage'
import { WorkspacePage } from './pages/WorkspacePage'

export default function App() {
  const [auth, setAuth] = useState<AuthState | null>(null)
  const [project, setProject] = useState<Project | null>(null)

  useEffect(() => {
    const url = localStorage.getItem('cc_server_url')
    const key = localStorage.getItem('cc_api_key')
    if (url && key) setAuth({ serverUrl: url, apiKey: key })
  }, [])

  if (!auth) return <LoginPage onConnect={setAuth} />
  if (project) return <WorkspacePage project={project} onBack={() => setProject(null)} />
  return <ProjectListPage onOpen={setProject} onLogout={() => { localStorage.removeItem('cc_server_url'); setAuth(null) }} />
}
