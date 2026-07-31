import { useEffect, useState } from 'react';
import type { Project } from '../../types';
import { C, FONT } from '../../lib/design';
import { agentDotColor } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { projectColor } from '../../lib/tasks';
import { projectInitials } from './projectUtil';

// Контурные режимы иконки: приглушённый (проект не в фокусе) и контрастный
// (выбранный проект). Экспортируется, чтобы вызывающий не пересказывал строки.
export type ProjectIconOutline = 'muted' | 'strong';

// Единая иконка проекта (по образцу PersonaAvatar, но КВАДРАТНАЯ со скруглением —
// чтобы отличаться от круглых персон). kind==='image' и есть картинка — рендерим <img>
// (с фолбэком на инициалы при ошибке). Иначе — две буквы на цветном фоне.
// Цвет: icon.color из палитры AGENT_COLORS; если не задан — детерминированный
// projectColor(id), чтобы старые проекты без иконки не «побелели».
export function ProjectIcon({ project, size = 40, radius, outline, imageUrl: imageUrlOverride }: { project: Project; size?: number; radius?: number; outline?: ProjectIconOutline; imageUrl?: string | null }) {
  const [hasError, setHasError] = useState(false);
  // imageUrlOverride — локальный objectURL для превью ещё не сохранённой картинки (диалог создания).
  const imageUrl = imageUrlOverride ?? (project.icon?.kind === 'image' ? api.projects.iconUrl(project) : null);
  // Сброс ошибки при смене картинки — иначе после одного сбоя hasError залипает и валидный
  // новый src (перекроп/новый кандидат) не рисуется (компонент не перемонтируется по смене пропа).
  useEffect(() => { setHasError(false); }, [imageUrl]);
  const br = radius ?? Math.round(size * 0.22);

  const base: React.CSSProperties = {
    width: size, height: size, borderRadius: br, flexShrink: 0, userSelect: 'none',
  };

  // Контурный режим: рамка и инициалы вместо заливки, и это ЕДИНЫЙ вид для всех,
  // включая проекты с картинкой — иначе в ряду тонких контуров фото оставалось бы
  // тяжёлым пятном, ровно тем шумом, от которого контур и избавляет.
  //   muted  — «не в фокусе»: бледная рамка, приглушённые буквы;
  //   strong — выбранный проект: акцентная рамка и текст (акцент в системе и значит
  //            «активное»), но по-прежнему без заливки и картинки, чтобы вес держал
  //            весь ряд, а не одна иконка.
  if (outline) {
    const strong = outline === 'strong';
    return (
      <div
        aria-hidden
        style={{
          ...base,
          border: `1px solid ${strong ? C.accent : C.border}`, boxSizing: 'border-box',
          color: strong ? C.accent : C.textMuted,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontFamily: FONT.sans, fontWeight: strong ? 700 : 600, fontSize: Math.round(size * 0.34),
          lineHeight: 1,
        }}
      >
        {projectInitials(project.name)}
      </div>
    );
  }

  if (imageUrl && !hasError) {
    return (
      <img
        src={imageUrl}
        alt=""
        aria-hidden
        draggable={false}
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
        background: bg, color: C.onDark,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.38),
        lineHeight: 1,
      }}
    >
      {projectInitials(project.name)}
    </div>
  );
}
