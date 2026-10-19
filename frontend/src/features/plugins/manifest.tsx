// Чистый экспорт манифеста подсистемы «Плагины» (SubsystemManifest, ADR-031).
// Ключ совпадает с PluginsSubsystem.Key бэкенда: гейт слотов сверяется с активными
// подсистемами из /api/auth/me. Вклад — только в ленту чата: карточка черновика
// build-it-yourself (запись plugin_draft). Экран «Плагины» и слот панели не затрагиваются.

import type { ChatItemToolCtx, SubsystemManifest } from '../../lib/subsystems/registryCore';
import { DraftAnchor, RECORD_DRAFT, recordKey } from './feed/records';

export const manifest: SubsystemManifest = {
  key: 'plugins',
  title: 'Плагины',
  order: 94,
  noPill: true,
  slots: {
    // Запись черновика в ленте: ключ `plugins:plugin_draft` (module_record)
    'chat-item-tool': [
      { name: recordKey(RECORD_DRAFT), render: (ctx: ChatItemToolCtx) => <DraftAnchor ctx={ctx} /> },
    ],
  },
};
