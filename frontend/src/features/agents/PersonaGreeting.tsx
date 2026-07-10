import type { Persona } from '../../types';
import { C, FONT } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { personaLabel } from '../../lib/personas';
import { PersonaAvatar } from './PersonaAvatar';

// Приветственный пузырь персоны в пустом чате: аватар + реплика greeting в цветах
// персоны. Чисто визуальный (в бэкенд не отправляется) — исчезает с первым сообщением.
export function PersonaGreeting({ persona }: { persona: Persona }) {
  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? C.accent;
  return (
    <div style={{
      flex: 1, display: 'flex', flexDirection: 'column',
      alignItems: 'center', justifyContent: 'center', gap: 16, padding: '40px 16px',
    }}>
      <PersonaAvatar persona={persona} size={72} />
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4 }}>
        <div style={{ fontFamily: FONT.serif, fontSize: 20, fontWeight: 500, color: accent, letterSpacing: '-0.01em' }}>
          {personaLabel(persona)}
        </div>
        {persona.description && (
          <div style={{ fontSize: 12.5, color: C.textMuted, textAlign: 'center', maxWidth: 340, lineHeight: 1.5 }}>
            {persona.description}
          </div>
        )}
      </div>
      {/* Реплика-приветствие — «пузырь» ассистента в акцент персоны */}
      <div style={{
        maxWidth: 420, padding: '12px 16px', borderRadius: '18px 18px 18px 4px',
        background: `${accent}14`, border: `1px solid ${accent}33`,
        fontSize: 14, lineHeight: 1.55, color: C.textHeading,
      }}>
        {persona.greeting}
      </div>
    </div>
  );
}
