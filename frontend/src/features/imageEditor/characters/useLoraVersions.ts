// Версии LoRA персонажа: список, подключение, переименование, удаление (ADR-026 §7.3).
// Логика — в контроллере без React, хук — тонкая обёртка.

import { useCallback, useMemo, useRef, useSyncExternalStore } from 'react';
import { loraTrainApi, type ImageEditCharacter, type LoraTrainApi, type LoraVersionsDto } from '../api';
import { loraFailure, type LoraFailure } from './loraErrors';

export interface LoraVersionsSnapshot {
  data: LoraVersionsDto | null;
  loading: boolean;
  error: LoraFailure | null;
  // runId версий, над которыми идёт действие (строк может быть несколько сразу)
  pending: string[];
}

export interface LoraVersionsController {
  getSnapshot(): LoraVersionsSnapshot;
  subscribe(fn: () => void): () => void;
  refresh(): Promise<void>;
  connect(runId: string): Promise<boolean>;
  rename(runId: string, title: string): Promise<boolean>;
  remove(runId: string): Promise<boolean>;
}

export function createLoraVersionsController(
  projectId: string, slug: string, api: LoraTrainApi = loraTrainApi,
  // Подключение и удаление подключённой версии меняют LoRA персонажа: null — «перечитай сам»
  onCharacterChanged?: (c: ImageEditCharacter | null) => void,
): LoraVersionsController {
  let snap: LoraVersionsSnapshot = { data: null, loading: true, error: null, pending: [] };
  const listeners = new Set<() => void>();
  // Загрузка стартует один раз на контроллер: переподписка (StrictMode, смена ссылки) её не повторяет
  let started = false;
  const set = (patch: Partial<LoraVersionsSnapshot>) => {
    snap = { ...snap, ...patch };
    listeners.forEach(fn => fn());
  };

  async function refresh(): Promise<void> {
    try {
      set({ data: await api.versions(projectId, slug), loading: false, error: null });
    } catch (e) {
      set({ loading: false, error: loraFailure(e) });
    }
  }

  async function act(runId: string, run: () => Promise<void>): Promise<boolean> {
    set({ pending: [...snap.pending, runId], error: null });
    const done = () => snap.pending.filter(x => x !== runId);
    try {
      await run();
    } catch (e) {
      set({ pending: done(), error: loraFailure(e) });
      return false;
    }
    set({ pending: done() });
    return true;
  }

  const patchData = (fn: (d: LoraVersionsDto) => LoraVersionsDto) => {
    if (snap.data) set({ data: fn(snap.data) });
  };

  return {
    getSnapshot: () => snap,
    subscribe(fn) {
      listeners.add(fn);
      if (!started) { started = true; void refresh(); }
      return () => { listeners.delete(fn); };
    },
    refresh,
    // Подключённость и «своя LoRA» зависят от всего списка — после подключения перечитываем его
    connect: runId => act(runId, async () => {
      const c = await api.connectVersion(projectId, slug, runId);
      onCharacterChanged?.(c);
      await refresh();
    }),
    rename: (runId, title) => act(runId, async () => {
      const upd = await api.renameVersion(projectId, slug, runId, title);
      patchData(d => ({ ...d, items: d.items.map(v => (v.runId === runId ? { ...v, ...upd } : v)) }));
    }),
    // Строка уходит сразу: на сервере она станет deleting и из списка исчезает; подключённая снимается
    remove: runId => act(runId, async () => {
      const r = await api.deleteVersion(projectId, slug, runId);
      if (r.disconnected) onCharacterChanged?.(null);
      patchData(d => ({
        ...d,
        items: d.items.filter(v => v.runId !== runId).map(v => (r.disconnected ? { ...v, connected: false } : v)),
        used: Math.max(0, d.used - (d.items.find(v => v.runId === runId)?.status === 'ready' ? 1 : 0)),
      }));
    }),
  };
}

export type LoraVersionsHandle = ReturnType<typeof useLoraVersions>;

export function useLoraVersions(
  projectId: string | null, slug: string | null, onCharacterChanged?: (c: ImageEditCharacter | null) => void,
) {
  // Колбэк читается в момент вызова: контроллер от смены его ссылки не пересоздаётся
  const cb = useRef(onCharacterChanged);
  cb.current = onCharacterChanged;
  const ctl = useMemo(
    () => (projectId && slug ? createLoraVersionsController(projectId, slug, undefined, c => cb.current?.(c)) : null),
    [projectId, slug]);
  const none: LoraVersionsSnapshot = useMemo(
    () => ({ data: null, loading: false, error: null, pending: [] }), []);
  // Ссылки стабильны по контроллеру: инлайновая подписка переподписывалась на каждом рендере
  const subscribe = useCallback((fn: () => void) => (ctl ? ctl.subscribe(fn) : () => {}), [ctl]);
  const getSnapshot = useCallback(() => (ctl ? ctl.getSnapshot() : none), [ctl, none]);
  const snap = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return { ...snap, ctl };
}
