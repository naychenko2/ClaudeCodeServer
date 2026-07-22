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
