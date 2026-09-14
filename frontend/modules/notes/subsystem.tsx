// MF entry: реэкспорт манифеста подсистемы «Заметки» для Module Federation.
//
// Хост загружает этот модуль через loadRemote('aihome_notes/subsystem') и получает
// SubsystemManifest (manifest + tab + slots). Регистрация в реестре слотов —
// ответственность хоста (registerSubsystem), не этого модуля.
//
// ASYNC BOUNDARY — не убирать. Код фичи импортирует ядро оболочки статически
// (`aihome_shell/kit`), а @module-federation/vite переписывает такой импорт в
// СИНХРОННУЮ деструктуризацию remote-прокси: пока кит не загружен, любое чтение
// его экспорта бросает промис загрузки (proxy.get → `throw ensurePending()`).
// Файлы фичи читают токены кита на верхнем уровне модуля, поэтому статический
// импорт манифеста отсюда падал БЕЗ ВИДИМОЙ ПРИЧИНЫ: хост ловил брошенный промис
// в catch и молча не регистрировал подсистему — раздел «Заметки» исчезал целиком.
//
// Поэтому сначала дожидаемся кита (динамический import — плагин ждёт его
// __mf_remote_pending), и только потом грузим код фичи. Экспорт — промис:
// хост его await'ит (await по обычному значению тоже безвреден).

import type { SubsystemManifest } from '../../src/lib/subsystems/registryCore';

export const subsystem: Promise<SubsystemManifest> = (async () => {
  await import('aihome_shell/kit');
  const { manifest } = await import('../../src/features/notes/manifest');
  return manifest;
})();
