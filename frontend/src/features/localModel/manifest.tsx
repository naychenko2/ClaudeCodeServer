// Манифест фичи «Локальные модели» (флаг local-model-stats). Статическая фича:
// бэковой подсистемы с тумблером у неё нет — гейт включённости не применяется
// (core), доступность решают вклады: флаг + admin + configured (isAvailable)
// и сама панель (то же условие внутри — null прячет кнопку).

import { Cpu } from 'lucide-react';
import type {
  SubsystemManifest, WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { FLAGS, getFlag } from '../../lib/featureFlags';
import { getLocalModelAdmin, getLocalModelConfigured, isLocalModelPanelVisible } from './useLocalModelStats';
import { lazy } from 'react';
const LocalModelPanel = lazy(() => import('./LocalModelPanel').then(m => ({ default: m.LocalModelPanel })));

// Ключ панели в каталоге (panelCatalog)
export const LOCAL_MODEL_PANEL = 'localModel';

export const manifest: SubsystemManifest = {
  key: 'localmodel',
  title: 'Локальные модели',
  icon: <Cpu size={18} strokeWidth={2} />,
  order: 98,
  noPill: true,
  core: true,
  slots: {
    // Панель правого рельса: значок есть, только когда флаг + admin + configured
    'workspace-panel-def': [
      {
        name: LOCAL_MODEL_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <LocalModelPanel ctx={ctx} />,
        action: {
          title: 'Локальные модели',
          icon: <Cpu size={16} strokeWidth={2} />,
          // Геттеры вне React: isAvailable вызывается из рендера страницы
          isAvailable: (_projectId: string | null) => isLocalModelPanelVisible({
            flag: getFlag(FLAGS.localModelStats),
            isAdmin: getLocalModelAdmin(),
            configured: getLocalModelConfigured(),
          }),
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
  },
};
