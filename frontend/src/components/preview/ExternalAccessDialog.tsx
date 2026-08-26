import { useState } from 'react'
import { Check, Copy, TriangleAlert } from 'lucide-react'
import { C, R, SP, FS, FONT, MODAL_W } from '../../lib/design'
import { Modal, Button } from '../ui'
import type { ExternalLinkIssued } from '../../types'

// Выданная ссылка внешнего доступа: её показывают ОДИН раз в момент выдачи — токен живёт
// в самой ссылке и на сервере не хранится, повторить его нельзя. Отсюда и упор на копирование.
interface Props {
  result: ExternalLinkIssued
  serviceName: string
  onClose: () => void
}

export function ExternalAccessDialog({ result, serviceName, onClose }: Props) {
  const [copied, setCopied] = useState(false)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(result.url)
      setCopied(true)
      setTimeout(() => setCopied(false), 1800)
    } catch {
      // Буфер недоступен (нет разрешения) — ссылку всё равно видно и можно выделить руками
    }
  }

  const until = new Date(result.expiresAt).toLocaleString('ru-RU', {
    day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit',
  })

  const footer = (
    <div style={{ display: 'flex', gap: SP.sm, marginLeft: 'auto' }}>
      <Button variant="ghost" onClick={onClose}>Закрыть</Button>
      <Button variant="primary" onClick={() => void copy()}>
        {copied ? <Check size={14} /> : <Copy size={14} />}
        {copied ? 'Скопировано' : 'Скопировать ссылку'}
      </Button>
    </div>
  )

  return (
    <Modal
      width={MODAL_W.form}
      title="Доступ снаружи открыт"
      subtitle={`«${serviceName}» доступен по ссылке до ${until}`}
      footer={footer}
      onClose={onClose}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <div style={{
          padding: SP.sm,
          borderRadius: R.sm,
          background: C.bgInset,
          border: `1px solid ${C.border}`,
          fontFamily: FONT.mono,
          fontSize: FS.sm,
          color: C.textPrimary,
          wordBreak: 'break-all',
          userSelect: 'all',
        }}>
          {result.url}
        </div>

        <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
          Ссылка именная и работает, пока открыт доступ. Её можно закрыть в любой момент —
          кнопкой «Закрыть» рядом с сервисом.
        </div>

        {result.evicted.length > 0 && (
          <div style={{
            display: 'flex',
            gap: SP.sm,
            padding: SP.sm,
            borderRadius: R.sm,
            background: C.warningBg,
            color: C.warningText,
            fontSize: FS.sm,
            lineHeight: 1.5,
          }}>
            <TriangleAlert size={16} style={{ flexShrink: 0, color: C.warning, marginTop: 2 }} />
            <div>
              Открытых ссылок стало слишком много, поэтому самая старая закрылась
              {result.evicted.length > 1 ? ` (${result.evicted.length} шт.)` : ''}.
            </div>
          </div>
        )}
      </div>
    </Modal>
  )
}
