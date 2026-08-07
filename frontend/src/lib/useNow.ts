import { useEffect, useState } from 'react';

// Реактивное «текущее время»: состояние, обновляемое по интервалу, вместо вызова
// Date.now() в фазе рендера (нечистая функция по react-hooks/purity — результат
// рендера не должен зависеть от момента вызова). Компоненты с таймерами обратного
// отсчёта и «кто онлайн» получают живое значение, а интервал живёт только пока
// потребителю это нужно (enabled).
export function useNow(intervalMs: number, enabled: boolean = true): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    const t = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(t);
  }, [intervalMs, enabled]);
  return now;
}
