import { AgentKanban } from './AgentKanban';
import { C, FONT } from '../../lib/design';

// Эта страница больше не маршрутизируется — диспетчер встроен в NotificationsPage.
// Оставлена для обратной совместимости импортов.
export function AgentKanbanPage() {
  return (
    <div style={{
      height: '100dvh', background: C.bgMain, fontFamily: FONT.sans,
      display: 'flex', flexDirection: 'column', overflow: 'hidden',
    }}>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '20px 32px 0' }}>
        <h1 style={{
          margin: 0, fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading,
          marginBottom: 16,
        }}>
          Диспетчер
        </h1>
        <AgentKanban />
      </div>
    </div>
  );
}
