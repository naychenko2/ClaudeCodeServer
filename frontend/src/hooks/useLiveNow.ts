import { useEffect, useMemo, useState } from 'react';
import type { PromptCacheState } from '../lib/promptCache';

// Часы циферблата (и живых строк рядом с ним): тик ровно на смену показываемой минуты
// (остаток округляется вверх, порог «остывает» — целые минуты), поэтому перерисовок
// раз в минуту и не чаще; у остывшего и пустого состояния таймера нет. Держит их
// компонент циферблата, не хук: тик не перерисовывает всю пилюлю (F-1b-r).
// «Сейчас» пересчитывается и на смену состояния (новый result): иначе после долгого
// простоя остаток нового якоря считался бы от устаревших часов (F-1b-r2).
export function useLiveNow(state: PromptCacheState | null): number {
  const [tick, setTick] = useState(0);
  // eslint-disable-next-line react-hooks/exhaustive-deps, react-hooks/purity -- tick и state — триггеры пересчёта часов
  const now = useMemo(() => Date.now(), [state, tick]);
  useEffect(() => {
    if (!state) return;
    const remaining = state.expiresAt - now;
    if (remaining <= 0) return;
    const t = window.setTimeout(() => setTick(n => n + 1), (remaining % 60_000 || 60_000) + 50);
    return () => window.clearTimeout(t);
  }, [now, state]);
  return now;
}
