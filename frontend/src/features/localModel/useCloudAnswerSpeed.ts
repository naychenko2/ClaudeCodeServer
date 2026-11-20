// «Облачные модели: скорость ответа» — отдельный медленный опрос (раз в минуту).
// Снимок за сутки из журнала расхода меняется медленно: тащить его в 2-с опрос
// статистики стенда незачем. Отдельный хук — по двум причинам: карточка не должна
// блокировать отрисовку вкладки и не должна дёргать сеть чаще раза в минуту.

import { useEffect, useRef, useState } from 'react';
import { fetchCloudAnswerSpeed } from './api';
import type { CloudAnswerSpeedResponse } from './types';

export interface CloudAnswerSpeedState {
  // снимок за сутки; null — данных ещё нет
  data: CloudAnswerSpeedResponse | null;
  // первый запрос идёт
  loading: boolean;
  // 403 — не админ: карточка не показывается вовсе, и опрос прекращается
  forbidden: boolean;
}

const EMPTY: CloudAnswerSpeedState = { data: null, loading: false, forbidden: false };

// Хук опроса: первый запрос сразу, дальше по таймеру (минута — окно суток,
// чаще смысла нет). 403 — навсегда: опрос прекращается, карточка не показывается.
// Прочие сбои тихие: прежние данные остаются, вкладка от этого не страдает.
export function useCloudAnswerSpeed(active: boolean, intervalMs = 60_000): CloudAnswerSpeedState {
  const [state, setState] = useState<CloudAnswerSpeedState>(EMPTY);
  // 403 — навсегда: дальше опрос не идёт (роль не поменяется на этой вкладке).
  const forbiddenRef = useRef(false);
  useEffect(() => {
    if (!active) {
      forbiddenRef.current = false;
      setState(EMPTY);
      return;
    }
    let cancelled = false;
    const load = () => {
      if (forbiddenRef.current) return;
      // Окна нет в запросе: последние 24 часа считает бэкенд (api.ts)
      void fetchCloudAnswerSpeed().then(d => {
        if (!cancelled) setState({ data: d, loading: false, forbidden: false });
      }).catch((err: unknown) => {
        if (cancelled) return;
        const status = (err as { status?: number } | null)?.status;
        if (status === 403) {
          forbiddenRef.current = true;
          setState({ data: null, loading: false, forbidden: true });
          return;
        }
        setState(s => ({ ...s, loading: false }));
      });
    };
    setState(s => ({ ...s, loading: true }));
    load();
    const id = window.setInterval(load, intervalMs);
    return () => { cancelled = true; window.clearInterval(id); };
  }, [active, intervalMs]);
  return state;
}
