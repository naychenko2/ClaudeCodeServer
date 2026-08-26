import { useState } from 'react';
import type { CSSProperties, MouseEvent, ReactNode } from 'react';
import { C, R, TB, SHADOW } from '../../lib/design';

// Единая квадратная icon-кнопка (действие-иконка) для всех тулбаров, сайдбаров и шапок.
// Заменяет ~десяток инлайновых реализаций с размерами 22..44 и радиусами 6..12.

export type IconButtonSize = 'xs' | 'sm' | 'md' | 'lg';
export type IconButtonTone = 'muted' | 'accent' | 'danger';
// ghost — прозрачная; soft — с подложкой C.bgPanel; media — внутри КАРТИНКА
// (иконка проекта, аватар), а не штриховая иконка: она занимает бокс целиком,
// поэтому заливка под ней не видна вовсе. Состояние такая кнопка сообщает самим
// содержимым (в доке проектов — цветом против контура), а от кнопки берёт только
// лёгкий подъём под курсором.
export type IconButtonVariant = 'ghost' | 'soft' | 'media';

// Единая шкала: 24(плотные строки списков/дерева) / 28 / 32 / 40(тач). Радиус — R.sm/R.md, для тач R.lg.
const SIZE: Record<IconButtonSize, { box: number; radius: number }> = {
  xs: { box: 24, radius: R.sm },
  sm: { box: 28, radius: R.md },
  md: { box: 32, radius: R.md },
  lg: { box: 40, radius: R.lg },
};

const TONE: Record<IconButtonTone, { idle: string; hoverBg: string; hoverColor: string }> = {
  muted:  { idle: TB.iconColor, hoverBg: TB.iconHoverBg, hoverColor: TB.iconColorHover },
  accent: { idle: C.accent,     hoverBg: C.accentLight,  hoverColor: C.accent },
  danger: { idle: C.textMuted,  hoverBg: C.dangerBg,     hoverColor: C.danger },
};

// Единый focus-visible ring (клавиатура) — инжектим один раз.
const FOCUS_CLASS = 'cc-iconbtn';
if (typeof document !== 'undefined' && !document.getElementById('cc-iconbtn-style')) {
  const el = document.createElement('style');
  el.id = 'cc-iconbtn-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

interface Props {
  onClick?: (e: MouseEvent) => void;
  title?: string;
  // Имя кнопки БЕЗ нативного тултипа: подсказку рисует кто-то другой (плашка
  // рельсы — RailFlyout), и браузерный title вылезал бы поверх неё вторым
  // объяснение того же самого. Задан — title не ставится вовсе.
  ariaLabel?: string;
  disabled?: boolean;
  active?: boolean;
  size?: IconButtonSize;
  tone?: IconButtonTone;
  variant?: IconButtonVariant;   // см. IconButtonVariant
  color?: string;                // переопределить цвет иконки в покое
  style?: CSSProperties;
  // Дополнительный класс корневой кнопки (склеивается с фокус-классом): нужен
  // для состояний, которыми управляет родительская зона (cc-ghost-live в
  // ghost-ряду шапки — «эту кнопку не гасить в покое»)
  className?: string;
  children: ReactNode;           // svg
}

export function IconButton({
  onClick, title, ariaLabel, disabled, active, size = 'md', tone = 'muted', variant = 'ghost', color, style, className, children,
}: Props) {
  const [hover, setHover] = useState(false);
  const s = SIZE[size];
  const t = TONE[tone];
  const media = variant === 'media';
  const base = variant === 'soft' ? C.bgPanel : 'transparent';
  // У media-кнопки картинка закрывает бокс целиком — заливка под ней невидима,
  // поэтому состояния она показывает КОЛЬЦОМ снаружи: акцентным у выбранной,
  // блёклым при наведении. Внутренний контур цветом холста отбивает кольцо от
  // самой картинки, иначе оно читается как её собственная рамка.
  const bg = media ? 'transparent' : (disabled ? base : (active ? C.accentMuted : (hover ? t.hoverBg : base)));
  const fg = disabled ? C.border : (active ? C.accent : (hover ? t.hoverColor : (color ?? t.idle)));
  // Подъём мелкий намеренно: рядом с кнопкой раскрывается подсказка ровно её высоты,
  // и заметный масштаб дал бы ступеньку на стыке.
  const mediaLift = media && hover && !disabled ? 'scale(1.05)' : undefined;
  // Выбранная media-кнопка обводится кольцом: заливку под непрозрачной картинкой не
  // видно, а знать, что выбрано, надо. Кольцо тонкое не от скромности — в 40px-рельсе
  // кнопка 32 оставляет по 3px на сторону, и всё толще вылезло бы за кромку капсулы.
  const mediaRing = media && active ? `0 0 0 1px ${C.bgMain}, 0 0 0 3px ${C.accent}` : undefined;
  return (
    <button
      className={className ? `${FOCUS_CLASS} ${className}` : FOCUS_CLASS}
      onClick={onClick}
      title={ariaLabel ? undefined : title}
      aria-label={ariaLabel ?? title}
      disabled={disabled}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        width: s.box, height: s.box, flexShrink: 0, padding: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        border: 'none', borderRadius: s.radius, cursor: disabled ? 'default' : 'pointer',
        background: bg, color: fg, transform: mediaLift, boxShadow: mediaRing,
        transition: 'background 0.12s, color 0.12s, box-shadow 0.12s, transform 0.12s',
        ...style,
      }}
    >
      {children}
    </button>
  );
}
