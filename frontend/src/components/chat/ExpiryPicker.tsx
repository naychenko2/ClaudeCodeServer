import { C } from '../../lib/design';
import { EXPIRY_PRESETS } from '../../lib/expiry';
import { SegmentedControl } from '../ui';

// Выбор времени жизни чата: «Бессрочно» + пресеты срока. Один и тот же блок нужен
// и в пустом чате (пилюли NewChatSetup), и в шапке начатого (кнопка-часы), поэтому
// живёт отдельным компонентом — иначе разметка и текст расходятся по копиям.
export function ExpiryPicker({ value, onChange, columns = 3 }: {
  // Минуты неактивности до авто-удаления; null — чат бессрочный
  value: number | null | undefined;
  onChange: (minutes: number | null) => void;
  columns?: number;
}) {
  return (
    <>
      <div style={{ fontSize: 11.5, color: C.textMuted, marginBottom: 8, lineHeight: 1.4 }}>
        Временный чат удалится сам вместе с историей, если не будет активности выбранное время.
      </div>
      <SegmentedControl
        value={value ? String(value) : ''}
        options={[{ value: '', label: 'Бессрочно' }, ...EXPIRY_PRESETS.map(p => ({ value: String(p.minutes), label: p.label }))]}
        onChange={v => onChange(v ? Number(v) : null)}
        columns={columns}
      />
    </>
  );
}
