import { C, FONT } from '../../../lib/design';
import type { FeatureRound } from '../../../types/featureRun';

// «Пружина» витков: каждый виток — кольцо, цвет и штрих кодируют исход
// (вернул / принял с правками / принял / идёт). Компактная альтернатива списку:
// на узкой строке конвейера видно и число витков, и их характер с одного взгляда.
//
// Кольца нарисованы inline-SVG (эллипсы внахлёст, как витки спирали), а не
// иконками lucide: нужен именно «спиральный» силуэт, готовой такой иконки нет.

// Штрих и толщина кольца по исходу витка. Экспортируется: PipelineFlow берёт
// stroke по verdict для дуги возврата (тот же кодировщик исходов, что и кольца).
// withdrawn — приглушённое сплошное кольцо (C.textMuted, штрих 1.6) БЕЗ пульса:
// «снят с выкладки» не «идёт» (running — C.textHeading + пунктир + пульс).
export const RING: Record<string, { stroke: string; width: number; dash?: string }> = {
  returned:             { stroke: C.warning, width: 2 },
  accepted_with_remarks:{ stroke: C.warning, width: 1.6, dash: '2.4 1.8' },
  accepted:             { stroke: C.success, width: 2 },
  running:              { stroke: C.textHeading, width: 1.6, dash: '2 2' },
  withdrawn:            { stroke: C.textMuted, width: 1.6 },
};

// Подпись исхода витка (для title/aria).
export function roundVerdictLabel(v: FeatureRound['verdict']): string {
  switch (v) {
    case 'returned': return 'вернул';
    case 'accepted_with_remarks': return 'принял с правками';
    case 'accepted': return 'принял';
    case 'withdrawn': return 'снят';
    default: return 'идёт';
  }
}

// caption — подписывать ли кольца строкой «N витков · ↺N» (по умолчанию да, прежнее
// поведение). Легенда рисует те же кольца БЕЗ подписи (caption={false}): только силуэт.
export function Rounds({ rounds, max = 6, caption = true }: { rounds: FeatureRound[]; max?: number; caption?: boolean }) {
  if (!rounds.length) return null;
  const shown = rounds.slice(-max);
  const extra = rounds.length - shown.length;
  const gap = 7;
  const w = 12 + (shown.length - 1) * gap;
  const rets = rounds.filter(r => r.verdict === 'returned' || r.verdict === 'accepted_with_remarks').length;
  const baseLabel = `${rounds.length} ${plural(rounds.length, 'виток', 'витка', 'витков')}`;

  return (
    <span
      className="coil"
      style={{ display: 'inline-flex', alignItems: 'center', gap: 5, verticalAlign: 'middle' }}
      aria-label={rounds.map(r => `${r.n} — ${roundVerdictLabel(r.verdict)}`).join(', ')}
    >
      {extra > 0 && (
        <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.mono }}>+{extra}</span>
      )}
      <svg width={w} height={16} viewBox={`0 0 ${w} 16`} aria-hidden="true" style={{ display: 'block', overflow: 'visible' }}>
        {shown.map((r, i) => {
          const cfg = RING[r.verdict] ?? RING.running;
          return (
            <ellipse
              key={r.n}
              cx={6 + i * gap}
              cy={8}
              rx={5}
              ry={6.4}
              fill="none"
              stroke={cfg.stroke}
              strokeWidth={cfg.width}
              strokeDasharray={cfg.dash}
              className={r.verdict === 'running' ? 'feature-pulse' : undefined}
            >
              <title>{`${r.n} — ${roundVerdictLabel(r.verdict)}`}</title>
            </ellipse>
          );
        })}
      </svg>
      {caption && (
        // ↺N — C.warningText 700 (отдельный span), остальная подпись — C.textMuted.
        <span style={{ display: 'inline-flex', alignItems: 'center', fontSize: 11, fontFamily: FONT.mono, whiteSpace: 'nowrap' }}>
          <span style={{ color: C.textMuted }}>{baseLabel}</span>
          {rets > 0 && <span style={{ color: C.warningText, fontWeight: 700 }}> · ↺{rets}</span>}
        </span>
      )}
    </span>
  );
}

// Русские формы: виток/витка/витков.
function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}
