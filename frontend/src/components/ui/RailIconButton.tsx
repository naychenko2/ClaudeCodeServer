import { useState, type HTMLAttributes, type ReactNode } from 'react';
import { useCanHover, TOUCH_CALLOUT_GUARD } from '../../lib/pointer';
import { useLongPress } from '../../hooks/useLongPress';
import { IconButton, type IconButtonVariant } from './IconButton';
import { RailFlyout, type RailFlyoutAction } from './RailFlyout';
import { RAIL_W } from './RailCapsule';
import type { BadgeTone } from './CountBadge';

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
// до следующего тапа. Поэтому hover от касания не заводится, а плашку там поднимает
// ДОЛГОЕ НАЖАТИЕ (тот же жест, что открывает контекстные меню в файлах и заметках):
// коротким тапом кнопка работает как работала, удержание показывает её имя и кнопки
// действий — на таче это единственный вход в них. Гасит плашку тап мимо (см.
// onDismiss у RailFlyout). Способ узнать про палец — useCanHover: media query на
// планшете с клавиатурой врёт, см. lib/pointer.
//
// Наведение и удержание — РАЗНЫЕ состояния: children получает только hover (иконка
// открытой панели подменяется крестиком под курсором, и после удержания пальцем
// такая подмена читалась бы как «сейчас закрою»), а наружу (onHoverChange —
// призрак места, попап-превью) удержание не сообщается вовсе.
export function RailIconButton({
  side, label, hint, actions, active, disabled, variant, wrapper, hoverSuppressed, onClick, onHoverChange, children,
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
  hint?: string | readonly { text: string; tone?: BadgeTone }[];
  // Кнопки внутри подписи (у иконки проекта — настройки, у кнопки панели — «убрать
  // в ящик» и «перенести на другую сторону»). Не заданы — подпись просто называет
  // кнопку.
  actions?: readonly RailFlyoutAction[];
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
  // Плашка, поднятая долгим нажатием. Отдельно от hover — см. комментарий выше.
  const [pressed, setPressed] = useState(false);
  const canHover = useCanHover();
  // Жест ловим ВСЕГДА, а не только в тач-режиме: на гибриде (планшет с
  // клавиатурой) media query отвечает «наведение умею», и до первого касания
  // canHover ещё true — обработчики, навешанные по нему, пропустили бы как раз
  // первое долгое нажатие. Мышь touch-событий не шлёт вовсе, так что лишним
  // это не будет.
  const { pressProps } = useLongPress(true);
  const set = (v: boolean) => {
    if (v && !canHover) return;
    setHover(v);
    onHoverChange?.(v);
  };
  // Обработчики удержания. Кнопка в примитиве одна, поэтому ключ списка здесь
  // формальный — хук общий с длинными списками (файлы, документы).
  const press = pressProps('rail', () => setPressed(true));
  const ariaLabel = hint
    ? `${label}: ${typeof hint === 'string' ? hint : hint.map(l => l.text).join(', ')}`
    : label;
  return (
    <span
      {...wrapper}
      {...press}
      onMouseEnter={e => { set(true); wrapper?.onMouseEnter?.(e); }}
      onMouseLeave={e => { set(false); wrapper?.onMouseLeave?.(e); }}
      // Без щита удержание поднимает ЕЩЁ И меню браузера поверх нашей плашки: Chrome
      // на Android считает кнопку с <svg> внутри картинкой и предлагает её скачать
      // (см. TOUCH_CALLOUT_GUARD). Правый клик мышью гасится тем же обработчиком —
      // своего контекстного меню у кнопки рельсы нет, а нативное здесь ни о чём.
      onContextMenu={e => e.preventDefault()}
      style={{ display: 'flex', ...TOUCH_CALLOUT_GUARD, ...wrapper?.style }}
    >
      <RailFlyout
        side={side}
        label={label}
        hint={hint}
        open={(hover || pressed) && !hoverSuppressed}
        actions={actions}
        railWidth={RAIL_W}
        onDismiss={() => setPressed(false)}
      >
        <IconButton size="md" variant={variant} onClick={() => { setPressed(false); onClick?.(); }} active={active} disabled={disabled} ariaLabel={ariaLabel}>
          {typeof children === 'function' ? children(hover) : children}
        </IconButton>
      </RailFlyout>
    </span>
  );
}
