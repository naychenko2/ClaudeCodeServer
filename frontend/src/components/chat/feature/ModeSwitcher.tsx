import { InlineSegmented } from '../../ui';

// Переключатель «План хода» · «Исполнение фичи» — InlineSegmented size="sm"
// (компонент из components/ui): трек pillTrack, активный сегмент — C.accentLight/C.accent,
// тач-цель 40 на мобиле. Рисуется, когда есть контекст фичи: фичи в списке ИЛИ загрузка/
// ошибка запроса (ревью фронта 2, major 4) — иначе состояния «загрузка»/«ошибка» из макета
// недостижимы (умолчание режима = feature, а переключиться на него нечем). Без контекста
// переключателя нет, и пилюля/карточка остаются байт-в-байт прежними (проверка «было/стало»).
export type TurnPlanMode = 'plan' | 'feature';

export function ModeSwitcher({ mode, onMode, show, isMobile }: {
  mode: TurnPlanMode;
  onMode: (m: TurnPlanMode) => void;
  // Есть контекст фичи: фичи в списке ИЛИ загрузка/ошибка запроса (решает вызывающий).
  show: boolean;
  isMobile?: boolean;
}) {
  if (!show) return null;
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
