import { useState } from 'react';
import type { Persona } from '../../types';
import { C, R, FONT } from '../../lib/design';
import { agentDotColor } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { personaInitials } from '../../lib/personas';

// Круглый аватар персоны. kind==='image' и есть картинка — рендерим <img>
// (с фолбэком на инициалы при ошибке загрузки). Иначе — инициалы на цветном
// фоне (цвет из палитры AGENT_COLORS через agentDotColor).
//
// fill=true — аватар растягивается по родителю (width/height 100%). Нужно для
// контейнеров, чей размер меняется плавно (плавающая кнопка AI: 36↔54 с transition),
// чтобы картинка точно совпадала с кругом на каждом кадре, а не по фиксированному
// size, который успевает рассинхронизироваться с анимацией. size при этом всё равно
// передавай — от него считается кегль инициалов (у img objectFit cover и так отлично).
export function PersonaAvatar({ persona, size = 40, fill = false }: {
  persona: Persona; size?: number; fill?: boolean;
}) {
  const [hasError, setHasError] = useState(false);
  const imageUrl = persona.avatar?.kind === 'image' ? api.personas.avatarUrl(persona) : null;

  const base: React.CSSProperties = {
    width: fill ? '100%' : size, height: fill ? '100%' : size,
    borderRadius: R.full, flexShrink: 0, userSelect: 'none',
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
        background: bg, color: C.onDark,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.4),
        lineHeight: 1,
      }}
    >
      {personaInitials(persona.name)}
    </div>
  );
}
