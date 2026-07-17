import { C, R } from '../../lib/design';
import { teamMechanic, type TeamMechanicId } from './teamMechanics';

// Бейдж командной механики: иконка + название. Текст хода такой механики — скилл-команда
// (/oh-my-claudecode:…, /panel-of-experts) или промпт-обвязка дискуссии, и без подсказки
// непонятен. Используется на сообщении в ленте (md), на карточках списка чатов и в шапке (sm).
export function TeamMechanicBadge({ id, size = 'md' }: { id: TeamMechanicId; size?: 'sm' | 'md' }) {
  const m = teamMechanic(id);
  const Icon = m.icon;
  const sm = size === 'sm';
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: sm ? 3 : 5,
      height: sm ? 17 : 22, padding: sm ? '0 6px' : '0 9px',
      borderRadius: R.max, background: C.accentLight, color: C.accent,
      fontSize: sm ? 10 : 11, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
    }}>
      <Icon size={sm ? 10 : 12} strokeWidth={2} style={{ flexShrink: 0 }} />
      {m.name}
    </span>
  );
}
