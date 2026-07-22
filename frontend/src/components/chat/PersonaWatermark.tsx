import type { Persona } from '../../types';
import { PersonaFace } from '../../features/personas/PersonaFace';

// Лицо стоит под колонкой сообщений и растворяется к обоим краям — без резной
// кромки сбоку. Ядро в полную силу узкое (44–56%), спад к краям длинный и с
// промежуточной ступенью: так центр читается плотно, а бока уходят в ничто
const FADE = 'linear-gradient(to right, '
  + 'transparent 0%, rgba(0,0,0,0.3) 20%, rgba(0,0,0,0.75) 34%, #000 44%, '
  + '#000 56%, rgba(0,0,0,0.75) 66%, rgba(0,0,0,0.3) 80%, transparent 100%)';

/**
 * Собеседник фоном ленты чата: лицо персоны во всю область, приглушённое до
 * водяного знака. Живёт вне области прокрутки, поэтому не едет за сообщениями.
 *
 * Прозрачность заметно ниже, чем у подложки в карточке чата: там текст в зону лица
 * не заходил вовсе, а здесь поверх идёт сама переписка.
 */
export function PersonaWatermark({ persona }: { persona: Persona }) {
  const hasPhoto = persona.avatar?.kind === 'image';
  return (
    <PersonaFace
      persona={persona}
      align="center"
      // Кегль от высоты области: инициалы должны заполнять её, а не висеть строчкой
      fontSize="clamp(160px, 38vh, 380px)"
      style={{
        position: 'absolute', inset: 0,
        pointerEvents: 'none', userSelect: 'none',
        WebkitMaskImage: FADE, maskImage: FADE,
        // Инициалы — сплошная заливка буквами, потому глуше фотографии
        opacity: hasPhoto ? 0.2 : 0.16,
      }}
    />
  );
}
