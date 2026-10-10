// Шкала «Насколько похож» (спека character-panel-final-spec §6): три деления-ступени, не процент сходства.
// Данные — готовые деления из characterCardView; здесь только вёрстка.
import { C, FS, ProgressBar, SP } from 'aihome_shell/kit';
import type { MeterStop } from './characterCardView';
import { METER } from './characterTexts';

const GRID = { display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: SP.xs } as const;

export function LikenessMeter({ stops }: { stops: readonly [MeterStop, MeterStop, MeterStop] }) {
  return (
    <div role="group" aria-label={METER.title} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary }}>{METER.title}</span>
      <div style={GRID}>
        {stops.map((s, i) => <ProgressBar key={i} value={s.value} tone={s.tone} estimate={s.estimate} />)}
      </div>
      <div style={GRID}>
        {stops.map((s, i) => (
          <div key={i} style={{ display: 'flex', flexDirection: 'column', gap: 2, lineHeight: 1.3, fontSize: FS.xs, overflowWrap: 'anywhere' }}>
            <span style={{ fontWeight: 600, color: s.titleColor }}>{s.titleKey === 'running' ? METER.running(s.percent) : METER.stops[s.titleKey]}</span>
            <span style={{ color: C.textMuted }}>{s.hint}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
