// Живые данные локальной модели для чипа Ctx (флаг local-model-stats). Состояние по
// sessionId: текущая фаза (queued/prefill/generating), живая скорость, очередь, и итог
// последнего хода. Источник — события хаба local_model_live / local_model_turn_stats
// (эфемерные, в историю не пишутся); после перезагрузки страницы — REST
// GET /api/sessions/{id}/local-model (через общий lib/api.ts; USE_MOCK=true —
// режим витрины, данных о ходе нет).
//
// Чат НЕ на локальной модели — событий просто нет (решает бэкенд, фронт не угадывает
// по имени модели) — хук возвращает null. Нет активности дольше IDLE_TIMEOUT_MS —
// фаза idle, скорость последнего хода показывается приглушённо.

import { useEffect, useReducer, useRef } from 'react';
import { onLocalModelMessage, type LocalModelMessage } from '../../lib/signalr';
import { api, type LocalModelTurnStats } from '../../lib/api';
import { USE_MOCK } from './api';

// Фаза генерации. idle — нет активности дольше таймаута (или ход завершился).
export type LocalModelPhase = 'queued' | 'prefill' | 'generating' | 'idle';

// Итог завершённого хода (из local_model_turn_stats / REST lastTurn).
// DTO — в lib/api.ts (общий слой): тот же состав у hub-события и у
// GET /api/sessions/{id}/local-model.
export type { LocalModelTurnStats };

// Состояние редьюсера. seen — хоть одно событие/REST по этой сессии (иначе чат не на
// локальной модели и хук вернёт null). lastActivityAt — момент последнего события (для
// таймаута в idle); null — активности ещё не было.
export interface LocalModelLiveState {
  seen: boolean;
  phase: LocalModelPhase;
  genTps: number | null;
  waitingTotal: number | null;
  lastTurn: LocalModelTurnStats | null;
  lastActivityAt: number | null;
}

// Сколько без событий считать «нет активности» → idle.
export const IDLE_TIMEOUT_MS = 10_000;

// Начальное состояние (свежая сессия: событий не было).
export function initialLiveState(): LocalModelLiveState {
  return { seen: false, phase: 'idle', genTps: null, waitingTotal: null, lastTurn: null, lastActivityAt: null };
}

// Действия редьюсера.
type Action =
  | { type: 'live'; phase: string; genTps: number | null; waitingTotal: number | null; now: number }
  | { type: 'turn'; stats: LocalModelTurnStats; now: number }
  | { type: 'restore'; stats: LocalModelTurnStats | null; now: number }
  | { type: 'idle-check'; now: number }
  | { type: 'reset' };

// Проваяная фаза → наша: queued/prefill/generating проходят как есть, прочее → prefill
// (без скорости — «читает»). idle не приходит в live (он только по таймауту/завершению).
function mapPhase(p: string): Exclude<LocalModelPhase, 'idle'> {
  if (p === 'queued' || p === 'generating') return p;
  return 'prefill';
}

// Чистый редьюсер: вся логика состояния — здесь (тестируется без React/DOM).
export function liveReducer(state: LocalModelLiveState, action: Action): LocalModelLiveState {
  switch (action.type) {
    case 'live':
      return {
        ...state,
        seen: true,
        phase: mapPhase(action.phase),
        genTps: action.genTps,
        waitingTotal: action.waitingTotal,
        lastActivityAt: action.now,
      };
    case 'turn':
      // Ход завершился: запоминаем итог, фаза — idle (пока нет новых live-событий).
      return {
        ...state,
        seen: true,
        phase: 'idle',
        lastTurn: action.stats,
        lastActivityAt: action.now,
      };
    case 'restore':
      // После перезагрузки: последний ход из REST. Фаза idle, живых live-событий нет.
      if (action.stats == null) return state;
      return {
        ...state,
        seen: true,
        phase: 'idle',
        lastTurn: action.stats,
        lastActivityAt: action.now,
      };
    case 'idle-check':
      // Нет активности дольше таймаута → idle (скорость последнего хода — приглушённо).
      if (state.phase === 'idle') return state;
      if (state.lastActivityAt == null) return state;
      if (action.now - state.lastActivityAt < IDLE_TIMEOUT_MS) return state;
      return { ...state, phase: 'idle' };
    case 'reset':
      // Смена сессии: всё сбрасываем (иначе чужой ход/фаза бы «протекли» в новый чат).
      return initialLiveState();
  }
}

// === Хук ===

// Состояние для конкретной сессии; null — чат не на локальной модели (событий не было).
// Ядро — чистый liveReducer (тестируется отдельно); хук только подписывает на события,
// ведёт таймер idle и сбрасывает состояние при смене сессии.
export function useLocalModelLive(sessionId: string | null): LocalModelLiveState | null {
  const [state, dispatch] = useReducer(liveReducer, undefined, initialLiveState);
  const prevSession = useRef<string | null>(null);

  // Смена сессии — сброс (иначе фаза/итог предыдущего чата «протекли» бы в новый).
  useEffect(() => {
    if (prevSession.current !== sessionId) {
      dispatch({ type: 'reset' });
      prevSession.current = sessionId;
    }
  }, [sessionId]);

  // Подписка на события хаба этой сессии.
  useEffect(() => {
    if (!sessionId) return;
    return onLocalModelMessage(sessionId, (msg: LocalModelMessage) => {
      if (msg.type === 'local_model_live') {
        dispatch({ type: 'live', phase: msg.phase, genTps: msg.genTps ?? null, waitingTotal: msg.waitingTotal ?? null, now: Date.now() });
      } else {
        dispatch({ type: 'turn', stats: {
          genTps: msg.genTps ?? null, prefillTps: msg.prefillTps ?? null, ttftMs: msg.ttftMs ?? null,
          queueMsApprox: msg.queueMsApprox ?? null, cacheHitPerc: msg.cacheHitPerc ?? null, outTokens: msg.outTokens ?? null,
        }, now: Date.now() });
      }
    });
  }, [sessionId]);

  // Таймаут в idle: после каждого события ставит таймер на IDLE_TIMEOUT_MS; истёк без
  // новых событий — idle (скорость последнего хода показывается приглушённо).
  useEffect(() => {
    if (!sessionId || state.lastActivityAt == null) return;
    const delay = Math.max(0, IDLE_TIMEOUT_MS - (Date.now() - state.lastActivityAt));
    const t = setTimeout(() => dispatch({ type: 'idle-check', now: Date.now() }), delay);
    return () => clearTimeout(t);
  }, [sessionId, state.lastActivityAt]);

  // После перезагрузки: последний ход из REST (GET /api/sessions/{id}/local-model).
  // 404 — сессия чужая/неизвестная (бэкенд не раскрывает её существование) или
  // сетевая ошибка: данных о ходе нет — состояние не трогаем.
  useEffect(() => {
    if (!sessionId) return;
    if (USE_MOCK) {
      // Режим витрины: данных о последнем ходе нет
      dispatch({ type: 'restore', stats: null, now: Date.now() });
      return;
    }
    let cancelled = false;
    void api.localModel.lastTurn(sessionId)
      .then(res => { if (!cancelled && res.lastTurn != null) dispatch({ type: 'restore', stats: res.lastTurn, now: Date.now() }); })
      .catch(() => { /* данных о ходе нет */ });
    return () => { cancelled = true; };
  }, [sessionId]);

  return state.seen ? state : null;
}
