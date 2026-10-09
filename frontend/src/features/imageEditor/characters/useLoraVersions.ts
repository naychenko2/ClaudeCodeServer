// Версии LoRA персонажа: список, подключение, переименование, удаление (ADR-026 §7.3).
// Логика — в контроллере без React, хук — тонкая обёртка.

import { useMemo, useSyncExternalStore } from 'react';
import { loraTrainApi, type LoraTrainApi, type LoraVersionsDto } from '../api';
import { loraFailure, type LoraFailure } from './loraErrors';

export interface LoraVersionsSnapshot {
  data: LoraVersionsDto | null;
  loading: boolean;
  error: LoraFailure | null;
  // runId версии, над которой идёт действие
  pending: string | null;
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
): LoraVersionsController {
  let snap: LoraVersionsSnapshot = { data: null, loading: true, error: null, pending: null };
  const listeners = new Set<() => void>();
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
    set({ pending: runId, error: null });
    try {
      await run();
    } catch (e) {
      set({ pending: null, error: loraFailure(e) });
      return false;
    }
    set({ pending: null });
    return true;
  }

  const patchData = (fn: (d: LoraVersionsDto) => LoraVersionsDto) => {
    if (snap.data) set({ data: fn(snap.data) });
  };

  return {
    getSnapshot: () => snap,
    subscribe(fn) {
      listeners.add(fn);
      if (listeners.size === 1) void refresh();
      return () => { listeners.delete(fn); };
    },
    refresh,
    // Подключённость и «своя LoRA» зависят от всего списка — после подключения перечитываем его
    connect: runId => act(runId, async () => {
      await api.connectVersion(projectId, slug, runId);
      await refresh();
    }),
    rename: (runId, title) => act(runId, async () => {
      const upd = await api.renameVersion(projectId, slug, runId, title);
      patchData(d => ({ ...d, items: d.items.map(v => (v.runId === runId ? { ...v, ...upd } : v)) }));
    }),
    // Строка уходит сразу: на сервере она станет deleting и из списка исчезает; подключённая снимается
    remove: runId => act(runId, async () => {
      const r = await api.deleteVersion(projectId, slug, runId);
      patchData(d => ({
        ...d,
        items: d.items.filter(v => v.runId !== runId).map(v => (r.disconnected ? { ...v, connected: false } : v)),
        used: Math.max(0, d.used - (d.items.find(v => v.runId === runId)?.status === 'ready' ? 1 : 0)),
      }));
    }),
  };
}

export function useLoraVersions(projectId: string | null, slug: string | null) {
  const ctl = useMemo(
    () => (projectId && slug ? createLoraVersionsController(projectId, slug) : null), [projectId, slug]);
  const none: LoraVersionsSnapshot = useMemo(
    () => ({ data: null, loading: false, error: null, pending: null }), []);
  const snap = useSyncExternalStore(
    fn => (ctl ? ctl.subscribe(fn) : () => {}),
    () => (ctl ? ctl.getSnapshot() : none),
    () => (ctl ? ctl.getSnapshot() : none),
  );
  return { ...snap, ctl };
}
