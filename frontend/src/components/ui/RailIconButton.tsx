import { useState, type HTMLAttributes, type ReactNode } from 'react';
import { useCanHover } from '../../lib/pointer';
import { IconButton, type IconButtonVariant } from './IconButton';
import { RailFlyout, type RailFlyoutAction } from './RailFlyout';
import { RAIL_W } from './RailCapsule';

// Кнопка вертикальной рельсы: icon-кнопка плюс подпись сбоку по наведению. Общая
// для рельсы панелей (иконки панелей, тумблер режима, «свернуть все») и дока
// проектов (создание, поиск, сами проекты) — раньше этот сэндвич был выписан в
// каждом месте заново и расходился при первой же правке.
//
// hover живёт здесь, а не в IconButton: тому он нужен только для собственных
// цветов и наружу не отдаётся, а рельсе он нужен снаружи — по нему подменяется
// иконка (крестик у открытой панели) и показывается подпись.

// Пальцем наведения нет вовсе: браузер шлёт эмулированный mouseenter при тапе, а
// mouseleave не приходит, пока не тапнут в другое место, — подпись висела на экране
// до следующего тапа. Гасить её по клику мало (тапают и мимо кнопок), поэтому hover
// там просто не заводится: имя кнопки на планшете и так читает ariaLabel. Способ
// узнать про палец — useCanHover: media query на планшете с клавиатурой врёт, см.
// lib/pointer.
export function RailIconButton({
  side, label, hint, action, active, disabled, variant, wrapper, hoverSuppressed, onClick, onHoverChange, children,
}: {
  side: 'left' | 'right';
  // media — внутри картинка (иконка проекта), а не штриховой глиф; см. IconButton
  variant?: IconButtonVariant;
  // Текст подписи. Он же имя кнопки для скринридера — нативный тултип не ставится,
  // иначе подсказка приезжала бы дважды.
  label: string;
  // Подзаголовок-расшифровка под названием в плашке (что значит число-кружок).
  // Строка или список линий с тоном; в ariaLabel сцепляем с названием — на таче плашки
  // нет, и скринридер так дочитает.
  hint?: string | readonly { text: string; tone?: 'accent' | 'muted' }[];
  // Кнопка внутри подписи (у иконки проекта — настройки). Не задана — подпись просто
  // называет кнопку.
  action?: RailFlyoutAction;
  active?: boolean;
  disabled?: boolean;
  // Атрибуты обёртки: метки для зоны (data-rail-item), ручки перетаскивания,
  // pointer-события дока. Обёртка нужна как раз потому, что кнопка-примитив своего
  // API под них не имеет и дырявить его ради этого не стоит.
  wrapper?: HTMLAttributes<HTMLElement>;
  // Погасить подпись, не трогая сам hover: во время перетаскивания она лезла бы
  // поверх места вставки и рассказывала не про то, чем человек занят.
  hoverSuppressed?: boolean;
  onClick?: () => void;
  // Наведение наружу — зоне панелей: по нему она показывает место будущей панели
  onHoverChange?: (hover: boolean) => void;
  // Функцией — когда содержимое зависит от наведения (иконка панели под курсором
  // становится крестиком закрытия)
  children: ReactNode | ((hover: boolean) => ReactNode);
}) {
  const [hover, setHover] = useState(false);
  const canHover = useCanHover();
  const set = (v: boolean) => {
    if (v && !canHover) return;
    setHover(v);
    onHoverChange?.(v);
  };
  const ariaLabel = hint
    ? `${label}: ${typeof hint === 'string' ? hint : hint.map(l => l.text).join(', ')}`
    : label;
  return (
    <span
      {...wrapper}
      onMouseEnter={e => { set(true); wrapper?.onMouseEnter?.(e); }}
      onMouseLeave={e => { set(false); wrapper?.onMouseLeave?.(e); }}
      style={{ display: 'flex', ...wrapper?.style }}
    >
      <RailFlyout
        side={side}
        label={label}
        hint={hint}
        open={hover && !hoverSuppressed}
        action={action}
        railWidth={RAIL_W}
      >
        <IconButton size="md" variant={variant} onClick={onClick} active={active} disabled={disabled} ariaLabel={ariaLabel}>
          {typeof children === 'function' ? children(hover) : children}
        </IconButton>
      </RailFlyout>
    </span>
  );
}
