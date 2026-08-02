import { useEffect, useRef, useState } from 'react'
import { RotateCcw, Square, Monitor, ExternalLink, ScrollText, X } from 'lucide-react'
import { C, FONT } from '../../lib/design'
import { IconButton, Button, PillSwitch } from '../ui'
import { PreviewLogView, type LogSource } from './PreviewLogView'
import type { ProjectService } from '../../types'

interface Props {
  service: ProjectService
  projectId: string
  onStop: (serviceId: string) => void
  // Закрыть окно сервиса (сам сервис при этом продолжает работать)
  onClose?: () => void
  // Все сервисы проекта — из них берутся живые для режима «Все логи»
  services?: ProjectService[]
}

// Preview-прокси /preview/* аутентифицируется по cookie cc_preview (iframe и его сабресурсы
// не могут слать Authorization). Ставим её из токена сессии перед загрузкой iframe.
function ensurePreviewCookie() {
  const token = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token')
  if (!token) return
  const secure = location.protocol === 'https:' ? '; Secure' : ''
  document.cookie = `cc_preview=${token}; path=/preview; SameSite=Strict${secure}`
}

type Tab = 'preview' | 'logs'

export function PreviewView({ service, projectId, onStop, onClose, services }: Props) {
  const iframeRef = useRef<HTMLIFrameElement>(null)
  const started = service.status === 'started'
  const starting = service.status === 'starting'
  // Процесс поднят снаружи (Rider, терминал): проксировать порт можем, а останавливать
  // его и читать его вывод — нет, поэтому ни «Стоп», ни вкладки логов у него нет
  const external = service.status === 'external'
  // Логи живут вместе с процессом, поэтому вкладка есть только у живого сервиса.
  // У упавшего инстанса нет: он удалён из реестра вместе с буфером, а причина
  // осталась в service.error — её и показываем ниже отдельным блоком.
  const hasLogs = started || starting || service.status === 'partial'
  // Пока сервис поднимается, смотреть в пустой iframe незачем — интересен вывод
  const [tab, setTab] = useState<Tab>(starting ? 'logs' : 'preview')
  // Смена сервиса — заново: у нового своя вкладка по умолчанию
  useEffect(() => { setTab('preview') }, [service.id])
  const activeTab: Tab = hasLogs ? tab : 'preview'

  // Логи всех живых сервисов сразу: типовой случай — бэк и фронт, и переключаться
  // между их окнами, чтобы поймать ошибку, невозможно
  const [allLogs, setAllLogs] = useState(false)
  const all = services ?? []
  const live = all.filter(s => s.status === 'started' || s.status === 'starting')
  // У группы своего вывода нет — её лог складывается из логов участников
  const own: LogSource[] = service.members?.length
    ? all.filter(s => service.members!.includes(s.id)).map(s => ({ id: s.id, name: s.name }))
    : [{ id: service.id, name: service.name }]
  const canShowAll = live.length > own.length
  const logSources: LogSource[] = allLogs && canShowAll
    ? live.map(s => ({ id: s.id, name: s.name }))
    : own

  if (started || external) ensurePreviewCookie()
  const previewUrl = started || external ? `/preview/${projectId}/` : null
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
        {external && (
          <span style={{ fontSize: 12, color: C.info }}>запущен снаружи</span>
        )}
        <div style={{ flex: 1 }} />
        {hasLogs && (
          <PillSwitch<Tab>
            value={activeTab}
            onChange={setTab}
            compact
            options={[
              { value: 'preview', label: 'Страница', icon: <Monitor size={12} /> },
              { value: 'logs', label: 'Логи', icon: <ScrollText size={12} /> },
            ]}
          />
        )}
        {/* Без compact: он прячет подпись неактивного сегмента в пользу иконки,
            а у этих вариантов иконок нет — «Все» просто исчезала */}
        {activeTab === 'logs' && canShowAll && (
          <PillSwitch<'one' | 'all'>
            value={allLogs ? 'all' : 'one'}
            onChange={v => setAllLogs(v === 'all')}
            options={[
              { value: 'one', label: 'Этот', title: 'Логи только этого сервиса' },
              { value: 'all', label: 'Все', title: `Логи всех запущенных сервисов (${live.length})` },
            ]}
          />
        )}
        {previewUrl && activeTab === 'preview' && (
          <>
            <IconButton size="xs" variant="soft" onClick={() => iframeRef.current?.contentWindow?.location.reload()} title="Обновить">
              <RotateCcw size={13} />
            </IconButton>
            <IconButton size="xs" variant="soft" onClick={() => window.open(previewUrl, '_blank', 'noopener')} title="Открыть в новой вкладке">
              <ExternalLink size={13} />
            </IconButton>
          </>
        )}
        {!external && (
          <Button size="sm" variant="ghost" onClick={() => onStop(service.id)}>
            <Square size={12} strokeWidth={2.5} style={{ marginRight: 4 }} />
            Стоп
          </Button>
        )}
        {onClose && (
          <IconButton size="xs" variant="soft" onClick={onClose} title="Закрыть окно сервиса">
            <X size={13} />
          </IconButton>
        )}
      </div>

      {/* Контент */}
      {activeTab === 'logs' ? (
        <PreviewLogView projectId={projectId} sources={logSources} />
      ) : previewUrl ? (
        <iframe ref={iframeRef} src={previewUrl}
          style={{
            flex: 1, border: 'none',
            // Подложка чужой страницы в iframe: она рендерится как в браузере
            // и нашей темой не управляется
            // eslint-disable-next-line design/no-raw-color
            background: '#fff',
          }}
          sandbox="allow-scripts allow-same-origin allow-forms allow-popups" title="Страница сервиса" />
      ) : service.status === 'error' ? (
        <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 10, padding: 20, overflow: 'auto' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: C.danger, fontWeight: 600, fontSize: 14 }}>
            <Monitor size={18} strokeWidth={1.8} />
            Не удалось запустить сервис
          </div>
          <pre style={{
            margin: 0, flex: '0 1 auto', overflow: 'auto',
            fontFamily: FONT.mono, fontSize: 12, lineHeight: 1.5, color: C.textPrimary,
            background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: 8, padding: 12,
            whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          }}>
            {service.error || 'Ошибка запуска'}
          </pre>
        </div>
      ) : (
        <div style={{
          flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
          flexDirection: 'column', gap: 12, color: C.textMuted, fontSize: 14,
        }}>
          <Monitor size={32} strokeWidth={1.5} />
          <span>{starting ? 'Запуск…' : 'Сервис не запущен'}</span>
        </div>
      )}
    </div>
  )
}

function StatusDot({ status }: { status: string }) {
  return <span style={{ width: 8, height: 8, borderRadius: '50%', background: statusColor(status), flexShrink: 0 }} />
}

// Цвет статуса сервиса — общий для окна превью и строки в списке
export function statusColor(status: string): string {
  switch (status) {
    case 'started': return C.success
    case 'starting': return C.warning
    // Составная конфигурация, у которой поднята только часть участников
    case 'partial': return C.warning
    case 'error': return C.danger
    case 'external': return C.info
    default: return C.textMuted
  }
}
