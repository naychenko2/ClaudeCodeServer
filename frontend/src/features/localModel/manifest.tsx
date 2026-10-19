// Манифест фичи «Локальные модели» (флаг local-model-stats). Статическая фича:
// бэковой подсистемы с тумблером у неё нет — гейт включённости не применяется
// (core), доступность решает isLocalModelPanelVisible: admin — флаг + configured,
// не-админ — только configured (isAvailable) и сама панель (то же условие внутри).

import { Cpu } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type {
  SubsystemManifest, WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { FLAGS, getFlag } from '../../lib/featureFlags';
import { getLocalModelAdmin, getLocalModelConfigured, isLocalModelPanelVisible } from './useLocalModelStats';
import { LocalModelPanel } from './LocalModelPanel';

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
    // Панель правого рельса: значок есть, только когда панель видима
    // (admin — флаг + configured, не-админ — configured)
    'workspace-panel-def': [
      {
        name: LOCAL_MODEL_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => <LocalModelPanel ctx={ctx} />,
        action: {
          title: 'Локальные модели',
          icon: <Cpu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
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
