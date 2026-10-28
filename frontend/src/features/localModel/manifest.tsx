// Манифест фичи «Локальные модели» (флаг local-model-stats). Статическая фича:
// бэковой подсистемы с тумблером у неё нет — гейт включённости не применяется
// (core), доступность решает isLocalModelPanelVisible: admin — флаг + configured,
// не-админ — только configured (isAvailable) и сама панель (то же условие внутри).
// «configured» — одно выражение на значок и панель: основной/медиа ИЛИ последний
// снимок второй дорожки (isLocalModelConfiguredAny, «Bonsai без vLLM тоже
// configured»). Плюс Ф6: геттер indicator — точка состояния на значке рельса.

import { Cpu } from 'lucide-react';
import type {
  SubsystemManifest, WorkspacePanelDefApi, WorkspacePanelDefCtx,
} from '../../lib/subsystems/registryCore';
import { FLAGS, getFlag } from '../../lib/featureFlags';
import { C, ISLAND } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { getLocalModelAdmin, isLocalModelPanelVisible } from './useLocalModelStats';
import { getLocalModelBonsai, isLocalModelConfiguredAny } from './useLocalModelWatcher';
import { standDot, standHint } from './format';
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
    // Панель правого рельса: значок есть, только когда панель видима
    // (admin — флаг + configured, не-админ — configured)
    'workspace-panel-def': [
      {
        name: LOCAL_MODEL_PANEL,
        render: (ctx: WorkspacePanelDefCtx) => (
          <Suspense fallback={<PanelLoadingPlaceholder />}><LocalModelPanel ctx={ctx} /></Suspense>
        ),
        action: {
          title: 'Локальные модели',
          icon: <Cpu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          // Геттеры вне React: isAvailable вызывается из рендера страницы.
          // «Bonsai без vLLM тоже configured» (макет §8.6, разрез принял «да»): когда
          // основная дорожка не настроена, значим и configured из лёгкого опроса второй
          // дорожки — значок есть вход в стенд Bonsai. Для не-админа и админа без флага
          // опроса Bonsai нет вовсе — здесь по-прежнему только настроенный ComfyUI.
          isAvailable: (_projectId: string | null) => isLocalModelPanelVisible({
            flag: getFlag(FLAGS.localModelStats),
            isAdmin: getLocalModelAdmin(),
            configured: isLocalModelConfiguredAny(),
          }),
          // Точка состояния на значке рельса (Ф6): состояние второй модели из лёгкого
          // опроса сторожа, а НЕ из снимка текущего стенда — точка горит и при закрытой
          // панели, а тяжёл опрос живёт только пока она открыта (ревью Ф2, MAJOR 2).
          // Состояние известно только админу с флагом (не-админ /stats не спрашивает) —
          // для остальных точки нет; формата состояния нет (опрос ещё не ответил) — тоже нет.
          indicator: () => {
            if (!getLocalModelAdmin() || !getFlag(FLAGS.localModelStats)) return null;
            const data = getLocalModelBonsai();
            if (!data) return null;
            const dot = standDot(data);
            if (!dot) return null;
            return { dot, hint: standHint(data) ?? undefined };
          },
        } satisfies WorkspacePanelDefApi as unknown as Record<string, unknown>,
      },
    ],
  },
};
