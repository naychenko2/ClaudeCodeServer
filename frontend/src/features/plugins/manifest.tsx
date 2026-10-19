// Чистый экспорт манифеста подсистемы «Плагины» (SubsystemManifest, ADR-031).
// Ключ совпадает с PluginsSubsystem.Key бэкенда: гейт слотов сверяется с активными
// подсистемами из /api/auth/me. Вклад — только в ленту чата: карточка черновика
// build-it-yourself (запись plugin_draft). Экран «Плагины» и слот панели не затрагиваются.
//
// DraftAnchor — ленивый импорт (React.lazy): статический import './feed/records'
// тянет DraftFeedCard → глубокую цепочку (api, signalr, …), которая транзитивно
// возвращается в registry/registryCore и оставляет manifest неинициализированным
// при top-level registerSubsystem (цикл M1). Константы RECORD_DRAFT/recordKey
// продублированы здесь, чтобы не тянуть records.tsx в статический граф.

import { lazy } from 'react';
import type { ChatItemToolCtx, SubsystemManifest } from '../../lib/subsystems/registryCore';

const RECORD_DRAFT = 'plugin_draft';
const recordKey = (recordType: string) => `plugins:${recordType}`;

const DraftAnchor = lazy(() => import('./feed/records').then(m => ({ default: m.DraftAnchor })));

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
