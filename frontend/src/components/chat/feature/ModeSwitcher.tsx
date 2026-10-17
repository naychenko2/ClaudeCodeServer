import { C, R } from '../../../lib/design';

// Переключатель «План хода» · «Исполнение фичи» в шапке меню/карточки.
// Рисуется ТОЛЬКО когда у чата есть фичи: без фичи переключателя нет, и
// пилюля/карточка остаются байт-в-байт прежними (проверка «было/стало»).
//
// Сегмент: трек bgSelected, активная кнопка — accentLight + accent (как в макете).
export type TurnPlanMode = 'plan' | 'feature';

export function ModeSwitcher({ mode, onMode, hasFeatures }: {
  mode: TurnPlanMode;
  onMode: (m: TurnPlanMode) => void;
  hasFeatures: boolean;
}) {
  if (!hasFeatures) return null;
  const btn = (m: TurnPlanMode): React.CSSProperties => {
    const on = mode === m;
    return {
      border: 'none', borderRadius: R.md, cursor: 'pointer',
      fontSize: 11, fontWeight: 600, padding: '2px 8px', minHeight: 20,
      background: on ? C.accentLight : 'transparent',
      color: on ? C.accent : C.textMuted,
      whiteSpace: 'nowrap',
    };
  };
  return (
    <div
      role="group"
      aria-label="Что показать"
      style={{ display: 'flex', gap: 2, background: C.bgSelected, borderRadius: R.lg, padding: 2, flexShrink: 0 }}
    >
      <button aria-pressed={mode === 'plan'} onClick={() => onMode('plan')} style={btn('plan')}>
        План хода
      </button>
      <button aria-pressed={mode === 'feature'} onClick={() => onMode('feature')} style={btn('feature')}>
        Исполнение фичи
      </button>
    </div>
  );
}
