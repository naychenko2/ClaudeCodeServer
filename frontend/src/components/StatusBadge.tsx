import { C } from '../lib/design'

type SessionStatus = 'starting' | 'working' | 'active' | 'waiting' | 'finished' | 'error'

const STATUS_CONFIG: Record<SessionStatus, { label: string; dot: string; text: string; bg: string }> = {
  starting: { label: 'запуск',    dot: C.info,    text: C.info,        bg: C.infoBg },
  working:  { label: 'работает',  dot: '#D97757', text: '#BE5536',     bg: '#FBECD9' },
  active:   { label: 'активна',   dot: C.success, text: C.successText, bg: C.successBg },
  waiting:  { label: 'ждёт ввода',dot: C.warning, text: C.warningText, bg: C.warningBg },
  finished: { label: 'готово',    dot: '#B0A697', text: C.textMuted,   bg: C.bgPanel },
  error:    { label: 'ошибка',    dot: C.danger,  text: C.dangerText,  bg: C.dangerBg },
}

export function StatusBadge({ status }: { status: SessionStatus }) {
  const cfg = STATUS_CONFIG[status] ?? STATUS_CONFIG.finished
  return (
    <span style={{ display:'inline-flex', alignItems:'center', gap:5, fontSize:11, fontWeight:600,
      color:cfg.text, background:cfg.bg, padding:'3px 9px', borderRadius:20 }}>
      <span style={{ width:6, height:6, borderRadius:'50%', background:cfg.dot }} />
      {cfg.label}
    </span>
  )
}
