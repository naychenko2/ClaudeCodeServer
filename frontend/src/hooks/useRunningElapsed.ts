import { useEffect, useState } from 'react';
import { shownFor, tickShownClock, type ShownClock } from '../lib/toolTiming';

// Живой отсчёт «идёт M:SS» раз в секунду. Значение привязано к своему startedAt
// (tickShownClock): сдвиг старта начинает отсчёт заново, а не тащит старый максимум в итог.
// null — до первого тика: короче порога таймер всё равно не показывается. После остановки
// возвращает последнее показанное — итог «готово» не должен оказаться меньше него.
// Общий для карточки инструмента и индикатора ожидания под лентой
export function useRunningElapsed(startedAt: number | null | undefined, running: boolean): number | null {
  const [clock, setClock] = useState<ShownClock | null>(null);
  useEffect(() => {
    if (!running || typeof startedAt !== 'number') return;
    const tick = () => setClock(prev => tickShownClock(prev, startedAt, Date.now()));
    const first = setTimeout(tick, 0);
    const t = setInterval(tick, 1000);
    return () => { clearTimeout(first); clearInterval(t); };
  }, [startedAt, running]);
  return shownFor(clock, startedAt);
}
