// Чистый экспорт манифеста подсистемы «Архитектура» (SubsystemManifest) для MF-remote
// modules/architecture. Раздел — не вкладка хаба, а панель рельсы + документ в центре
// воркспейса, поэтому tab нет: только вклады в два слота каркаса. Флаг `architecture`
// фича не читает — его проверяет оболочка на рендере вкладов.

import { DraftingCompass } from 'lucide-react';
import type {
  SubsystemManifest, WorkspacePanelArchCtx, WorkspaceCenterDocCtx,
} from '../../lib/subsystems/registryCore';
import { ArchitecturePanel } from './ArchitecturePanel';
import { ArchitectureDocument } from './ArchitectureDocument';

export const manifest: SubsystemManifest = {
  key: 'architecture',
  title: 'Архитектура',
  icon: <DraftingCompass size={16} />,
  order: 60,
  slots: {
    // --- Панель рельсы проекта (ключ панели arch) ---
    'workspace-panel': [
      {
        name: 'architecture', order: 20,
        render: (ctx: WorkspacePanelArchCtx) => (
          <ArchitecturePanel projectId={ctx.projectId} archOpen={ctx.archOpen} onEnsureOpen={ctx.onEnsureOpen} onCollapse={ctx.onCollapse} />
        ),
      },
    ],
    // --- Документ центральной зоны воркспейса ---
    'workspace-center-doc': [
      {
        name: 'arch', order: 10,
        render: (ctx: WorkspaceCenterDocCtx) => (
          <ArchitectureDocument projectId={ctx.projectId} projectName={ctx.projectName} isMobile={ctx.isMobile} onClose={ctx.onClose} onShowFile={ctx.onShowFile} />
        ),
      },
    ],
  },
};
