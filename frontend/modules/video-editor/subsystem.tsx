// MF entry: манифест MF-модуля «Видео» (ADR-022). Пока — скелет: регистрирует заглушку
// панели за флагом video-editor; настоящая панель (features/videoEditor) — блок 5.
//
// Хост загружает этот модуль через loadRemote('videoeditor/subsystem') и получает
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
  const { Clapperboard } = await import('lucide-react');
  const { FLAGS, getFlag, ICON_SIZE, ICON_STROKE } = await import('aihome_shell/kit');
  const { StubPanel } = await import('./StubPanel');
  const enabled = () => getFlag(FLAGS.videoEditor);
  return {
    key: 'videoeditor',
    title: 'Видео',
    order: 97,
    noPill: true,
    slots: {
      'workspace-panel-def': [
        {
          name: 'videoEditor',
          render: () => <StubPanel />,
          action: {
            title: 'Видео',
            icon: <Clapperboard size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
            isAvailable: () => enabled(),
          } as unknown as Record<string, unknown>,
        },
      ],
    },
  };
})();
