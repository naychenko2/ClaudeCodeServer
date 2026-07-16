import { useRef } from 'react'
import { RotateCcw, Square, Monitor, ExternalLink } from 'lucide-react'
import { C } from '../../lib/design'
import { IconButton, Button } from '../ui'
import type { ProjectService } from '../../types'

interface Props {
  service: ProjectService
  projectId: string
  onStop: (serviceId: string) => void
}

export function PreviewView({ service, projectId, onStop }: Props) {
  const iframeRef = useRef<HTMLIFrameElement>(null)
  const previewUrl = service.status === 'started' ? `/preview/${projectId}/` : null
  const port = service.runningPort ?? service.suggestedPort

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Тулбар */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8,
        padding: '8px 12px', borderBottom: `1px solid ${C.border}`,
        background: C.bgPanel,
      }}>
        <StatusDot status={service.status} />
        <span style={{ fontSize: 12, fontWeight: 600, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {service.name}
        </span>
        {port && (
          <span style={{ fontSize: 12, color: C.textMuted }}>
            localhost:{port}
          </span>
        )}
        <div style={{ flex: 1 }} />
        {previewUrl && (
          <>
            <IconButton size="xs" variant="soft" onClick={() => iframeRef.current?.contentWindow?.location.reload()} title="Обновить">
              <RotateCcw size={13} />
            </IconButton>
            <IconButton size="xs" variant="soft" onClick={() => window.open(previewUrl, '_blank', 'noopener')} title="Открыть в новой вкладке">
              <ExternalLink size={13} />
            </IconButton>
          </>
        )}
        <Button size="sm" variant="ghost" onClick={() => onStop(service.id)}>
          <Square size={12} strokeWidth={2.5} style={{ marginRight: 4 }} />
          Стоп
        </Button>
      </div>

      {/* Контент */}
      {previewUrl ? (
        <iframe ref={iframeRef} src={previewUrl}
          style={{ flex: 1, border: 'none', background: '#fff' }}
          sandbox="allow-scripts allow-same-origin allow-forms allow-popups" title="Live preview" />
      ) : (
        <div style={{
          flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
          flexDirection: 'column', gap: 12, color: C.textMuted, fontSize: 14,
        }}>
          <Monitor size={32} strokeWidth={1.5} />
          <span>
            {service.status === 'starting' ? 'Запуск…'
              : service.status === 'error' ? (service.error || 'Ошибка запуска')
              : 'Сервис не запущен'}
          </span>
        </div>
      )}
    </div>
  )
}

function StatusDot({ status }: { status: string }) {
  const color = status === 'started' ? C.success : status === 'starting' ? C.warning : status === 'error' ? C.danger : C.textMuted
  return <span style={{ width: 8, height: 8, borderRadius: '50%', background: color, flexShrink: 0 }} />
}
