import { useState, useCallback } from 'react'
import { Plus, Terminal, Monitor, Circle, Square, Play } from 'lucide-react'
import { C, R, FONT } from '../../lib/design'
import { IconButton } from '../ui'
import type * as ts from '../../lib/terminalSignalr'
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
  // Список терминалов и операции подняты в WorkspacePage (нужны и хедеру ToolsPane)
  terminals: ts.TerminalInfo[]
  onCreateTerminal: () => void
  onStopTerminal: (id: string) => void
  onRenameTerminal: (id: string, name: string) => void
  onSelectTerminal: (id: string | null) => void
  activeTerminalId: string | null
  onSelectPreview: (id: string | null) => void
  activePreviewId: string | null
  previewSessions: PreviewSession[]
  onPreviewSessionsChange: (sessions: PreviewSession[] | ((prev: PreviewSession[]) => PreviewSession[])) => void
  terminalBusy?: boolean
}

export function ToolsSidebar({
  projectId, activeTab, onTabChange,
  terminals, onCreateTerminal, onStopTerminal, onRenameTerminal,
  onSelectTerminal, activeTerminalId,
  onSelectPreview, activePreviewId,
  previewSessions, onPreviewSessionsChange,
  terminalBusy,
}: Props) {
  // Инлайн-переименование: id редактируемого терминала + текущее значение поля
  const [renaming, setRenaming] = useState<{ id: string; value: string } | null>(null)

  const commitRename = useCallback(() => {
    setRenaming(prev => {
      if (prev && prev.value.trim()) onRenameTerminal(prev.id, prev.value.trim())
      return null
    })
  }, [onRenameTerminal])

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
              {/* Индикатор: зелёный пульс при занятости, зелёный статика когда готов, серый когда остановлен */}
              <span style={{
                width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
                background: t.status === 'running'
                  ? (activeTerminalId === t.id && terminalBusy ? C.warning : C.success)
                  : C.textMuted,
                transition: 'background 0.2s',
              }} />
              {renaming?.id === t.id ? (
                <input
                  autoFocus
                  value={renaming.value}
                  onChange={e => setRenaming({ id: t.id, value: e.target.value })}
                  onClick={e => e.stopPropagation()}
                  onBlur={commitRename}
                  onKeyDown={e => {
                    if (e.key === 'Enter') { e.preventDefault(); commitRename() }
                    else if (e.key === 'Escape') { e.preventDefault(); setRenaming(null) }
                  }}
                  style={{
                    flex: 1, minWidth: 0, fontSize: 13, fontFamily: FONT.sans,
                    color: C.textPrimary, background: C.bgWhite,
                    border: `1px solid ${C.accent}`, borderRadius: 6, padding: '2px 6px', outline: 'none',
                  }}
                />
              ) : (
                <span
                  onDoubleClick={e => { e.stopPropagation(); setRenaming({ id: t.id, value: t.name }) }}
                  title="Двойной клик — переименовать"
                  style={{ flex: 1, fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                >
                  {t.name}
                </span>
              )}
              <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onStopTerminal(t.id) }} title="Остановить">
                <Square size={10} />
              </IconButton>
            </div>
          ))}
          <button
            onClick={onCreateTerminal}
            style={{
              width: '100%', padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
              border: `1px dashed ${C.border}`, background: 'transparent',
              color: C.textSecondary, fontSize: 13,
              display: 'flex', alignItems: 'center', gap: 6, justifyContent: 'center',
              marginTop: 4,
            } as React.CSSProperties}
          >
            <Plus size={14} strokeWidth={2} />
            Новый терминал
          </button>
        </div>
      )}

      {/* Список preview */}
      {activeTab === 'preview' && (
        <div style={{ flex: 1, overflowY: 'auto', padding: '8px 10px' }}>
          {previewSessions.map((p: PreviewSession) => (
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
