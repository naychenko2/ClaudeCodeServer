// Секция «Контекст» — тонкая обёртка над PersonaContextTab (память/знания/задачи
// персоны-собеседника рядом с чатом). Контейнер перенесён из ArtifactsPanel verbatim.
import { PersonaContextTab } from '../PersonaContextTab';

export function ContextSection({ personaId, sessionId }: { personaId: string; sessionId: string | null }) {
  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 12px' }}>
      <PersonaContextTab personaId={personaId} sessionId={sessionId} />
    </div>
  );
}
