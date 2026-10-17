import { C, FONT } from '../../../lib/design';
import type { FeatureStep } from '../../../types/featureRun';
import { Rounds } from './Rounds';

// Шаги производства, сгруппированные по линиям (бэк/фронт/тест).
// Каждый шаг: глифа состояния + код + название + «пружина» витков.
//
// Глифы нарисованы inline-SVG: нужны четыре состояния одного размера (идёт /
// ждёт ревью / готов / снят), готовой такой четверки в наборе lucide нет.

function StepGlyph({ status }: { status: FeatureStep['status'] }) {
  const common = { width: 14, height: 14, viewBox: '0 0 16 16', 'aria-hidden': true as const };
  switch (status) {
    case 'run':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.5" className="feature-pulse" />
          <circle cx="8" cy="8" r="3.5" fill={C.textHeading} />
        </svg>
      );
    case 'await':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.5" />
          <path d="M8 4.5V8l2.3 1.6" fill="none" stroke={C.textHeading} strokeWidth="1.5" strokeLinecap="round" />
        </svg>
      );
    case 'done':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="7" fill={C.success} />
          <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.onAccent} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    default: // todo
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.5" strokeDasharray="2.5 2" />
        </svg>
      );
  }
}

// Подпись состояния шага (для строки-подсказки).
function stepStatusText(s: FeatureStep): string {
  if (s.status === 'run') return 'идёт';
  if (s.status === 'await') return 'ждёт ревью';
  if (s.status === 'done') return s.closed ?? 'принят';
  return 'ещё не начат';
}

export function FeatureSteps({ steps, lanes }: { steps: FeatureStep[]; lanes: { key: string; label: string }[] }) {
  if (!steps.length) {
    return (
      <div style={{
        fontSize: 12.5, color: C.textSecondary, background: C.bgCard,
        border: `1px dashed ${C.dashed}`, borderRadius: 10, padding: '10px 12px',
      }}>
        Шагов в трекере нет
      </div>
    );
  }
  // Порядок линий — как в макете: по порядку, в котором они встречены.
  const laneOrder = lanes.length ? lanes : [...new Set(steps.map(s => s.lane).filter(Boolean))].map(l => ({ key: l as string, label: l as string }));
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {laneOrder.map(lane => {
        const list = steps.filter(s => s.lane === lane.key);
        if (!list.length) return null;
        return (
          <div key={lane.key}>
            <div style={{ fontSize: 11, color: C.textMuted, margin: '4px 0 0' }}>{lane.label}</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              {list.map(s => (
                <div
                  key={s.id}
                  style={{
                    display: 'grid', gridTemplateColumns: '14px 1fr auto', gap: 7, alignItems: 'start',
                    padding: '4px 4px', borderRadius: 8, fontSize: 12, color: C.textPrimary,
                  }}
                >
                  <span style={{ display: 'flex', marginTop: 1 }}><StepGlyph status={s.status} /></span>
                  <span style={{ minWidth: 0 }}>
                    <span style={{ fontFamily: FONT.mono, color: C.textMuted }}>{s.id}</span>{' '}
                    <span style={{ color: s.status === 'todo' ? C.textMuted : C.textPrimary }}>{s.name}</span>
                    <span style={{ display: 'block', fontSize: 11, color: C.textMuted, marginTop: 1 }}>
                      {stepStatusText(s)}
                    </span>
                  </span>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 4, paddingTop: 1 }}>
                    {s.rounds.length > 0 ? <Rounds rounds={s.rounds} /> : null}
                  </span>
                </div>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}
