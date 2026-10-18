import { C } from '../../../lib/design';

// Глифы состояний фичи (12 px) — ОБЩИЕ: пилюля (TurnPlanPill) и выбор фичи (FeaturePicker)
// рисуют их из одного места, не дублируя. Форма дублирует цвет (макет v1/v2).
//   wait    — ромб (ждёт человека)
//   stopped — квадрат (исполнитель упал)
//   run     — кольцо (идёт)
//   closed  — круг с чертой (закрыта/смержена)
//   done    — галочка (принято/пройдено)
//   absent  — перечёркнутый круг (этапа/задачи нет в трекере)
//   loop    — петля (доработка/возврат)
//
// Пилюля для «идёт»/«закрыта» использует lucide `Route` (не эти глифы); ромб/квадрат
// (wait/stopped) — отсюда. Выбор фичи (FeaturePicker) — все пять отсюда.
export type FeatureGlyph = 'wait' | 'stopped' | 'run' | 'closed' | 'done' | 'absent' | 'loop';

export function FeatureGlyph({ kind, size = 12 }: { kind: FeatureGlyph; size?: number }) {
  const v = { width: size, height: size, viewBox: '0 0 16 16', 'aria-hidden': true as const };
  switch (kind) {
    case 'wait':
      return <svg {...v}><path d="M8 1.5 14.5 8 8 14.5 1.5 8Z" fill={C.info} /></svg>;
    case 'stopped':
      return <svg {...v}><rect x="2.5" y="2.5" width="11" height="11" rx="1.5" fill={C.danger} /></svg>;
    case 'run':
      return (
        <svg {...v}>
          <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.accent} strokeWidth="1.5" />
          <circle cx="8" cy="8" r="3.5" fill={C.accent} />
        </svg>
      );
    case 'closed':
      return (
        <svg {...v}>
          <circle cx="8" cy="8" r="6.5" fill={C.bgSelected} stroke={C.textMuted} strokeWidth="1" />
          <path d="M5 8h6" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" />
        </svg>
      );
    case 'done':
      return (
        <svg {...v}>
          {/* stroke textMuted: в тёмной теме C.bgSelected≈C.bgWhite, без обводки круг сливается (Майя M2). */}
          <circle cx="8" cy="8" r="7" fill={C.success} stroke={C.textMuted} strokeWidth="1" />
          <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    // Перечёркнутый круг: сплошной (не пунктирный) контур r 6 + косая черта — «нет в трекере».
    // Черта — C.textMuted (главный знак): C.dashed в тёмной теме почти сливается с фоном (Майя M1).
    case 'absent':
      return (
        <svg {...v}>
          <circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.5" />
          <path d="M4 12 L12 4" stroke={C.textMuted} strokeWidth="1.5" strokeLinecap="round" />
        </svg>
      );
    // Петля (макет v2): дуга-петля + стрелка, C.warning — «доработка/возврат» (Майя M5).
    case 'loop':
      return (
        <svg {...v}>
          <path d="M12.6 9.2A4.8 4.8 0 1 1 11.3 4.4" fill="none" stroke={C.warning} strokeWidth="2.2" strokeLinecap="round" />
          <path d="M9.2 2.6 12.6 3.4 11.8 6.8" fill="none" stroke={C.warning} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
  }
}
