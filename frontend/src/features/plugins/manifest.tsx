// Чистый экспорт манифеста подсистемы «Плагины» (SubsystemManifest, ADR-031).
// Ключ совпадает с PluginsSubsystem.Key бэкенда: гейт слотов сверяется с активными
// подсистемами из /api/auth/me. Вклад — только в ленту чата: карточка черновика
// build-it-yourself (запись plugin_draft). Экран «Плагины» и слот панели не затрагиваются.
//
// DraftAnchor — ленивый импорт (React.lazy): статический import './feed/records'
// тянет DraftFeedCard → глубокую цепочку (api, signalr, …), которая транзитивно
// возвращается в registry/registryCore и оставляет manifest неинициализированным
// при top-level registerSubsystem (цикл M1). Константы RECORD_DRAFT/recordKey —
// из лист-модуля feed/recordKeys (без тяжёлых импортов), чтобы не тянуть records.tsx.

import { lazy, Suspense } from 'react';
import type { ChatItemToolCtx, SubsystemManifest } from '../../lib/subsystems/registryCore';
import { RECORD_DRAFT, recordKey } from './feed/recordKeys';

const DraftAnchor = lazy(() => import('./feed/records').then(m => ({ default: m.DraftAnchor })));

export const manifest: SubsystemManifest = {
  key: 'plugins',
  title: 'Плагины',
  order: 94,
  noPill: true,
  slots: {
    // Запись черновика в ленте: ключ `plugins:plugin_draft` (module_record).
    // Граница Suspense здесь (не в ядре): ленточный слот рендерят прямым вызовом,
    // а null-заглушка в ленте корректна (строка занимает своё место, макет не «мигает»).
    'chat-item-tool': [
      { name: recordKey(RECORD_DRAFT), render: (ctx: ChatItemToolCtx) => (
        <Suspense fallback={null}><DraftAnchor ctx={ctx} /></Suspense>
      ) },
    ],
  },
};
