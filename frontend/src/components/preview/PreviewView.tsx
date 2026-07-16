import { useRef } from 'react'
import { RotateCcw, Square, Terminal } from 'lucide-react'
import { C, R, FONT } from '../../lib/design'
import { IconButton, Button } from '../ui'
import type { PreviewSession } from '../tools/ToolsSidebar'

interface Props {
  preview: PreviewSession
  projectId: string
  onStop: (id: string) => void
}

export function PreviewView({ preview, projectId, onStop }: Props) {
  const iframeRef = useRef<HTMLIFrameElement>(null)
  const previewUrl = preview.port ? `/preview/${projectId}/` : null

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Тулбар */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8,
        padding: '8px 12px', borderBottom: `1px solid ${C.border}`,
        background: C.bgPanel,
      }}>
        <StatusDot status={preview.status} />
        <span style={{ fontSize: 12, fontWeight: 600, color: C.textPrimary }}>
          {preview.command} {preview.args}
        </span>
        {preview.port && (
          <span style={{ fontSize: 12, color: C.textMuted }}>
            localhost:{preview.port}
          </span>
        )}
        <div style={{ flex: 1 }} />
        {previewUrl && (
          <IconButton size="xs" variant="soft" onClick={() => iframeRef.current?.contentWindow?.location.reload()} title="Обновить">
            <RotateCcw size={13} />
          </IconButton>
        )}
        <Button size="sm" variant="ghost" onClick={() => onStop(preview.id)}>
          <Square size={12} strokeWidth={2.5} style={{ marginRight: 4 }} />
          Стоп
        </Button>
      </div>

      {/* Контент */}
      {previewUrl ? (
        <iframe ref={iframeRef} src={previewUrl}
          style={{ flex: 1, border: 'none', background: '#fff' }}
          sandbox="allow-scripts allow-same-origin" title="Live preview" />
      ) : (
        <div style={{
          flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
          flexDirection: 'column', gap: 12, color: C.textMuted, fontSize: 14,
        }}>
          <Terminal size={32} strokeWidth={1.5} />
          <span>{preview.status === 'starting' ? 'Запуск...' : preview.status === 'error' ? 'Ошибка запуска' : 'Dev-сервер не запущен'}</span>
        </div>
      )}
    </div>
  )
}

function StatusDot({ status }: { status: string }) {
  const color = status === 'started' ? C.success : status === 'starting' ? C.warning : status === 'error' ? C.danger : C.textMuted
  return <span style={{ width: 8, height: 8, borderRadius: '50%', background: color, flexShrink: 0 }} />
}
