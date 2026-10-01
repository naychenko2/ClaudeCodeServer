// MF entry: манифест MF-модуля «Звук» (ADR-021 §3).
//
// Хост загружает этот модуль через loadRemote('audioeditor/subsystem') и получает
// SubsystemManifest; регистрация в реестре слотов — ответственность хоста.
//
// ASYNC BOUNDARY — не убирать (грабли spend, см. modules/spend/subsystem.tsx):
// код фичи статически импортирует `aihome_shell/kit`, а @module-federation/vite
// переписывает такой импорт в синхронную деструктуризацию remote-прокси. Пока кит не
// загружен, чтение его экспорта бросает промис, хост ловит его в catch и молча не
// регистрирует подсистему.

import type { SubsystemManifest } from '../../src/lib/subsystems/registryCore';

export const subsystem: Promise<SubsystemManifest> = (async () => {
  await import('aihome_shell/kit');
  const { manifest } = await import('../../src/features/audioEditor/manifest');
  return manifest;
})();
