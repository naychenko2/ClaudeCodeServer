// Сторож «локальная модель настроена»: хук, всегда вызываемый на страницах
// WorkspacePage и ChatsPage (в начале компонента, до ранних return).
// Публикует роль страницы (auth.role — тот же источник, что в HubHeader),
// кормит стор configured лёгким 60-с опросом и ПОДПИСЫВАЕТ страницу на
// configured/роль: isAvailable панели — геттер, который читается в рендере
// страницы, и без подписки смена configured не перерисовала бы рельс.
// Без флага или не-админу — ни одного запроса к /api/local-model/*.
// С Ф6 здесь же живёт состояние второй модели (Bonsai) для точки на значке
// рельса: лёгкий 15-с опрос (макет §6) ТОЛЬКО админа с флагом — не-админ /stats
// не спрашивает, состояние неизвестно, точка врал бы.

import { useEffect, useSyncExternalStore } from 'react';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import {
  getLocalModelConfigured, setLocalModelAdmin, configuredSource, useLocalModelAdmin,
  useLocalModelConfigured, useLocalModelConfiguredPolling,
} from './useLocalModelStats';
import { fetchLocalModelStats, type LocalModelStats } from './api';

// === Стор состояния Bonsai для точки рельса (Ф6) ===
// Последний снимок второй модели из лёгкого опроса; null — опрос ещё не ответил
// (точки нет: состояния не знает никто, выдумывать нечем). Источник — лёгкий опрос,
// а не тяжёл поллер панели: точка горит и при закрытой панели, а тяжёл опрос живёт
// только пока она открыта (ревью Ф2, MAJOR 2).
let _bonsai: LocalModelStats | null = null;
const _bonsaiSubs = new Set<() => void>();
export function getLocalModelBonsai(): LocalModelStats | null { return _bonsai; }
export function setLocalModelBonsai(v: LocalModelStats | null): void {
  if (_bonsai === v) return;
  _bonsai = v;
  _bonsaiSubs.forEach(fn => fn());
}
function subscribeLocalModelBonsai(fn: () => void): () => void {
  _bonsaiSubs.add(fn);
  return () => { _bonsaiSubs.delete(fn); };
}
// Одно выражение «настроен ли» для значка (isAvailable) и самой панели: основной/медиа
// configured ИЛИ последний снимок второй дорожки («Bonsai без vLLM тоже configured»,
// макет §8.6). Две разные формулы дали бы значок без панели (пустая карточка).
export function isLocalModelConfiguredAny(): boolean {
  return getLocalModelConfigured() === true || getLocalModelBonsai()?.configured === true;
}

// Чистый запуск лёгкого опроса второй модели (15 с, макет §6): первый запрос сразу,
// затем по таймеру. Возвращает остановку. Сбой не критичен: прежнее значение остаётся
// (одиночная неудача не гасит и не зажигает точку). window '1m' — самый лёгкий из
// допустимых: точке нужны status и duty, а не история.
export function startBonsaiStatePolling(
  intervalMs = 15_000,
  fetch: () => Promise<LocalModelStats> = () => fetchLocalModelStats('1m', undefined, 'bonsai'),
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): () => void {
  let cancelled = false;
  const tick = () => {
    void fetch()
      .then(d => { if (!cancelled) setLocalModelBonsai(d); })
      .catch(() => { /* прежнее значение */ });
  };
  tick();
  const id = setTimer(tick, intervalMs);
  return () => { cancelled = true; clearTimer(id); };
}

export function useLocalModelWatcher(isAdmin: boolean): void {
  const flag = useFeature(FLAGS.localModelStats);
  useEffect(() => { setLocalModelAdmin(isAdmin); }, [isAdmin]);
  // Админ — /stats (флаг гейтит), не-админ — /stats/media (без флага)
  useLocalModelConfiguredPolling(configuredSource(flag, isAdmin));
  // Состояние второй модели — только админу с флагом: точка на значке тоже только для них
  useEffect(() => {
    if (!isAdmin || !flag) return;
    return startBonsaiStatePolling();
  }, [isAdmin, flag]);
  // Подписка страницы на сторы (значение не нужно — нужен ререндер, чтобы рельса
  // перечитала геттеры): configured, роль и состояние Bonsai
  useLocalModelConfigured();
  useLocalModelAdmin();
  useSyncExternalStore(subscribeLocalModelBonsai, getLocalModelBonsai, getLocalModelBonsai);
}
