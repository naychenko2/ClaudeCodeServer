// Раздел «Аналитика токенов» как полноценная страница-вкладка хаба: главная
// шапка (HubHeader) остаётся сверху и кликабельной, контент раздела — под ней.
// Вход — через меню аватара (как «Знания»), вкладки в таббаре нет (TABLESS / noPill).
// Подсистема на слотах: props — SubsystemTabProps (auth, onLogout, onHubTab).
// Контекст открытия: openSpend() (spendContract) кладёт его в stash перед
// отправкой события; при монтировании компонента мы забираем stash
// (consumeSpendContext), а при прямом заходе (#/spend без события) — дефолт.
import { useState } from 'react';
import type { SubsystemTabProps } from '../../lib/subsystems/registryCore';
import { HubHeader, PageCanvas, subsystemTabValue } from '../../lib/shell-kit';
import { consumeSpendContext, type SpendOpenContext } from '../../lib/spendContract';
import { SpendScreen } from './SpendScreen';

// Контекст открытия: при монтировании берём stash (заложил openSpend());
// если stash пуст (прямой заход на #/spend) — дефолтный пустой контекст.
// Провайдер-проп ctx — запасной путь (прямой рендер из App.tsx), но при
// рендере через ActiveSubsystemTab не передаётся; stash — основной механизм.
export function SpendPage({ auth, onLogout, onHubTab, ctx = {}, onClose = () => onHubTab('home') }: SubsystemTabProps & { ctx?: SpendOpenContext; onClose?: () => void }) {
  // useState-инициализатор: вызывается один раз при первом mount.
  // consumeSpendContext() возвращает stashed context (и обнуляет stash)
  // либо null (ничего не заложено — прямой заход).
  const [effCtx] = useState<SpendOpenContext>(() => consumeSpendContext() ?? ctx);
  return (
    <PageCanvas>
      <HubHeader value={subsystemTabValue('spend')} onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <SpendScreen ctx={effCtx} isAdmin={auth.role === 'admin'} onClose={onClose} embedded />
      </div>
    </PageCanvas>
  );
}
