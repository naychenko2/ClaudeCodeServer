import { useState, type CSSProperties } from 'react';
import type { Persona } from '../../types';
import { FONT } from '../../lib/design';
import { api } from '../../lib/api';
import { personaInitials } from '../../lib/personas';
import { agentDotColor } from '../../components/AgentSelector';

interface Props {
  persona: Persona;
  // К какому краю прижимать лицо — картинку кропом, инициалы выравниванием
  align: 'left' | 'right' | 'center';
  // Кегль инициалов, когда фото нет. Строкой — если нужен clamp/vh под высоту области
  fontSize: number | string;
  // Геометрия целиком на потребителе: он же задаёт маску и прозрачность
  style: CSSProperties;
}

// Лицо плотное у правого края и тает влево; хвост доводит до левого края цветовая вуаль
const BACKDROP_FADE = 'linear-gradient(to left, #000 40%, transparent)';

// Стоп цветовой вуали. Цвета персон — hex, но фолбэк палитры это CSS-переменная,
// к которой альфу не приклеить, поэтому для неё считаем прозрачность через color-mix
function veilStop(color: string, alpha: number, pos: number): string {
  const c = /^#[0-9a-f]{6}$/i.test(color)
    ? color + Math.round(alpha * 255).toString(16).padStart(2, '0')
    : `color-mix(in srgb, ${color} ${Math.round(alpha * 100)}%, transparent)`;
  return `${c} ${pos}%`;
}

/**
 * Собеседник у правого края контейнера (position:relative + overflow:hidden на нём):
 * лицо персоны почти в полную силу плюс вуаль её цветом, уводящая изображение влево.
 * Используется карточками списка чатов и hero-шапкой открытого чата.
 * Прозрачность у фото и инициалов разная: буквы визуально легче фотографии
 * и при равной прозрачности выглядели бы бледнее.
 */
export function PersonaBackdrop({ persona, width = 84, fontSize = 38 }: {
  persona: Persona;
  // Ширина полосы лица у правого края
  width?: number;
  // Кегль инициалов-фолбэка без фото
  fontSize?: number;
}) {
  const color = agentDotColor(persona.avatar?.color);
  const hasPhoto = persona.avatar?.kind === 'image';

  return (
    <>
      {/* Вуаль цветом персоны: подхватывает лицо у его края и длинной мягкой
          растяжкой уводит цвет влево — стык картинки с фоном не читается.
          Ступени по альфе, а не один линейный переход: так спад плавнее */}
      <div aria-hidden style={{
        position: 'absolute', inset: 0, pointerEvents: 'none', opacity: 0.22,
        background: 'linear-gradient(to left, '
          + [
            veilStop(color, 1, 0),
            veilStop(color, 0.82, 16),
            veilStop(color, 0.5, 38),
            veilStop(color, 0.22, 62),
            veilStop(color, 0.06, 82),
            veilStop(color, 0, 100),
          ].join(', ') + ')',
      }} />
      <PersonaFace
        persona={persona} align="right" fontSize={fontSize}
        style={{
          position: 'absolute', top: 0, right: 0, bottom: 0, width,
          pointerEvents: 'none', userSelect: 'none',
          WebkitMaskImage: BACKDROP_FADE, maskImage: BACKDROP_FADE,
          opacity: hasPhoto ? 0.92 : 0.85,
          paddingRight: hasPhoto ? undefined : 10,
        }}
      />
    </>
  );
}

/**
 * Лицо персоны без рамки и круга: фото, а если его нет — инициалы её цветом.
 * В отличие от PersonaAvatar не задаёт форму и размер — это подложка, которую
 * потребитель вписывает в свою геометрию (полоса карточки, фон ленты чата).
 */
export function PersonaFace({ persona, align, fontSize, style }: Props) {
  const [hasError, setHasError] = useState(false);
  const imageUrl = persona.avatar?.kind === 'image' ? api.personas.avatarUrl(persona) : null;

  if (imageUrl && !hasError) {
    return (
      <img
        src={imageUrl} alt="" aria-hidden onError={() => setHasError(true)}
        // width/height обязательны и идут ДО style, чтобы потребитель мог их
        // переопределить: у замещаемого <img> абсолютные офсеты (inset) сами по себе
        // не растягивают элемент — при width:auto он остаётся своего размера
        style={{ width: '100%', height: '100%', ...style, objectFit: 'cover', objectPosition: `${align} center` }}
      />
    );
  }

  return (
    <div aria-hidden style={{
      ...style,
      display: 'flex', alignItems: 'center',
      justifyContent: align === 'right' ? 'flex-end' : align === 'center' ? 'center' : 'flex-start',
      color: agentDotColor(persona.avatar?.color),
      fontFamily: FONT.sans, fontWeight: 800, fontSize, lineHeight: 1, letterSpacing: -1,
    }}>
      {personaInitials(persona.name)}
    </div>
  );
}
