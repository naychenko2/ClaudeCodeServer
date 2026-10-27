// Выбор движка панели «Локальные модели» («Основная» / «Bonsai», макет §2).
// Стор: список движков с сервера + выбор пользователя, который запоминается в
// localStorage. Паттерн — как lib/pinnedProjects.ts: модульное состояние, подписчики,
// useSyncExternalStore. Фабрика принимает хранилище, чтобы тест мог «перезапустить»
// стор поверх того же localStorage.

import { useEffect, useSyncExternalStore } from 'react';
import { fetchLocalModelEngines, type LocalModelEngine, type LocalModelEngineName } from './api';

export const ENGINE_STORAGE_KEY = 'cc_local_model_engine';

type EngineStorage = Pick<Storage, 'getItem' | 'setItem'>;

function isEngineName(v: unknown): v is LocalModelEngineName {
  return v === 'main' || v === 'bonsai';
}

function isConfigured(engines: LocalModelEngine[], name: LocalModelEngineName): boolean {
  return engines.some(e => e.name === name && e.configured);
}

// Умолчание: main, если vLLM настроен; иначе bonsai, если настроен он; иначе main
// (ни один не настроен — панели нет, значение безвредно и совпадает с прежним поведением).
export function pickDefaultEngine(engines: LocalModelEngine[]): LocalModelEngineName {
  if (isConfigured(engines, 'main')) return 'main';
  if (isConfigured(engines, 'bonsai')) return 'bonsai';
  return 'main';
}

// Итоговый движок: сохранённый выбор, если этот движок сейчас настроен, иначе умолчание.
// Устаревший выбор (движок отключили) молча заменяется умолчанием, но не стирается из хранилища.
export function resolveEngine(engines: LocalModelEngine[], saved: LocalModelEngineName | null): LocalModelEngineName {
  if (saved && isConfigured(engines, saved)) return saved;
  return pickDefaultEngine(engines);
}

export interface EnginesSnapshot {
  engines: LocalModelEngine[];
  engine: LocalModelEngineName;
}

export interface EnginesStore {
  getSnapshot(): EnginesSnapshot;
  subscribe(fn: () => void): () => void;
  setEngines(list: LocalModelEngine[]): void;
  setEngine(name: LocalModelEngineName): void;
}

export function createEnginesStore(storage?: EngineStorage): EnginesStore {
  const read = (): LocalModelEngineName | null => {
    try {
      const raw = (storage ?? localStorage).getItem(ENGINE_STORAGE_KEY);
      return isEngineName(raw) ? raw : null;
    } catch { return null; }
  };
  let saved = read();
  let engines: LocalModelEngine[] = [];
  const subs = new Set<() => void>();
  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is
  let snapshot: EnginesSnapshot = { engines, engine: resolveEngine(engines, saved) };
  const refresh = () => {
    const engine = resolveEngine(engines, saved);
    if (snapshot.engines !== engines || snapshot.engine !== engine) snapshot = { engines, engine };
  };
  const emit = () => subs.forEach(fn => fn());

  return {
    getSnapshot: () => snapshot,
    subscribe(fn) { subs.add(fn); return () => { subs.delete(fn); }; },
    setEngines(list) {
      engines = list;
      refresh();
      emit();
    },
    setEngine(name) {
      if (saved === name) return;
      saved = name;
      try { (storage ?? localStorage).setItem(ENGINE_STORAGE_KEY, name); } catch { /* приватный режим */ }
      refresh();
      emit();
    },
  };
}

const store = createEnginesStore();

export function getLocalModelEngine(): LocalModelEngineName { return store.getSnapshot().engine; }
export function setLocalModelEngine(name: LocalModelEngineName): void { store.setEngine(name); }

// Хук панели: подгружает список движков, пока панель открыта (active), и отдаёт выбор.
// Сбой запроса не критичен: остаётся прежний список (до первого успеха — пустой, main).
export function useLocalModelEngines(active: boolean): {
  engines: LocalModelEngine[];
  engine: LocalModelEngineName;
  setEngine: (name: LocalModelEngineName) => void;
} {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    fetchLocalModelEngines()
      .then(list => { if (!cancelled) store.setEngines(list); })
      .catch(() => { /* прежний список */ });
    return () => { cancelled = true; };
  }, [active]);
  return { engines: snap.engines, engine: snap.engine, setEngine: setLocalModelEngine };
}
