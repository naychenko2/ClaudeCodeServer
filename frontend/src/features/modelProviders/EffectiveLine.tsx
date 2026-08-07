import { useEffectiveLine, type EffectiveLineContext } from '../../lib/presets';
import { C, FS, R } from '../../lib/design';

// Строка-итог «Сейчас пойдёт: {модель} · {откуда}» — ключевой элемент спеки (блок 4):
// в каждом месте выбора видно не только что выбрано, но и чем это обернётся.
// Данные — с серверного резолва (см. useEffectiveLine): пока эндпоинта нет, строка
// не рендерится; место под неё стоит во всех контролах выбора.
export function EffectiveLine({ ctx }: { ctx: EffectiveLineContext }) {
  const line = useEffectiveLine(ctx);
  if (!line) return null;
  return (
    <div style={{
      fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
      background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
      padding: '6px 9px',
    }}>
      {line}
    </div>
  );
}
