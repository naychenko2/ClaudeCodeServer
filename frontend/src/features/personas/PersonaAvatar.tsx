import { useState } from 'react';
import type { Persona } from '../../types';
import { R, FONT } from '../../lib/design';
import { agentDotColor } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { personaInitials } from '../../lib/personas';

// Круглый аватар персоны. kind==='image' и есть картинка — рендерим <img>
// (с фолбэком на инициалы при ошибке загрузки). Иначе — инициалы на цветном
// фоне (цвет из палитры AGENT_COLORS через agentDotColor).
export function PersonaAvatar({ persona, size = 40 }: { persona: Persona; size?: number }) {
  const [hasError, setHasError] = useState(false);
  const imageUrl = persona.avatar?.kind === 'image' ? api.personas.avatarUrl(persona) : null;

  const base: React.CSSProperties = {
    width: size, height: size, borderRadius: R.full, flexShrink: 0, userSelect: 'none',
  };

  if (imageUrl && !hasError) {
    return (
      <img
        src={imageUrl}
        alt=""
        aria-hidden
        onError={() => setHasError(true)}
        style={{ ...base, objectFit: 'cover', display: 'block' }}
      />
    );
  }

  const bg = agentDotColor(persona.avatar?.color);
  return (
    <div
      aria-hidden
      style={{
        ...base,
        background: bg, color: '#fff',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.4),
        lineHeight: 1,
      }}
    >
      {personaInitials(persona.name)}
    </div>
  );
}
