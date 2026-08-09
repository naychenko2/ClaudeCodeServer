// Плашка с текстом — цветная метка состояния: статус документа, вид уведомления,
// состояние комментария.
//
// Зачем примитив: такой чип уже был нарисован руками в восьми местах (комментарии
// к документам, карточка задачи, чипы срока и метки, расход, происхождение чата,
// автоматизации персон, виды уведомлений), и габарит успел разъехаться — где 1px
// отступа, где 3px, где радиус 11, где R.sm. Ровно та же история, что когда-то
// случилась с FileTypeTile.
//
// Тон — РОЛЬ, а не цвет: набор ограничен парами токенов, которые в теме уже есть,
// поэтому плашка одинаково читается в светлой и тёмной теме и не требует нового цвета.

import type { CSSProperties, ReactNode } from 'react';
import { C, FS, R, SP } from '../../lib/design';
import { Dot } from './Dot';
import { useIsMobile } from '../../lib/breakpoints';

export type BadgeTone =
  | 'neutral' | 'accent' | 'success' | 'warning' | 'danger' | 'info' | 'plan';

export type BadgeSize = 'xs' | 'sm';

// Пары «фон + текст». У info отдельного infoText в теме нет — на его подложке
// работает сам C.info; заводить новый токен ради одной плашки незачем
const TONE: Record<BadgeTone, { bg: string; fg: string }> = {
  neutral: { bg: C.bgInset, fg: C.textMuted },
  accent: { bg: C.accentLight, fg: C.accent },
  success: { bg: C.successBg, fg: C.successText },
  warning: { bg: C.warningBg, fg: C.warningText },
  danger: { bg: C.dangerBg, fg: C.dangerText },
  info: { bg: C.infoBg, fg: C.info },
  plan: { bg: C.planLight, fg: C.planText },
};

// Цвет точки/акцента тона — когда плашка не помещается и остаётся один кружок
export const TONE_DOT: Record<BadgeTone, string> = {
  neutral: C.textMuted,
  accent: C.accent,
  success: C.success,
  warning: C.warning,
  danger: C.danger,
  info: C.info,
  plan: C.plan,
};

interface Props {
  tone?: BadgeTone;
  size?: BadgeSize;              // xs — плотные ряды, sm — шапки и формы
  icon?: ReactNode;              // иконка 11px слева
  dot?: boolean;                 // вместо иконки — кружок в цвете тона
  children: ReactNode;
  title?: string;
  // Есть обработчик — плашка становится кнопкой (меню выбора значения)
  onClick?: (e: React.MouseEvent<HTMLElement>) => void;
  active?: boolean;              // попап от плашки открыт
  disabled?: boolean;
  style?: CSSProperties;
}

export function Badge({
  tone = 'neutral', size = 'sm', icon, dot, children,
  title, onClick, active, disabled, style,
}: Props) {
  const t = TONE[tone];
  const isMobile = useIsMobile();
  const base: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: SP.xs,
    padding: size === 'xs' ? `1px ${SP.sm}px` : `3px ${SP.sm}px`,
    borderRadius: R.max,
    fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4,
    color: t.fg, background: t.bg,
    // Многоточие живёт на внутреннем span: у inline-flex textOverflow не работает,
    // и длинное значение обрезалось бы на полбукве без всякого признака обрезки
    overflow: 'hidden', flexShrink: 0, maxWidth: '100%',
    border: 'none',
    ...style,
  };

  const body = (
    <>
      {dot && <Dot color={TONE_DOT[tone]} size={7} />}
      {icon}
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
        {children}
      </span>
    </>
  );

  if (!onClick) return <span title={title} style={base}>{body}</span>;

  return (
    <button
      type="button"
      title={title}
      disabled={disabled}
      onClick={onClick}
      // Плашка-кнопка всегда открывает выбор значения — сообщаем это и голосовому доступу
      aria-haspopup="menu"
      aria-expanded={!!active}
      style={{
        ...base,
        cursor: disabled ? 'default' : 'pointer',
        // Цель нажатия: на тач у пальца нет пиксельной точности, одной высоты текста мало
        minHeight: isMobile ? 40 : size === 'xs' ? 22 : 26,
        padding: isMobile ? `6px ${SP.md}px` : base.padding,
        opacity: disabled ? 0.6 : 1,
        // Открытый попап — обводка тем же тоном: заливку менять нельзя, она несёт смысл
        boxShadow: active ? `inset 0 0 0 1px ${t.fg}` : 'none',
      }}
    >
      {body}
    </button>
  );
}
