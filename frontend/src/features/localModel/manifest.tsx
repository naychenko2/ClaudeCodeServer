// Манифест фичи «Локальные модели» (флаг local-model-stats). Статическая фича:
// бэковой подсистемы с тумблером у неё нет — гейт включённости не применяется
// (core), доступность решают вклады: флаг + admin + configured (isAvailable)
// и сама панель (то же условие внутри — null прячет кнопку).

import { Cpu } from 'lucide-react';
import type {
  SubsystemManifest, WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { FLAGS, getFlag } from '../../lib/featureFlags';
import { C, ISLAND } from '../../lib/design';
import { getLocalModelAdmin, getLocalModelConfigured, isLocalModelPanelVisible } from './useLocalModelStats';
import { lazy, Suspense } from 'react';
const LocalModelPanel = lazy(() => import('./LocalModelPanel').then(m => ({ default: m.LocalModelPanel })));

// Заглушка на время загрузки ленивого чанка панели: каркас один в один как у PanelShell
// (Island: рамка/тень/радиус + 40px-шапка + белое тело). Без неё панель (весь остров)
// на миг исчезает, и макет «мигает»; заглушка того же размера держит раскладку.
function PanelLoadingPlaceholder() {
  return (
    <div style={{
      display: 'flex', flexDirection: 'column', overflow: 'hidden', minHeight: 0,
      background: ISLAND.bg, border: `1px solid ${ISLAND.border}`,
      borderRadius: ISLAND.radius, boxShadow: ISLAND.shadow,
    }}>
      <div style={{ flexShrink: 0, height: ISLAND.headerH, background: ISLAND.headerBg, borderBottom: `1px solid ${C.border}` }} />
      <div style={{ flex: 1, minHeight: 0, background: C.bgWhite }} />
    </div>
  );
}

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
        render: (ctx: WorkspacePanelDefCtx) => (
          <Suspense fallback={<PanelLoadingPlaceholder />}><LocalModelPanel ctx={ctx} /></Suspense>
        ),
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
