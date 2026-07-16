import type { AuthState } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { AgentKanban } from './AgentKanban';
import { C, FONT } from '../../lib/design';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}

export function AgentKanbanPage({ auth, onLogout, onHubTab }: Props) {
  const onOpenChat = (sessionId: string) => {
    // Переход в чаты с открытием конкретной сессии
    window.location.hash = `#/chats/${sessionId}`;
  };

  return (
    <div style={{
      height: '100dvh', background: C.bgMain, fontFamily: FONT.sans,
      display: 'flex', flexDirection: 'column', overflow: 'hidden',
    }}>
      <HubHeader value="agent-kanban" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{
          maxWidth: 1180, margin: '0 auto',
          padding: '20px 32px 0', boxSizing: 'border-box',
        }}>
          <h1 style={{
            margin: 0, fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading,
            marginBottom: 16,
          }}>
            Диспетчер
          </h1>
          <AgentKanban onOpenChat={onOpenChat} />
        </div>
      </div>
    </div>
  );
}
