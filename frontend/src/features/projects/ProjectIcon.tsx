import { useEffect, useState } from 'react';
import type { Project } from '../../types';
import { C, FONT } from '../../lib/design';
import { api } from '../../lib/api';
import { projectInitials, projectMainColor } from './projectUtil';

// Единая иконка проекта (по образцу PersonaAvatar, но КВАДРАТНАЯ со скруглением —
// чтобы отличаться от круглых персон). kind==='image' и есть картинка — рендерим <img>
// (с фолбэком на инициалы при ошибке). Иначе — две буквы на цветном фоне.
// Цвет: icon.color из палитры AGENT_COLORS; если не задан — детерминированный
// projectColor(id), чтобы старые проекты без иконки не «поблели».
//
// muted=true — приглушённый режим спящего дока: проект НЕ в фокусе, иконка
// обесцвечивается (картинка — grayscale с прозрачностью, плашка — бледный контур
// с инициалами). Активный проект приглушённым НЕ бывает — он всегда цветной, а
// выбор док отмечает кольцом кнопки (RailIconButton); на фоне спящих он и читается
// как выбранный. Цвет возвращается к остальным при наведении курсора на рельсу.
export function ProjectIcon({ project, size = 40, radius, muted, imageUrl: imageUrlOverride }: { project: Project; size?: number; radius?: number; muted?: boolean; imageUrl?: string | null }) {
  const [hasError, setHasError] = useState(false);
  // imageUrlOverride — локальный objectURL для превью ещё не сохранённой картинки (диалог создания).
  const imageUrl = imageUrlOverride ?? (project.icon?.kind === 'image' ? api.projects.iconUrl(project) : null);
  // Сброс ошибки при смене картинки — иначе после одного сбоя hasError залипает и валидный
  // новый src (перекроп/новый кандидат) не рисуется (компонент не перемонтируется по смене пропа).
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс флага ошибки загрузки при смене URL иконки
  useEffect(() => { setHasError(false); }, [imageUrl]);
  const br = radius ?? Math.round(size * 0.22);

  const base: React.CSSProperties = {
    width: size, height: size, borderRadius: br, flexShrink: 0, userSelect: 'none',
  };

  // Картинка сильнее контура: у кого иконка есть, того узнают по ней мгновенно, а две
  // буквы пришлось бы читать. Поэтому в покое рельсы картинка остаётся картинкой, но
  // ОБЕСЦВЕЧЕННОЙ — форму сохраняет, а цветным пятном в спящем ряду не висит. Цвет
  // возвращается вместе с пробуждением ряда (muted снят) или у активного (muted=false).
  if (imageUrl && !hasError) {
    return (
      <img
        src={imageUrl}
        alt=""
        aria-hidden
        draggable={false}
        onError={() => setHasError(true)}
        style={{
          ...base, objectFit: 'cover', display: 'block',
          transition: 'filter 0.15s, opacity 0.15s',
          ...(muted ? { filter: 'grayscale(1)', opacity: 0.7 } : null),
        }}
      />
    );
  }

  // Приглушённый режим для проектов без картинки: бледная рамка и инициалы вместо
  // цветной плашки. Спящий ряд держит вес, а не пестрит цветными квадратами — выбор
  // при этом метится кольцом кнопки, а не заливкой этой иконки.
  if (muted) {
    return (
      <div
        aria-hidden
        style={{
          ...base,
          border: `1px solid ${C.border}`, boxSizing: 'border-box',
          color: C.textMuted,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontFamily: FONT.sans, fontWeight: 600, fontSize: Math.round(size * 0.34),
          lineHeight: 1,
        }}
      >
        {projectInitials(project.name)}
      </div>
    );
  }

  const bg = projectMainColor(project);
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
