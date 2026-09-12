// Публичная точка входа реестра фронтовых подсистем.
//
// Реэкспортирует механизм из registryCore и собирает РЕГИСТРАЦИЮ подсистем.
//
// Пилот Module Federation: подсистема «Заметки» НЕ импортируется в бандл каркаса
// (side-effect-импорт убран) — хост грузит её как MF-remote по URL
// (registerRemotes + loadRemote) и сам зовёт registerSubsystem. Список remotes
// приходит с бэка: GET /api/subsystem-modules (единая секция DynamicModules).
//
// Деградация честная, inline-фолбэка НЕТ: если API недоступен (офлайн, первый
// запуск) или remote конкретного модуля не отдаёт remoteEntry.js — этот модуль
// просто не регистрируется, и его раздел не показывается, пока remote не станет
// доступен. Оболочка от этого не роняется.

export * from './registryCore';

import { registerSubsystem } from './registryCore';
import type { SubsystemManifest } from './registryCore';
import { loadRemote, registerRemotes } from '@module-federation/runtime';
import { api } from '../api';

// ===== Загрузка subsystem-remotes через Module Federation =====

const _registeredRemotes = new Set<string>();

// Загружает все subsystem-remotes из бэка и регистрирует их в реестре.
// Вызывается на старте (App.tsx) после аутентификации.
// Честная деградация: API недоступен (офлайн/первый запуск) → список не получен,
// модули не регистрируются; отдельный remote не отвечит → только его модуль
// не регистрируется. Ни в каком случае оболочка не роняется (inline-фолбэка нет).
export async function loadSubsystemRemotes(): Promise<void> {
  const response = await api.subsystemModules.list().catch(() => null);
  if (!response) return;
  for (const m of response.items) {
    try {
      await loadSubsystemRemote(m.id, m.remoteUrl, m.exposedModule ?? './subsystem');
    } catch {
      // remote конкретного модуля не отвечает — он не зарегистрируется, его раздел
      // не появится, пока remote не станет доступен; остальные модули не трогает.
    }
  }
}

// Загружает один MF-remote и регистрирует его манифест в реестре подсистем.
async function loadSubsystemRemote(id: string, entry: string, exposed: string): Promise<void> {
  if (_registeredRemotes.has(id)) return;
  registerRemotes([{ name: id, entry, type: 'module' }]);
  _registeredRemotes.add(id);
  const mod = await loadRemote<{ subsystem: SubsystemManifest }>(`${id}/${exposed.replace(/^\.\//, '')}`);
  if (mod?.subsystem) {
    registerSubsystem(mod.subsystem);
  }
}
