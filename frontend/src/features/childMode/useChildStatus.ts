import { useEffect, useState } from 'react';
import { isTabHidden, startVisiblePoll } from '../../lib/visiblePoll';
import { childApi } from './api';
import type { ChildStatus } from './types';

// Опрос состояния детского режима для оболочки: присутствие в пространстве (heartbeat)
// и снимок окон/лимитов (status). Порядок — ADR-041 §3 и разрез волны 1: «время в
// пространстве считается присутствием», фронт шлёт POST /api/child/heartbeat {space} ровно
// раз в 60 с (сервер копит минуту один раз, частить бесполезно); статус — при монтировании,
// раз в 30 с и после каждого heartbeat (снимок сразу после учёта присутствия).
//
// Скрытая вкладка — ни одного запроса: startVisiblePoll пропускает тик на document.hidden и
// делает один немедленный при возврате видимости (тот же порядок, что у опросов списков).
// При монтировании под скрытой вкладкой начальный статус не запрашивается — он придёт догоном
// на возврате видимости.

const HEARTBEAT_MS = 60_000;
const STATUS_MS = 30_000;

// Куда опрос кладёт результат: хук — в свой стор, тест — в списки вызовов.
export interface ChildStatusSink {
  // Только ответ последнего запущенного status-запроса (счётчик в startChildStatusPoll)
  onStatus: (status: ChildStatus) => void;
  // Сеть недоступна: оболочка ставит офлайн-мостик, последний status при этом остаётся
  onOffline: (offline: boolean) => void;
}

// Модель опроса без React: вся семантика (интервалы, пауза на скрытой вкладке, гонка
// ответов, отмена) проверяется на ней; useChildStatus ниже — тонкая обёртка стор+эффект.
// Возвращает снятие: отменяет in-flight статус-запрос и снимает оба таймера и подписку
// visibilitychange. После снятия ни один ответ не применяется.
export function startChildStatusPoll(space: string, sink: ChildStatusSink): () => void {
  let alive = true;
  // Счётчик status-запросов: опрос тикает раз в 30 с и после каждого heartbeat, ответы
  // могут прийти в другом порядке — применяется ответ последнего запущенного, ранний
  // отбрасывается.
  let seq = 0;
  const ac = new AbortController();

  const loadStatus = () => {
    const mine = ++seq;
    childApi.status(space, { signal: ac.signal })
      .then((s) => { if (alive && mine === seq) sink.onStatus(s); })
      // Отказ сети — офлайн; status не трогаем (последний снимок остаётся на экране).
      // catch обязателен: без него отказ был бы необработанным rejection'ом.
      .catch(() => { if (alive && mine === seq) sink.onOffline(true); });
  };

  const beat = () => {
    childApi.heartbeat(space)
      .catch(() => { if (alive) sink.onOffline(true); })
      // Статус — после heartbeat: снимок уже с учётом этого присутствия. Ответ самого
      // heartbeat в состояние не поднимается (в api.ts он описан как экономия второго
      // запроса): здесь все обновления статуса идут через один канал и один счётчик,
      // иначе два источника разного возраста писали бы состояние по очереди.
      .then(() => { if (alive) loadStatus(); });
  };

  const stopHeartbeat = startVisiblePoll(beat, HEARTBEAT_MS);
  const stopStatus = startVisiblePoll(loadStatus, STATUS_MS);
  // Первый статус — при монтировании (первый heartbeat — через 60 с, не сразу).
  if (!isTabHidden()) loadStatus();

  return () => {
    alive = false;
    ac.abort();
    stopHeartbeat();
    stopStatus();
  };
}

// Состояние для оболочки: { status, offline }. Ключ состояния — space: снимок хранится
// вместе со своим ключом, при смене space прежний ответ не возвращается (опрос старого
// пространства снят, его in-flight запрос отменён — см. startChildStatusPoll).
interface Snapshot { space: string; status: ChildStatus | null; offline: boolean }

export function useChildStatus(space: string): { status: ChildStatus | null; offline: boolean } {
  const [snap, setSnap] = useState<Snapshot | null>(null);
  useEffect(() => startChildStatusPoll(space, {
    // Удачный снимок значит «сеть есть»: офлайн-мостик снимается вместе с ним.
    onStatus: (s) => setSnap({ space, status: s, offline: false }),
    // Отказ до первого снимка: offline=true и без статуса — мостик нужен и тогда.
    onOffline: (o) => setSnap((p) => (p?.space === space ? { ...p, offline: o } : { space, status: null, offline: o })),
  }), [space]);
  const cur = snap?.space === space ? snap : null;
  return { status: cur?.status ?? null, offline: cur?.offline ?? false };
}
