// Чистый экспорт манифеста подсистемы «Аналитика» (SubsystemManifest).
// Аналог manifest.tsx подсистемы Notes: вынесен из registry.tsx, чтобы
// при переходе на MF-remote (шаг Ф2.3) можно было экспортировать без side-effect.

import { lazy } from 'react';
import { Coins } from 'lucide-react';
import type {
  SubsystemManifest,
  HomeWidgetSpendCtx, ChatHeaderBadgeCtx,
} from '../../lib/subsystems/registryCore';
import { SpendWidget } from './SpendWidget';
import { SpendBadge } from './SpendBadge';

const SpendPage = lazy(() => import('./SpendPage').then(m => ({ default: m.SpendPage })));

export const manifest: SubsystemManifest = {
  key: 'spend',
  title: 'Аналитика',
  icon: <Coins size={18} strokeWidth={2} />,
  order: 90,
  noPill: true,
  tab: { component: SpendPage },
  slots: {
    'home-widget': [
      { name: 'spend-widget', order: 20, render: (_ctx: HomeWidgetSpendCtx) => <SpendWidget /> },
    ],
    'chat-header-badge': [
      {
        name: 'spend-badge', order: 10,
        render: (ctx: ChatHeaderBadgeCtx) => (
          <SpendBadge sessionId={ctx.sessionId} chatName={ctx.chatName} resultCount={ctx.resultCount} isMobile={ctx.isMobile} />
        ),
      },
    ],
  },
};
