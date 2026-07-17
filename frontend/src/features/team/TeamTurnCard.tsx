import { C, R } from '../../lib/design';
import { teamMechanic, type TeamTurnInfo } from './teamMechanics';

// Красивое представление командного хода механики вместо сырого JSON/слэш-команды:
// иконка + название механики, тема отдельной строкой, параметры компактными чипами.
// Сырой текст остаётся в истории (его читает модель) — это только слой отображения.
export function TeamTurnRequest({ info, ultra }: { info: TeamTurnInfo; ultra?: boolean }) {
  const m = teamMechanic(info.id);
  const Icon = m.icon;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6, minWidth: 160 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, flexWrap: 'wrap' }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: C.accent, fontWeight: 700, fontSize: 13 }}>
          <Icon size={15} strokeWidth={2} style={{ flexShrink: 0 }} />
          {m.name}
        </span>
        {ultra && (
          <span style={{
            display: 'inline-flex', alignItems: 'center', gap: 3,
            background: C.accent, color: C.onAccent, borderRadius: R.pill,
            padding: '2px 8px', fontSize: 9.5, fontWeight: 700,
            letterSpacing: 0.6, textTransform: 'uppercase',
          }}>
            ⚡ ультра
          </span>
        )}
      </div>
      {info.topic && (
        <div style={{ fontSize: 14, color: C.textHeading, lineHeight: 1.4 }}>{info.topic}</div>
      )}
      {info.chips.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {info.chips.map((c, i) => (
            <span key={i} style={{
              background: C.accentLight, color: C.accent, borderRadius: R.max,
              padding: '1px 8px', fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap',
            }}>
              {c}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
