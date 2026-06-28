import { C } from '../lib/design'

type SessionStatus = 'starting' | 'working' | 'active' | 'waiting' | 'orphaned' | 'finished' | 'error'

const STATUS_CONFIG: Record<SessionStatus, { label: string; dot: string; text: string; bg: string }> = {
  starting: { label: 'запуск',     dot: C.info,      text: C.info,        bg: C.infoBg    },
  working:  { label: 'работает',   dot: C.accent,    text: C.accent,      bg: C.accentLight },
  active:   { label: 'активна',    dot: C.success,   text: C.successText, bg: C.successBg },
  waiting:  { label: 'ждёт ввода', dot: C.plan,      text: C.planText,    bg: C.planLight  },
  orphaned: { label: 'прервана',   dot: C.warning,   text: C.warningText, bg: C.warningBg  },
  finished: { label: 'готово',     dot: C.textMuted, text: C.textMuted,   bg: C.bgPanel    },
  error:    { label: 'ошибка',     dot: C.danger,    text: C.dangerText,  bg: C.dangerBg   },
}

const PULSE_STATUSES = new Set(['starting', 'working'])

export function StatusBadge({ status }: { status: SessionStatus }) {
  const cfg = STATUS_CONFIG[status] ?? STATUS_CONFIG.finished
  const pulse = PULSE_STATUSES.has(status)
  return (
    <span style={{ display:'inline-flex', alignItems:'center', gap:5, fontSize:11, fontWeight:600,
      color:cfg.text, background:cfg.bg, padding:'3px 9px', borderRadius:20 }}>
      {pulse && <style>{`@keyframes sb-pulse{0%,100%{opacity:1}50%{opacity:0.3}} .sb-pulse{animation:sb-pulse 1.2s ease-in-out infinite}`}</style>}
      <span className={pulse ? 'sb-pulse' : undefined} style={{ width:6, height:6, borderRadius:'50%', background:cfg.dot }} />
      {cfg.label}
    </span>
  )
}
