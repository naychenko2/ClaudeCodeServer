import { useState, useEffect, useCallback } from 'react'
import { Plus, X, Terminal, Monitor, Circle, Square, Play } from 'lucide-react'
import { C, R, FONT } from '../../lib/design'
import { IconButton, Button } from '../ui'
import * as ts from '../../lib/terminalSignalr'
import { api } from '../../lib/api'

type ToolsTab = 'terminal' | 'preview'

// Состояние preview-сессии (создаётся через REST)
export interface PreviewSession {
  id: string
  command: string
  args: string
  port: number | null
  status: 'idle' | 'starting' | 'started' | 'stopped' | 'error'
}

interface Props {
  projectId: string
  activeTab: ToolsTab
  onTabChange: (t: ToolsTab) => void
  onSelectTerminal: (id: string | null) => void
  activeTerminalId: string | null
  onSelectPreview: (id: string | null) => void
  activePreviewId: string | null
  previewSessions: PreviewSession[]
  onPreviewSessionsChange: (sessions: PreviewSession[] | ((prev: PreviewSession[]) => PreviewSession[])) => void
}

export function ToolsSidebar({
  projectId, activeTab, onTabChange,
  onSelectTerminal, activeTerminalId,
  onSelectPreview, activePreviewId,
  previewSessions, onPreviewSessionsChange,
}: Props) {
  const [terminals, setTerminals] = useState<ts.TerminalInfo[]>([])
  const [creatingTerminal, setCreatingTerminal] = useState(false)

  // Refresh terminal list
  const refreshTerminals = useCallback(async () => {
    try {
      const list = await ts.listTerminals(projectId)
      setTerminals(list)
    } catch { /* ignore */ }
  }, [projectId])

  useEffect(() => {
    if (activeTab === 'terminal') refreshTerminals()
  }, [activeTab, refreshTerminals])

  // Подписка на статусы терминалов
  useEffect(() => {
    return ts.onTerminalMessage(msg => {
      if (msg.type === 'terminal_status') {
        refreshTerminals()
      }
    })
  }, [refreshTerminals])

  const handleCreateTerminal = useCallback(async () => {
    setCreatingTerminal(true)
    try {
      const t = await ts.createTerminal(projectId)
      setTerminals(prev => [...prev.filter(x => x.id !== t.id), t])
      onSelectTerminal(t.id)
    } catch { /* ignore */ }
    setCreatingTerminal(false)
  }, [projectId, onSelectTerminal])

  const handleStopTerminal = useCallback(async (id: string) => {
    await ts.stopTerminal(id)
    if (activeTerminalId === id) onSelectTerminal(null)
    refreshTerminals()
  }, [activeTerminalId, onSelectTerminal, refreshTerminals])

  const handleStartPreview = useCallback(async () => {
    const ps: PreviewSession = {
      id: 'preview-' + Date.now(),
      command: 'npm',
      args: 'run dev',
      port: null,
      status: 'starting',
    }
    onPreviewSessionsChange([...previewSessions, ps])
    onSelectPreview(ps.id)
    try {
      const result = await api.projects.previewStart(projectId, ps.command, ps.args.split(' ').filter(Boolean))
      onPreviewSessionsChange(prev => prev.map(p => p.id === ps.id ? {
        ...p, status: result.status as PreviewSession['status'],
        port: result.port ?? null,
      } : p))
    } catch {
      onPreviewSessionsChange(prev => prev.map(p => p.id === ps.id ? { ...p, status: 'error' as const } : p))
    }
  }, [projectId, onSelectPreview, previewSessions, onPreviewSessionsChange])

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgPanel }}>
      {/* Вкладки */}
      <div style={{ flexShrink: 0, padding: '10px 12px', borderBottom: `1px solid ${C.border}` }}>
        <div style={{ display: 'flex', gap: 4, background: C.bgInset, borderRadius: R.md, padding: 2 }}>
          <TabButton active={activeTab === 'terminal'} onClick={() => onTabChange('terminal')}>
            <Terminal size={14} strokeWidth={2} />
            Терминал
          </TabButton>
          <TabButton active={activeTab === 'preview'} onClick={() => onTabChange('preview')}>
            <Monitor size={14} strokeWidth={2} />
            Preview
          </TabButton>
        </div>
      </div>

      {/* Список терминалов */}
      {activeTab === 'terminal' && (
        <div style={{ flex: 1, overflowY: 'auto', padding: '8px 10px' }}>
          {terminals.map(t => (
            <div
              key={t.id}
              onClick={() => onSelectTerminal(t.id)}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
                background: activeTerminalId === t.id ? C.bgSelected : 'transparent',
                marginBottom: 4,
              }}
              onMouseEnter={e => { if (activeTerminalId !== t.id) e.currentTarget.style.background = C.bgInset }}
              onMouseLeave={e => { if (activeTerminalId !== t.id) e.currentTarget.style.background = 'transparent' }}
            >
              <Circle size={8} fill={t.status === 'running' ? C.success : C.textMuted}
                color={t.status === 'running' ? C.success : C.textMuted} />
              <span style={{ flex: 1, fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {t.name}
              </span>
              <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); handleStopTerminal(t.id) }} title="Остановить">
                <Square size={10} />
              </IconButton>
            </div>
          ))}
          <button
            onClick={handleCreateTerminal}
            disabled={creatingTerminal}
            style={{
              width: '100%', padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
              border: `1px dashed ${C.border}`, background: 'transparent',
              color: C.textSecondary, fontSize: 13,
              display: 'flex', alignItems: 'center', gap: 6, justifyContent: 'center',
              marginTop: 4,
            } as React.CSSProperties}
          >
            <Plus size={14} strokeWidth={2} />
            {creatingTerminal ? 'Создание...' : 'Новый терминал'}
          </button>
        </div>
      )}

      {/* Список preview */}
      {activeTab === 'preview' && (
        <div style={{ flex: 1, overflowY: 'auto', padding: '8px 10px' }}>
          {previews.map(p => (
            <div
              key={p.id}
              onClick={() => onSelectPreview(p.id)}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
                background: activePreviewId === p.id ? C.bgSelected : 'transparent',
                marginBottom: 4,
              }}
              onMouseEnter={e => { if (activePreviewId !== p.id) e.currentTarget.style.background = C.bgInset }}
              onMouseLeave={e => { if (activePreviewId !== p.id) e.currentTarget.style.background = 'transparent' }}
            >
              <Circle size={8} fill={p.status === 'started' ? C.success : C.textMuted}
                color={p.status === 'started' ? C.success : C.textMuted} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {p.command} {p.args}
                </div>
                {p.port && (
                  <div style={{ fontSize: 11, color: C.textMuted }}>
                    localhost:{p.port}
                  </div>
                )}
              </div>
            </div>
          ))}
          <button
            onClick={handleStartPreview}
            style={{
              width: '100%', padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
              border: `1px dashed ${C.border}`, background: 'transparent',
              color: C.textSecondary, fontSize: 13,
              display: 'flex', alignItems: 'center', gap: 6, justifyContent: 'center',
              marginTop: 4,
            } as React.CSSProperties}
          >
            <Play size={14} strokeWidth={2} />
            Запустить dev-сервер
          </button>
        </div>
      )}
    </div>
  )
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} style={{
      flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6,
      padding: '6px 10px', borderRadius: R.sm, border: 'none', cursor: 'pointer',
      fontSize: 12, fontWeight: 600,
      background: active ? C.bgWhite : 'transparent',
      color: active ? C.textHeading : C.textSecondary,
      fontFamily: FONT.sans,
    }}>
      {children}
    </button>
  )
}
