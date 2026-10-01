// Манифест MF-модуля «Звук» (ADR-021 §3). Ключ совпадает с AudioEditorSubsystem.Key
// бэкенда: гейт слотов сверяется с активными подсистемами из /api/auth/me. Фич-флаг
// владельца (audio-editor) проверяют сами входы — каждый вклад через isAvailable с
// getFlag(FLAGS.audioEditor). Пока вкладов нет: они появятся следующими шагами.

import type { SubsystemManifest } from '../../lib/subsystems/registryCore';

export const manifest: SubsystemManifest = {
  key: 'audioeditor',
  title: 'Звук',
  order: 96,
  noPill: true,
  slots: {},
};
