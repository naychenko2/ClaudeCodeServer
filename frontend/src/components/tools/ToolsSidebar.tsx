import { useState, useEffect, useCallback } from 'react'
import { Plus, Terminal, Monitor, Circle, Square, Play, RefreshCw, X } from 'lucide-react'
import { C, R, FONT } from '../../lib/design'
import { IconButton } from '../ui'
import type * as ts from '../../lib/terminalSignalr'
import { api } from '../../lib/api'
import type { ProjectService, LaunchConfigEntry } from '../../types'

type ToolsTab = 'terminal' | 'preview'

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
  activePreviewId: string | null
  previewServices: ProjectService[]
  onRefreshServices: () => void
  onStartService: (svc: ProjectService) => void
  onStopService: (serviceId: string) => void
  onSelectPreview: (serviceId: string) => void
  terminalBusy?: boolean
}

// Русская метка и порядок групп источников
const SOURCE_META: Record<string, { label: string; order: number }> = {
  'launch.json': { label: 'Сохранённые', order: 0 },
  'npm': { label: 'Node', order: 1 },
  'dotnet': { label: '.NET', order: 2 },
  'docker-compose': { label: 'Docker', order: 3 },
  'procfile': { label: 'Procfile', order: 4 },
  'makefile': { label: 'Makefile', order: 5 },
  'custom': { label: 'Прочее', order: 6 },
}
const sourceMeta = (s: string) => SOURCE_META[s] ?? { label: s, order: 9 }

export function ToolsSidebar({
  projectId, activeTab, onTabChange,
  terminals, onCreateTerminal, onStopTerminal, onRenameTerminal,
  onSelectTerminal, activeTerminalId,
  activePreviewId, previewServices,
  onRefreshServices, onStartService, onStopService, onSelectPreview,
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

  // Preview: подгрузка списка сервисов при открытии вкладки
  useEffect(() => {
    if (activeTab === 'preview') onRefreshServices()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, projectId])

  // Группировка сервисов по источнику
  const groups = (() => {
    const map = new Map<string, ProjectService[]>()
    for (const s of previewServices) {
      const arr = map.get(s.source) ?? []
      arr.push(s)
      map.set(s.source, arr)
    }
    return [...map.entries()].sort((a, b) => sourceMeta(a[0]).order - sourceMeta(b[0]).order)
  })()

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
            style={dashedButtonStyle}
          >
            <Plus size={14} strokeWidth={2} />
            Новый терминал
          </button>
        </div>
      )}

      {/* Список сервисов Preview */}
      {activeTab === 'preview' && (
        <PreviewServiceList
          projectId={projectId}
          groups={groups}
          hasAny={previewServices.length > 0}
          activePreviewId={activePreviewId}
          onRefreshServices={onRefreshServices}
          onStartService={onStartService}
          onStopService={onStopService}
          onSelectPreview={onSelectPreview}
        />
      )}
    </div>
  )
}

