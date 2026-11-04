// Сторож «локальная модель настроена»: хук, всегда вызываемый на страницах
// WorkspacePage и ChatsPage (в начале компонента, до ранних return).
// Публикует роль страницы (auth.role — тот же источник, что в HubHeader),
// кормит стор configured лёгким 60-с опросом и ПОДПИСЫВАЕТ страницу на
// configured/роль: isAvailable панели — геттер, который читается в рендере
// страницы, и без подписки смена configured не перерисовала бы рельс.
// Админ опрашивает /stats, не-админ — /stats/media.
// С Ф6 здесь же живёт состояние второй модели (движок на Video) для точки на значке
// рельса: лёгкий 15-с опрос (макет §6) ТОЛЬКО админа — не-админ /stats
// не спрашивает, состояние неизвестно, точка врала бы.

import { useEffect, useSyncExternalStore } from 'react';
import {
  getLocalModelConfigured, setLocalModelAdmin, configuredSource, useLocalModelAdmin,
  useLocalModelConfigured, useLocalModelConfiguredPolling,
} from './useLocalModelStats';
import {
  fetchLocalModelEngines, fetchLocalModelStats,
  type LocalModelEngine, type LocalModelEngineName, type LocalModelStats,
} from './api';

// === Стор состояния второго движка для точки рельса (Ф6) ===
// Последний снимок именованного движка (LocalEngines, например Gemma на Video) из лёгкого
// опроса; null — опрос ещё не ответил или второго движка нет (точки нет: состояния не знает
// никто, выдумывать нечем). Источник — лёгкий опрос, а не тяжёлый поллер панели: точка горит
// и при закрытой панели, а тяжёлый опрос живёт только пока она открыта (ревью Ф2, MAJOR 2).
let _engineStats: LocalModelStats | null = null;
const _engineSubs = new Set<() => void>();
export function getLocalModelEngineStats(): LocalModelStats | null { return _engineStats; }
export function setLocalModelEngineStats(v: LocalModelStats | null): void {
  if (_engineStats === v) return;
  _engineStats = v;
  _engineSubs.forEach(fn => fn());
}
function subscribeLocalModelEngineStats(fn: () => void): () => void {
  _engineSubs.add(fn);
  return () => { _engineSubs.delete(fn); };
}
// Одно выражение «настроен ли» для значка (isAvailable) и самой панели: основной/медиа
// configured ИЛИ последний снимок второй дорожки («движок без vLLM тоже configured»,
// макет §8.6). Две разные формулы дали бы значок без панели (пустая карточка).
export function isLocalModelConfiguredAny(): boolean {
  return getLocalModelConfigured() === true || getLocalModelEngineStats()?.configured === true;
}

// Второй движок — первый НАСТРОЕННЫЙ не-main из списка /engines: имя задаёт конфиг сервера
// (LocalEngines:<имя>), фронт его не знает. Нет такого — null.
export function pickNamedEngine(engines: LocalModelEngine[]): LocalModelEngineName | null {
  return engines.find(e => e.name !== 'main' && e.configured)?.name ?? null;
}

// Чистый запуск лёгкого опроса второй модели (15 с, макет §6): первый запрос сразу,
// затем по таймеру. Каждый тик сначала спрашивает список движков (имя второго — из конфига
// сервера), затем его снимок. Возвращает остановку. Сбой не критичен: прежнее значение
// остаётся (одиночная неудача не гасит и не зажигает точку). Второго движка нет — снимок
// сбрасывается в null. window '1m' — самый лёгкий из допустимых: точке нужны status и duty.
export function startEngineStatePolling(
  intervalMs = 15_000,
  fetchEngines: () => Promise<LocalModelEngine[]> = fetchLocalModelEngines,
  fetchStats: (engine: LocalModelEngineName) => Promise<LocalModelStats> = (engine) => fetchLocalModelStats('1m', undefined, engine),
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): () => void {
  let cancelled = false;
  const tick = () => {
    void fetchEngines()
      .then(list => {
        const name = pickNamedEngine(list);
        if (name === null) { if (!cancelled) setLocalModelEngineStats(null); return; }
        return fetchStats(name).then(d => { if (!cancelled) setLocalModelEngineStats(d); });
      })
      .catch(() => { /* прежнее значение */ });
  };
  tick();
  const id = setTimer(tick, intervalMs);
  return () => { cancelled = true; clearTimer(id); };
}

export function useLocalModelWatcher(isAdmin: boolean): void {
  useEffect(() => { setLocalModelAdmin(isAdmin); }, [isAdmin]);
  // Админ — /stats, не-админ — /stats/media
  useLocalModelConfiguredPolling(configuredSource(isAdmin));
  // Состояние второй модели — только админу: точка на значке тоже только для него
  useEffect(() => {
    if (!isAdmin) return;
    return startEngineStatePolling();
  }, [isAdmin]);
  // Подписка страницы на сторы (значение не нужно — нужен ререндер, чтобы рельса
  // перечитала геттеры): configured, роль и состояние второй движок
  useLocalModelConfigured();
  useLocalModelAdmin();
  useSyncExternalStore(subscribeLocalModelEngineStats, getLocalModelEngineStats, getLocalModelEngineStats);
}
