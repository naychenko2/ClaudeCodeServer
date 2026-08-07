import { AgentKanban } from './AgentKanban';
import { C, FONT } from '../../lib/design';
import { PageCanvas } from '../../components/ui/PageCanvas';

// Эта страница больше не маршрутизируется — диспетчер встроен в NotificationsPage.
// Оставлена для обратной совместимости импортов.
export function AgentKanbanPage() {
  return (
    <PageCanvas>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '20px 32px 0' }}>
        <h1 style={{
          margin: 0, fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading,
          marginBottom: 16,
        }}>
          Диспетчер
        </h1>
        <AgentKanban />
      </div>
    </PageCanvas>
  );
}
