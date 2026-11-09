import { lucideGlyphName } from '../../lib/lucideIcon';
import { C } from '../../lib/design';
import { GlyphIcon, ICON_PROPS, ICON_SIZE } from './icons';
import { Blank } from '../../lib/projectGlyphs';

interface Props {
  // Имя lucide-компонента из Session.topic (PascalCase: Cat, Bug, User). Пустой или
  // незнакомый (модель промахнулась) — значка нет
  topic?: string | null;
  size?: number;
  // Цвет значка: по умолчанию приглушённый, чтобы тема не спорила с самим именем
  color?: string;
}

/**
 * Значок темы чата — монохромная lucide-иконка по имени Session.topic.
 * Стоит перед именем в списках и шапке: тема узнаётся быстрее, чем читается текст.
 * Незнакомое имя (модель промахнулась / бэк ушёл вперёд) молча не рисуется — чат не ломается.
 */
export function ChatTopicIcon({ topic, size = ICON_SIZE.xs, color = C.textMuted }: Props) {
  const name = lucideGlyphName(topic);
  if (!name) return null;
  // Каталога подписей больше нет — tooltip показывает само имя компонента.
  // Пока чанк значка едет, обёртка держит его размер (minWidth/minHeight) — строка не прыгает
  return (
    <span title={topic ?? undefined} aria-label={topic ?? undefined}
      style={{ display: 'flex', flexShrink: 0, color, minWidth: size, minHeight: size }}>
      <GlyphIcon name={name} fallback={Blank} {...ICON_PROPS} size={size} />
    </span>
  );
}

/**
 * Тема чата как крупный полупрозрачный водяной знак в углу карточки.
 * В отличие от {@link ChatTopicIcon} (маленький значок в строке текста) — это фон ПОД
 * текстом: имени не сдвигает, места не занимает. Низ иконки уходит за нижний срез карточки
 * и обрезается её overflow:hidden — метафора «значок вырастает из нижней панели».
 *
 * Рисуется абсолютно и позиционируется относительно карточки-родителя (position: relative),
 * поэтому вставлять надо прямым ребёнком карточки, рядом с PersonaBackdrop, ДО текстового
 * блока — тогда текст ляжет поверх.
 */
export function ChatTopicBackdrop({ topic, size = 48, opacity = 0.1, align = 'left' }: {
  topic?: string | null; size?: number; opacity?: number; align?: 'left' | 'right';
}) {
  const name = lucideGlyphName(topic);
  if (!name) return null;
  return (
    <div aria-hidden style={{
      // Край карточки (align): слева — за кромкой состояния, справа — на месте персоны,
      // когда собеседника нет и правый угол свободен. Отступ сверху — иконка стоит в углу
      // целиком, не вылезая за срез карточки
      position: 'absolute', [align]: 7, top: 6, width: size, height: size,
      color: C.textSecondary, opacity, pointerEvents: 'none', zIndex: 0,
      display: 'flex', alignItems: 'flex-start',
      justifyContent: align === 'right' ? 'flex-end' : 'flex-start',
    }}>
      <GlyphIcon name={name} fallback={Blank} {...ICON_PROPS} size={size} />
    </div>
  );
}
