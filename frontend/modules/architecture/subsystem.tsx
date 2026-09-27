// MF entry: реэкспорт манифеста подсистемы «Архитектура» для Module Federation.
//
// Хост загружает этот модуль через loadRemote('architecture/subsystem') и получает
// SubsystemManifest (вклады в слоты workspace-panel и workspace-center-doc).
// Регистрация в реестре слотов — ответственность хоста (registerSubsystem).
//
// ASYNC BOUNDARY — не убирать (урок notes, c44d51d6). Код фичи импортирует ядро
// оболочки статически (`aihome_shell/kit`), а @module-federation/vite переписывает
// такой импорт в СИНХРОННУЮ деструктуризацию remote-прокси: пока кит не загружен,
// чтение его экспорта бросает промис загрузки. Статический импорт манифеста отсюда
// падал бы без видимой причины — хост молча не регистрировал бы подсистему, и раздел
// «Архитектура» исчезал бы целиком. Поэтому сначала ждём кит, потом грузим код фичи.
// Экспорт — промис: хост его await'ит.

import type { SubsystemManifest } from '../../src/lib/subsystems/registryCore';

export const subsystem: Promise<SubsystemManifest> = (async () => {
  await import('aihome_shell/kit');
  const { manifest } = await import('../../src/features/architecture/manifest');
  return manifest;
})();
