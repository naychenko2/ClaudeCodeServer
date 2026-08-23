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
// speaking — цвет колец «сейчас говорит»: аватар оборачивается пульсом этого цвета,
// пока её голосом читают ответ (источник — SpeakingItemContext ленты). Не задан —
// рендер ровно как раньше, без обёрток.
export function PersonaAvatar({ persona, size = 40, fill = false, speaking }: {
  persona: Persona; size?: number; fill?: boolean; speaking?: string;
}) {
  const face = <PersonaFace persona={persona} size={size} fill={fill} />;
  if (!speaking) return face;
  return <SpeakingHalo color={speaking} size={size} fill={fill}>{face}</SpeakingHalo>;
}

// Два расходящихся кольца вокруг лица (те же .cc-echo-ring, что у индикатора ожидания,
// но мелким размахом: аватар сидит внутри пузыря с overflow: hidden). При
// prefers-reduced-motion колец нет вовсе — иначе от них осталась бы статичная обводка.
function SpeakingHalo({ color, size, fill, children }: {
  color: string; size: number; fill: boolean; children: React.ReactNode;
}) {
  const reduced = typeof window !== 'undefined'
    && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  return (
    <span style={{
      position: 'relative', display: 'inline-flex', flexShrink: 0,
      width: fill ? '100%' : size, height: fill ? '100%' : size,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      ...({ ['--cc-echo-color' as any]: color }),
    }}>
      {children}
      {!reduced && (
        <>
          <span className="cc-echo-ring cc-echo-ring--tight" />
          <span className="cc-echo-ring cc-echo-ring--2 cc-echo-ring--tight" />
        </>
      )}
    </span>
  );
}

function PersonaFace({ persona, size, fill }: {
  persona: Persona; size: number; fill: boolean;
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
