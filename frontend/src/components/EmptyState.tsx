import { C, FONT } from '../lib/design'

interface EmptyStateProps {
  icon: React.ReactNode       // SVG или emoji
  title: string
  subtitle?: string
  action?: React.ReactNode    // кнопка или ссылка
}

export function EmptyState({ icon, title, subtitle, action }: EmptyStateProps) {
  return (
    <div style={{ display:'flex', flexDirection:'column', alignItems:'center', justifyContent:'center',
      textAlign:'center', padding:40, gap:8, height:'100%' }}>
      <div style={{ width:56, height:56, borderRadius:16, background:C.bgPanel, color:C.accent,
        display:'flex', alignItems:'center', justifyContent:'center', marginBottom:8 }}>
        {icon}
      </div>
      <div style={{ fontFamily:FONT.serif, fontWeight:500, fontSize:21, color:C.textPrimary, letterSpacing:'-0.01em' }}>{title}</div>
      {subtitle && <div style={{ fontSize:13.5, color:C.textSecondary, lineHeight:1.5, maxWidth:240 }}>{subtitle}</div>}
      {action && <div style={{ marginTop:12 }}>{action}</div>}
    </div>
  )
}
