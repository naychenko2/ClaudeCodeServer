import { useState } from 'react';
import type { Project } from '../../types';
import { FONT } from '../../lib/design';
import { agentDotColor } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { projectColor } from '../../lib/tasks';
import { projectInitials } from './projectUtil';

// Единая иконка проекта (по образцу PersonaAvatar, но КВАДРАТНАЯ со скруглением —
// чтобы отличаться от круглых персон). kind==='image' и есть картинка — рендерим <img>
// (с фолбэком на инициалы при ошибке). Иначе — две буквы на цветном фоне.
// Цвет: icon.color из палитры AGENT_COLORS; если не задан — детерминированный
// projectColor(id), чтобы старые проекты без иконки не «побелели».
export function ProjectIcon({ project, size = 40, radius }: { project: Project; size?: number; radius?: number }) {
  const [hasError, setHasError] = useState(false);
  const imageUrl = project.icon?.kind === 'image' ? api.projects.iconUrl(project) : null;
  const br = radius ?? Math.round(size * 0.22);

  const base: React.CSSProperties = {
    width: size, height: size, borderRadius: br, flexShrink: 0, userSelect: 'none',
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

  const bg = project.icon?.color ? agentDotColor(project.icon.color) : projectColor(project.id).main;
  return (
    <div
      aria-hidden
      style={{
        ...base,
        background: bg, color: '#fff',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.38),
        lineHeight: 1,
      }}
    >
      {projectInitials(project.name)}
    </div>
  );
}
