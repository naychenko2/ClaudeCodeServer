import { useState } from 'react';
import type { Persona } from '../../types';
import { R, FONT } from '../../lib/design';
import { agentDotColor } from '../../components/AgentSelector';
import { api } from '../../lib/api';

// Инициалы из имени: две первые буквы (по словам, иначе первые две буквы слова)
function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

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
      {initials(persona.name)}
    </div>
  );
}
