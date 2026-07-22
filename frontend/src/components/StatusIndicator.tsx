import { useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { C, FONT, SHADOW } from '../lib/design'

type SessionStatus = 'starting' | 'working' | 'active' | 'waiting' | 'orphaned' | 'finished' | 'error'

const STATUS_CONFIG: Record<SessionStatus, { label: string; dot: string }> = {
  starting: { label: 'запуск',     dot: C.info      },
  working:  { label: 'работает',   dot: C.accent    },
  active:   { label: 'активна',    dot: C.success   },
  waiting:  { label: 'ждёт ввода', dot: C.plan      },
  orphaned: { label: 'прервана',   dot: C.warning   },
  finished: { label: 'готово',     dot: C.textMuted },
  error:    { label: 'ошибка',     dot: C.danger    },
}

// Мигают только статусы «прямо сейчас что-то происходит» — иначе список рябит
const PULSE_STATUSES = new Set<SessionStatus>(['starting', 'working', 'waiting'])

const DOT = 7
const PULSE_CSS = '@keyframes sb-pulse{0%,100%{opacity:1}50%{opacity:0.25}} .sb-pulse{animation:sb-pulse 1.2s ease-in-out infinite}'

interface Props {
  status: SessionStatus
  // Шапка тултипа — кто собеседник. Единственное место, где он назван словами:
  // в самой карточке имя и роль не показываются, чтобы не съедать строку
  title?: ReactNode
  children?: ReactNode
}

/**
 * Индикатор статуса чата: цвет = статус, подпись — в тултипе над индикатором.
 * Без детей рисует точку; с ребёнком (аватар собеседника) — кольцо вокруг него,
 * чтобы не городить рядом с лицом ещё и точку. Мигание и тултип в обоих случаях общие.
 * Тултип рендерится порталом в body: карточка чата обрезает содержимое (overflow: hidden).
 */
export function StatusIndicator({ status, title, children }: Props) {
  const cfg = STATUS_CONFIG[status] ?? STATUS_CONFIG.finished
  const pulse = PULSE_STATUSES.has(status)
  const ref = useRef<HTMLSpanElement>(null)
  // Координаты фиксируем в момент наведения — тултип живёт вне потока карточки
  const [tip, setTip] = useState<{ left: number; top: number } | null>(null)

  const bind = {
    ref,
    'aria-label': cfg.label,
    onMouseEnter: () => {
      const r = ref.current?.getBoundingClientRect()
      if (r) setTip({ left: r.left, top: r.top - 7 })
    },
    onMouseLeave: () => setTip(null),
  }

  const tooltip = tip && createPortal(
    <span style={{
      position: 'fixed', left: tip.left, top: tip.top, transform: 'translateY(-100%)', zIndex: 200,
      display: 'flex', flexDirection: 'column', gap: 3, maxWidth: 280,
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: 8,
      boxShadow: SHADOW.dropdown, padding: '6px 10px',
      fontSize: 12, fontWeight: 500, color: C.textHeading,
      fontFamily: FONT.sans, pointerEvents: 'none',
    }}>
      {title && <span style={{ fontWeight: 600 }}>{title}</span>}
      <span style={{
        display: 'inline-flex', alignItems: 'center', gap: 6, whiteSpace: 'nowrap',
        color: title ? C.textMuted : C.textHeading,
      }}>
        <span
          className={pulse ? 'sb-pulse' : undefined}
          style={{ width: DOT, height: DOT, borderRadius: '50%', background: cfg.dot, flexShrink: 0 }}
        />
        {cfg.label}
      </span>
    </span>,
    document.body,
  )

  // Кольцо вокруг аватара: мигает только оно, само лицо остаётся статичным
  if (children) {
    return (
      <>
        <span {...bind} style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
          {pulse && <style>{PULSE_CSS}</style>}
          {children}
          <span
            className={pulse ? 'sb-pulse' : undefined}
            style={{
              position: 'absolute', inset: -3, borderRadius: '50%',
              border: `2px solid ${cfg.dot}`, pointerEvents: 'none',
            }}
          />
        </span>
        {tooltip}
      </>
    )
  }

  return (
    <>
      <span {...bind} style={{ display: 'inline-flex', flexShrink: 0, width: DOT, height: DOT }}>
        {pulse && <style>{PULSE_CSS}</style>}
        <span
          className={pulse ? 'sb-pulse' : undefined}
          style={{ width: DOT, height: DOT, borderRadius: '50%', background: cfg.dot, flexShrink: 0 }}
        />
      </span>
      {tooltip}
    </>
  )
}
