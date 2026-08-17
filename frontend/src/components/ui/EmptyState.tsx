import type { ReactNode } from 'react'
import { C, FONT, FS } from '../../lib/design'

interface EmptyStateProps {
  icon: ReactNode             // SVG или emoji
  title: string
  // Обычно строка, но допустима разметка — подсветить имя файла/команду внутри текста
  subtitle?: ReactNode
  action?: ReactNode          // кнопка или ссылка (можно ряд кнопок во flex-контейнере)
  // Компактный вид (узкие сайдбары, secondary-панели): кружок 44 в нейтральной гамме,
  // заголовок мельче. Иначе — крупный дефолтный empty (центр экрана).
  compact?: boolean
  // По контенту, а не на всю высоту: нужно, когда empty-state стоит в потоке НАД
  // другим содержимым (напр. обучающие подсказки под пустой базой знаний), а не
  // один занимает всю панель. По умолчанию тянется на height:100% и центрируется.
  inline?: boolean
}

export function EmptyState({ icon, title, subtitle, action, compact, inline }: EmptyStateProps) {
  return (
    <div style={{ display:'flex', flexDirection:'column', alignItems:'center', justifyContent:'center',
      textAlign:'center', padding: compact ? 24 : 40, gap:8, height: inline ? undefined : '100%' }}>
      <div style={{
        width: compact ? 44 : 56, height: compact ? 44 : 56, borderRadius:16,
        background: compact ? C.bgSelected : C.bgPanel, color: compact ? C.textMuted : C.accent,
        display:'flex', alignItems:'center', justifyContent:'center', marginBottom:8,
      }}>
        {icon}
      </div>
      <div style={{
        fontFamily:FONT.serif, letterSpacing:'-0.01em',
        fontWeight: compact ? 700 : 500,
        fontSize: compact ? FS.lg : 21,
        color: compact ? C.textHeading : C.textPrimary,
      }}>{title}</div>
      {subtitle && <div style={{ fontSize: compact ? FS.sm : 13.5, color:C.textSecondary, lineHeight:1.5, maxWidth:240 }}>{subtitle}</div>}
      {action && <div style={{ marginTop:12 }}>{action}</div>}
    </div>
  )
}
