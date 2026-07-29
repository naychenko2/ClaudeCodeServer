import { C, R } from '../../lib/design';
import { teamMechanic, type TeamMechanicId } from './teamMechanics';

// Бейдж командной механики: иконка + короткое имя (словарь shortName в teamMechanics.ts),
// полное название — в title. Используется на карточках списка чатов и в шапке (sm),
// на сообщении в ленте (md) — там короткое имя нужно тем же тесным местам без ребуса.
export function TeamMechanicBadge({ id, size = 'md' }: { id: TeamMechanicId; size?: 'sm' | 'md' }) {
  const m = teamMechanic(id);
  const Icon = m.icon;
  const sm = size === 'sm';
  return (
    <span title={m.name} style={{
      display: 'inline-flex', alignItems: 'center', gap: sm ? 3 : 5,
      height: sm ? 17 : 22, padding: sm ? '0 6px' : '0 9px',
      borderRadius: R.max, background: C.accentLight, color: C.accent,
      fontSize: sm ? 10 : 11, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
    }}>
      <Icon size={sm ? 10 : 12} strokeWidth={2} style={{ flexShrink: 0 }} />
      {m.shortName}
    </span>
  );
}
