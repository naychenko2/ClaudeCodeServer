import { InlineSegmented } from '../../ui';

// Переключатель «План хода» · «Исполнение фичи» — InlineSegmented size="sm"
// (компонент из components/ui): трек pillTrack, активный сегмент — C.accentLight/C.accent,
// тач-цель 40 на мобиле. Рисуется ТОЛЬКО когда у чата есть фичи: без фичи
// переключателя нет, и пилюля/карточка остаются байт-в-байт прежними (проверка «было/стало»).
export type TurnPlanMode = 'plan' | 'feature';

export function ModeSwitcher({ mode, onMode, hasFeatures, isMobile }: {
  mode: TurnPlanMode;
  onMode: (m: TurnPlanMode) => void;
  hasFeatures: boolean;
  isMobile?: boolean;
}) {
  if (!hasFeatures) return null;
  return (
    <div role="group" aria-label="Что показать">
      <InlineSegmented
        value={mode}
        isMobile={isMobile}
        size="sm"
        options={[
          { value: 'plan', label: 'План хода' },
          { value: 'feature', label: 'Исполнение фичи' },
        ]}
        onChange={onMode}
      />
    </div>
  );
}
