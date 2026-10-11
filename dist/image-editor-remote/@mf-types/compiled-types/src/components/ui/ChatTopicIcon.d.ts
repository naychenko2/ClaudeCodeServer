interface Props {
    topic?: string | null;
    size?: number;
    color?: string;
}
/**
 * Значок темы чата — монохромная lucide-иконка по имени Session.topic.
 * Стоит перед именем в списках и шапке: тема узнаётся быстрее, чем читается текст.
 * Незнакомое имя (модель промахнулась / бэк ушёл вперёд) молча не рисуется — чат не ломается.
 */
export declare function ChatTopicIcon({ topic, size, color }: Props): import("react").JSX.Element | null;
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
export declare function ChatTopicBackdrop({ topic, size, opacity, align }: {
    topic?: string | null;
    size?: number;
    opacity?: number;
    align?: 'left' | 'right';
}): import("react").JSX.Element | null;
export {};
