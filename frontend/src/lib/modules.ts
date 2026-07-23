// Стор внешних модулей платформы (контракт docs/module-platform-integration-contract.md,
// ТЗ R5/R6): список включённых модулей приходит с бэка (GET /api/modules), а их remote
// ./Tab грузятся через Module Federation в рантайме. Паттерн стора — как featureFlags.ts.

import { useSyncExternalStore } from 'react';
import { init, loadRemote, registerRemotes } from '@module-federation/runtime';
import type { ComponentType } from 'react';
import { api, type ModuleInfo } from './api';

// Контекст, который оболочка передаёт remote-компоненту ./Tab (контракт §7)
export interface AIHomeModuleContext {
  user: { id: string; name: string };
  apiBase: string;                  // "/api/modules/{id}"
  getToken: () => string | null;    // cc_token для Authorization на apiBase
  theme: { mode: 'light' | 'dark' };
  navigate: (hash: string) => void;
  onTitleChange?: (t: string) => void;
  schemaVersion: string;
}

export type ModuleTabComponent = ComponentType<AIHomeModuleContext>;

let _modules: ModuleInfo[] = [];
const _listeners = new Set<() => void>();
let _mfReady = false;
const _registered = new Set<string>();

function emit() { _listeners.forEach(fn => fn()); }

// Инициализация MF host-runtime единожды: shared singleton react/react-dom (§7.1).
// Версии берём из установленного пакета — тот же React, что и в оболочке.
function ensureMf() {
  if (_mfReady) return;
  _mfReady = true;
  // Динамические импорты, чтобы не тянуть React в этот модуль лишний раз
  // (react уже загружен оболочкой; здесь только регистрируем его как shared).
  const React = (window as unknown as { React?: unknown }).React;
  init({
    name: 'aihome_shell',
    remotes: [],
    shared: React ? {
      react: { version: '19.2.0', lib: () => React, shareConfig: { singleton: true, requiredVersion: '^19.2.0' } },
    } : {},
  });
}

// Загрузка списка модулей с бэка (на старте/после логина). Ошибку глушим — оболочка
// работает и без модулей (R9): раздел просто не покажет вкладок.
export async function loadModules(): Promise<void> {
  try {
    const { items } = await api.modules.list();
    _modules = items.slice().sort((a, b) => (a.tab?.order ?? 100) - (b.tab?.order ?? 100));
    emit();
  } catch {
    _modules = [];
    emit();
  }
}

export function getModules(): ModuleInfo[] { return _modules; }
export function getModule(id: string): ModuleInfo | undefined {
  return _modules.find(m => m.id === id);
}
export function subscribeModules(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Хук подписки на список модулей (для HubTabs/ModuleScreen)
export function useModules(): ModuleInfo[] {
  return useSyncExternalStore(subscribeModules, getModules, getModules);
}

// Загрузка remote-компонента ./Tab модуля. Регистрирует remote лениво (type:'module' —
// remoteEntry Vite/Rolldown это нативный ESM, грузится через import(), не <script>;
// установлено спайком R5a). Бросает при недоступности remote (ModuleHost покажет ошибку).
export async function loadModuleTab(m: ModuleInfo): Promise<ModuleTabComponent> {
  if (!m.remoteEntry) throw new Error(`У модуля «${m.id}» нет frontend.remoteEntry`);
  ensureMf();
  if (!_registered.has(m.id)) {
    registerRemotes([{ name: m.id, entry: m.remoteEntry, type: 'module' }]);
    _registered.add(m.id);
  }
  const exposed = m.exposedModule ?? './Tab';
  const loaded = await loadRemote<{ default: ModuleTabComponent }>(`${m.id}/${exposed.replace(/^\.\//, '')}`);
  if (!loaded?.default) throw new Error(`Модуль «${m.id}» не экспортировал ${exposed}`);
  return loaded.default;
}