function PreviewServiceList({
  projectId, groups, hasAny, activePreviewId,
  onRefreshServices, onStartService, onStopService, onSelectPreview,
}: {
  projectId: string
  groups: [string, ProjectService[]][]
  hasAny: boolean
  activePreviewId: string | null
  onRefreshServices: () => void
  onStartService: (svc: ProjectService) => void
  onStopService: (serviceId: string) => void
  onSelectPreview: (serviceId: string) => void
}) {
  const [adding, setAdding] = useState(false)
  const [form, setForm] = useState({ name: '', command: 'npm', args: 'run dev', port: '' })
  const [saving, setSaving] = useState(false)

  const saveCustom = useCallback(async () => {
    const command = form.command.trim()
    if (!command) return
    setSaving(true)
    try {
      const cur = await api.projects.getLaunchConfig(projectId)
      const entry: LaunchConfigEntry = {
        name: form.name.trim() || command,
        runtimeExecutable: command,
        runtimeArgs: form.args.split(' ').filter(Boolean),
        port: form.port.trim() ? Number(form.port) : undefined,
      }
      await api.projects.putLaunchConfig(projectId, [...cur.configurations, entry])
      setAdding(false)
      setForm({ name: '', command: 'npm', args: 'run dev', port: '' })
      onRefreshServices()
    } catch { /* ignore */ }
    setSaving(false)
  }, [projectId, form, onRefreshServices])

  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 10px' }}>
      {/* Панель действий */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '2px 4px 8px' }}>
        <span style={{ fontSize: 11, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
          Сервисы
        </span>
        <IconButton size="xs" variant="soft" onClick={onRefreshServices} title="Обновить список">
          <RefreshCw size={12} />
        </IconButton>
      </div>

      {!hasAny && !adding && (
        <div style={{ padding: '12px 8px', fontSize: 12, color: C.textMuted, lineHeight: 1.5 }}>
          Запускаемые сервисы не найдены. Добавьте свой запуск ниже —
          он сохранится в <code style={{ fontFamily: FONT.mono, fontSize: 11 }}>.claude/launch.json</code>.
        </div>
      )}

      {groups.map(([source, items]) => (
        <div key={source} style={{ marginBottom: 8 }}>
          <div style={{ fontSize: 10, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, padding: '4px 6px' }}>
            {sourceMeta(source).label}
          </div>
          {items.map(svc => (
            <ServiceRow
              key={svc.id}
              svc={svc}
              active={activePreviewId === svc.id}
              onStart={() => onStartService(svc)}
              onStop={() => onStopService(svc.id)}
              onSelect={() => onSelectPreview(svc.id)}
            />
          ))}
        </div>
      ))}

      {/* Добавить свой сервис */}
      {adding ? (
        <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, padding: 10, marginTop: 4, background: C.bgWhite }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8 }}>
            <span style={{ fontSize: 12, fontWeight: 600, color: C.textPrimary }}>Свой запуск</span>
            <IconButton size="xs" variant="soft" onClick={() => setAdding(false)} title="Отмена">
              <X size={12} />
            </IconButton>
          </div>
          <FormInput placeholder="Название (необязательно)" value={form.name} onChange={v => setForm(f => ({ ...f, name: v }))} />
          <FormInput placeholder="Команда (напр. npm)" value={form.command} onChange={v => setForm(f => ({ ...f, command: v }))} />
          <FormInput placeholder="Аргументы (напр. run dev)" value={form.args} onChange={v => setForm(f => ({ ...f, args: v }))} />
          <FormInput placeholder="Порт (необязательно)" value={form.port} onChange={v => setForm(f => ({ ...f, port: v }))} />
          <button
            onClick={saveCustom}
            disabled={saving || !form.command.trim()}
            style={{
              width: '100%', marginTop: 6, padding: '7px 10px', borderRadius: R.sm,
              border: 'none', cursor: 'pointer', fontSize: 12, fontWeight: 600,
              background: C.accent, color: '#fff', opacity: saving || !form.command.trim() ? 0.6 : 1,
            }}
          >
            {saving ? 'Сохранение…' : 'Сохранить в launch.json'}
          </button>
        </div>
      ) : (
        <button onClick={() => setAdding(true)} style={dashedButtonStyle}>
          <Plus size={14} strokeWidth={2} />
          Добавить свой…
        </button>
      )}
    </div>
  )
}

function ServiceRow({ svc, active, onStart, onStop, onSelect }: {
  svc: ProjectService
  active: boolean
  onStart: () => void
  onStop: () => void
  onSelect: () => void
}) {
  const running = svc.status === 'started' || svc.status === 'starting'
  const port = svc.runningPort ?? svc.suggestedPort
  const cmd = svc.command ? `${svc.command} ${svc.args.join(' ')}`.trim() : svc.name
  const dotColor = svc.status === 'started' ? C.success
    : svc.status === 'starting' ? C.warning
    : svc.status === 'error' ? C.danger
    : C.textMuted

  return (
    <div
      onClick={() => { if (running) onSelect() }}
      title={svc.error ?? undefined}
      style={{
        display: 'flex', alignItems: 'center', gap: 8,
        padding: '7px 8px', borderRadius: R.md,
        cursor: running ? 'pointer' : 'default',
        background: active ? C.bgSelected : 'transparent',
        marginBottom: 2,
      }}
      onMouseEnter={e => { if (!active) e.currentTarget.style.background = C.bgInset }}
      onMouseLeave={e => { if (!active) e.currentTarget.style.background = 'transparent' }}
    >
      <Circle size={8} fill={dotColor} color={dotColor} style={{ flexShrink: 0 }} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {svc.name}
        </div>
        <div style={{ fontSize: 11, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: FONT.mono }}>
          {cmd}{port ? `  ·  :${port}` : ''}
        </div>
      </div>
      {running ? (
        <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onStop() }} title="Остановить">
          <Square size={10} />
        </IconButton>
      ) : (
        <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onStart() }} title="Запустить">
          <Play size={12} />
        </IconButton>
      )}
    </div>
  )
}

function FormInput({ placeholder, value, onChange }: { placeholder: string; value: string; onChange: (v: string) => void }) {
  return (
    <input
      value={value}
      placeholder={placeholder}
      onChange={e => onChange(e.target.value)}
      style={{
        width: '100%', boxSizing: 'border-box', marginBottom: 6,
        padding: '6px 8px', borderRadius: R.sm, border: `1px solid ${C.border}`,
        fontSize: 12, color: C.textPrimary, background: C.bgMain, fontFamily: FONT.sans,
      }}
    />
  )
}

const dashedButtonStyle: React.CSSProperties = {
  width: '100%', padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
  border: `1px dashed ${C.border}`, background: 'transparent',
  color: C.textSecondary, fontSize: 13,
  display: 'flex', alignItems: 'center', gap: 6, justifyContent: 'center',
  marginTop: 4,
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
