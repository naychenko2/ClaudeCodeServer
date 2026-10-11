import { useEffect, useState } from 'react';
import type { HomeSummaryResponse } from '../../types';
import { api } from '../../lib/api';
import { onMessage, onReconnected } from '../../lib/signalr';
import { refreshAgentBoard } from '../../lib/agentBoard';
import { startVisiblePoll, throttleRefetch } from '../../lib/visiblePoll';

// Интервал страховочного пуллинга: статусы ПРОЕКТНЫХ сессий не приходят в user-группу
// SignalR (только в группы session/project), поэтому дашборд добирает их опросом.
const POLL_MS = 15_000;

// Сводка сессий для дашборда «Домой»: активные + недавние по всем проектам и чатам.
// Реалтайм-триггеры (status_changed/chat_deleted user-группы) покрывают вне-проектные
// чаты; проектные сессии доезжают пуллингом.
export function useHomeSummary(): { data: HomeSummaryResponse | null; failed: boolean } {
  const [data, setData] = useState<HomeSummaryResponse | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const fetchSummary = () => {
      // Тянем с запасом: виджет «Недавние» фильтрует по режиму (только чаты /
      // с проектами) на клиенте и показывает первые 8 из отфильтрованного
      api.home.summary(20)
        .then(d => { if (!cancelled) { setData(d); setFailed(false); } })
        .catch(() => { if (!cancelled) setFailed(true); });
    };
    fetchSummary();

    // Пауза на скрытой вкладке + догон: страховочный поллинг сводки не должен
    // жечь сеть в фоне (lib/visiblePoll)
    const stop = startVisiblePoll(() => {
      fetchSummary();
      refreshAgentBoard();
    }, POLL_MS);

    // Рефетч по событиям чатов — с троттлингом: пачка status_changed — один fetch
    const [fetchThrottled, disposeThrottle] = throttleRefetch(fetchSummary, 1000);
    const offMessage = onMessage(msg => {
      if (msg.type === 'status_changed' || msg.type === 'chat_deleted') fetchThrottled();
    });
    const offReconnected = onReconnected(fetchSummary);

    return () => {
      cancelled = true;
      stop();
      offMessage();
      offReconnected();
      disposeThrottle();
    };
  }, []);

  return { data, failed };
}
