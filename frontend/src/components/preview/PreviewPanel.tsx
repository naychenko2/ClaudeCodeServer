import { useState, useEffect, useRef, useCallback } from 'react'
import { Play, Square, RotateCcw, Terminal } from 'lucide-react'
import { C, R, FONT } from '../../lib/design'
import { Button, IconButton } from '../ui'
import { onMessage } from '../../lib/signalr'
import { api } from '../../lib/api'

interface Props {
  projectId: string
}

type PreviewStatus = 'idle' | 'starting' | 'started' | 'stopped' | 'error'

export function PreviewPanel({ projectId }: Props) {
  const [status, setStatus] = useState<PreviewStatus>('idle')
  const [port, setPort] = useState<number | null>(null)
  const iframeRef = useRef<HTMLIFrameElement>(null)
  const [command, setCommand] = useState('npm')
  const [cmdArgs, setCmdArgs] = useState('run dev')

  // Подписка на SignalR-события статуса preview
  useEffect(() => {
    return onMessage((msg: any) => {
      if (msg.type === 'preview_status') {
        setStatus(msg.status as PreviewStatus)
        if (msg.port) setPort(msg.port)
      }
    })
  }, [])

  // Проверить статус при монтировании
  useEffect(() => {
    api.projects.previewStatus(projectId)
      .then(r => {
        if (r.status === 'started' && r.port) {
          setStatus('started')
          setPort(r.port)
        }
      })
      .catch(() => {})
  }, [projectId])

  const handleStart = useCallback(async () => {
    setStatus('starting')
    try {
      const args = cmdArgs.split(' ').filter(Boolean)
      const result = await api.projects.previewStart(projectId, command, args)
      if (result.status === 'started') {
        setStatus('started')
        if (result.port) setPort(result.port)
      } else {
        setStatus('error')
      }
    } catch {
      setStatus('error')
    }
  }, [projectId, command, cmdArgs])

  const handleStop = useCallback(async () => {
    try {
      await api.projects.previewStop(projectId)
    } catch { /* ignore */ }
    setStatus('stopped')
    setPort(null)
  }, [projectId])

  const handleReload = useCallback(() => {
    iframeRef.current?.contentWindow?.location.reload()
  }, [])

  const previewUrl = port ? `/preview/${projectId}/` : null

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Тулбар */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8,
        padding: '8px 12px', borderBottom: `1px solid ${C.border}`,
        background: C.bgPanel,
      }}>
        {(status === 'idle' || status === 'stopped' || status === 'error') ? (
          <>
            <input
              value={command}
              onChange={e => setCommand(e.target.value)}
              placeholder="npm"
              style={{
                width: 100, padding: '4px 8px', borderRadius: R.sm,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                fontSize: 12, fontFamily: FONT.mono, color: C.textPrimary,
              }}
            />
            <input
              value={cmdArgs}
              onChange={e => setCmdArgs(e.target.value)}
              placeholder="run dev"
              style={{
                flex: 1, padding: '4px 8px', borderRadius: R.sm,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                fontSize: 12, fontFamily: FONT.mono, color: C.textPrimary,
              }}
            />
            <Button size="sm" onClick={handleStart} disabled={(status as string) === 'starting'}>
              <Play size={12} strokeWidth={2.5} style={{ marginRight: 4 }} />
              {(status as string) === 'starting' ? 'Запуск…' : 'Запустить'}
            </Button>
          </>
        ) : (
          <>
            <StatusBadge status={status} />
            <span style={{ fontSize: 12, color: C.textSecondary }}>
              {port ? `localhost:${port}` : ''}
            </span>
            <div style={{ flex: 1 }} />
            <IconButton size="xs" variant="soft" onClick={handleReload} title="Обновить">
              <RotateCcw size={13} />
            </IconButton>
            <Button size="sm" variant="ghost" onClick={handleStop}>
              <Square size={12} strokeWidth={2.5} style={{ marginRight: 4 }} />
              Стоп
            </Button>
          </>
        )}
      </div>

      {/* Еслирэйм или плейсхолдер */}
      {previewUrl ? (
        <iframe
          ref={iframeRef}
          src={previewUrl}
          style={{ flex: 1, border: 'none', background: '#fff' }}
          sandbox="allow-scripts allow-same-origin"
          title="Live preview"
        />
      ) : (status as string) === 'starting' ? (
        <div style={{
          flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
          flexDirection: 'column', gap: 12, color: C.textMuted, fontSize: 14,
        }}>
          <Terminal size={32} strokeWidth={1.5} />
          <span>Запуск dev-сервера…</span>
        </div>
      ) : (
        <div style={{
          flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
          flexDirection: 'column', gap: 12, color: C.textMuted, fontSize: 14,
        }}>
          <Terminal size={32} strokeWidth={1.5} />
          <span>Dev-сервер не запущен</span>
          <span style={{ fontSize: 12 }}>Укажите команду и нажмите «Запустить»</span>
        </div>
      )}
    </div>
  )
}

function StatusBadge({ status }: { status: PreviewStatus }) {
  const color = status === 'started' ? C.success
    : status === 'error' ? C.danger
    : status === 'starting' ? C.warning
    : C.textMuted
  const label = status === 'started' ? 'Работает'
    : status === 'starting' ? 'Запуск'
    : status === 'error' ? 'Ошибка'
    : 'Остановлен'
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 5,
      fontSize: 12, color, fontWeight: 500,
    }}>
      <span style={{ width: 6, height: 6, borderRadius: '50%', background: color }} />
      {label}
    </span>
  )
}
