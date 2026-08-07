// Раздел «Аналитика токенов» как полноценная страница-вкладка хаба: главная
// шапка (HubHeader) остаётся сверху и кликабельной, контент раздела — под ней.
// Вход — через меню аватара (как «Знания»), вкладки в таббаре нет (TABLESS).
// Точки входа (виджет «Домой», бейдж чата) открывают раздел с контекстом
// (фильтр/день/паспорт хода) — ctx пробрасывается в SpendScreen.
import type { AuthState } from '../../types';
import type { HubTabValue } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PageCanvas } from '../../components/ui/PageCanvas';
import type { SpendOpenContext } from '../../lib/spend';
import { SpendScreen } from './SpendScreen';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
  ctx: SpendOpenContext;
  // Крестик/Esc — уйти с раздела (возврат на дашборд)
  onClose: () => void;
}

export function SpendPage({ auth, onLogout, onHubTab, ctx, onClose }: Props) {
  return (
    <PageCanvas>
      <HubHeader value="spend" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <SpendScreen ctx={ctx} isAdmin={auth.role === 'admin'} onClose={onClose} embedded />
      </div>
    </PageCanvas>
  );
}
