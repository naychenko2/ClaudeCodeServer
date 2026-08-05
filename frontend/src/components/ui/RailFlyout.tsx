import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import type { LucideIcon } from 'lucide-react';
import { C, R, FS, FONT, SHADOW, Z } from '../../lib/design';
import { IconButton } from './IconButton';
import { ICON_STROKE } from './icons';

// Подсказка кнопки рельсы: пока курсор на кнопке, сбоку (со стороны центра окна)
// висит плашка с её названием, а при необходимости — и кнопка-действие.
//
// Это ОБЩЕЕ поведение всех рельс, а не украшение одной: в 40px-полосе подписей нет
// места, и раньше единственной подсказкой был нативный title — он приходит с
// задержкой браузера, выглядит инородно и уж точно не умеет носить кнопку. Отсюда
// же и настройки проекта: у иконки активного проекта в доке действие живёт в этой
// плашке, а не отдельной кнопкой, занимающей место в рельсе навсегда.
//
// Плашка ВЫЕЗЖАЕТ ИЗ-ПОД РЕЛЬСЫ, а не срастается с кнопкой: примыкает к внешней
// кромке капсулы, той же высоты, что кнопка, белая, скруглена только снаружи —
// язычок, выдвинутый из-под панели. Срастание пробовали (тон кнопки, снятое на стыке
// скругление) — кнопка от этого читалась темнее, чем при обычном наведении, а сама
// подсказка переставала быть отдельной сущностью.
// Курсор, идущий от кнопки к действию в плашке, пересекает поле капсулы, поэтому
// гашение с паузой обязательно — иначе подсказка закрывалась бы на полпути.

const HIDE_DELAY = 140;
// Высота плашки = высота кнопки рельсы (IconButton md): напротив кнопки должен
// стоять язычок её роста, а не наездник сверху.
const FLYOUT_H = 32;

export interface RailFlyoutAction {
  Icon: LucideIcon;
  title: string;
  onClick: () => void;
}

export function RailFlyout({ side, label, open, action, railWidth, children }: {
  // Сторона окна: у левой рельсы плашка растёт вправо, у правой — влево
  side: 'left' | 'right';
  label: string;
  // Курсор на кнопке. Состояние держит вызывающий — он же гасит его на старте
  // перетаскивания (браузер во время drag мышиных событий не шлёт, и hover залипает).
  open: boolean;
  // Кнопка в плашке. Не передана — плашка просто подписывает иконку.
  action?: RailFlyoutAction;
  // Ширина капсулы рельсы: от её ВНЕШНЕЙ кромки выезжает плашка. Не от кнопки —
  // язычок выходит из-под панели, а не прирастает к иконке.
  railWidth: number;
  children: ReactNode;   // сама кнопка рельсы
}) {
  const hostRef = useRef<HTMLSpanElement>(null);
  // Курсор ушёл с кнопки, но мог пойти к действию — держим плашку ещё мгновение.
  // Плашке БЕЗ действия тянуться незачем: она ничего не предлагает нажать.
  const hasAction = !!action;
  const [lingering, setLingering] = useState(false);
  const [onFlyout, setOnFlyout] = useState(false);
  // Вертикальный центр кнопки в координатах окна — по нему плашка встаёт напротив.
  // По горизонтали её место задаёт кромка рельсы (railWidth), а не кнопка.
  const [top, setTop] = useState(0);

  useEffect(() => {
    if (open) { setLingering(true); return; }
    if (!hasAction) { setLingering(false); return; }
    const id = setTimeout(() => setLingering(false), HIDE_DELAY);
    return () => clearTimeout(id);
  }, [open, hasAction]);

  // Пока курсор на самой плашке, она живёт независимо от таймера — иначе исчезала
  // бы прямо под ним, на полпути к кнопке
  const shown = open || onFlyout || lingering;

  // Кнопка-действие стоит на стороне, обращённой К ЦЕНТРУ окна: плашка правой
  // рельсы растёт влево, и действие в её хвосте оказалось бы зажатым между текстом
  // и кромкой капсулы — то есть у самого края экрана, дальше всего от того места,
  // куда идёт курсор. У левой рельсы центр справа, и хвост как раз туда и смотрит.
  const actionFirst = side === 'right';

  useLayoutEffect(() => {
    if (!shown) return;
    const el = hostRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setTop(r.top + r.height / 2);
  }, [shown, label]);

  return (
    <>
      <span ref={hostRef} style={{ display: 'flex' }}>{children}</span>
      {shown && createPortal(
        <div
          onMouseEnter={() => setOnFlyout(true)}
          onMouseLeave={() => { setOnFlyout(false); setLingering(false); }}
          style={{
            position: 'fixed', top, transform: 'translateY(-50%)', zIndex: Z.dropdown,
            // От внешней кромки рельсы: язычок выезжает ИЗ-ПОД панели
            ...(side === 'left' ? { left: railWidth } : { right: railWidth }),
            height: FLYOUT_H, display: 'flex', alignItems: 'center',
            // Поле у кнопки уже текстового: она сама держит свой бокс
            padding: action ? (actionFirst ? '0 10px 0 3px' : '0 3px 0 10px') : '0 10px',
            maxWidth: 280, boxSizing: 'border-box',
            background: C.bgWhite,
            border: `1px solid ${C.border}`,
            // Сторона, обращённая к рельсе, открыта — оттуда плашка и выехала
            ...(side === 'left'
              ? { borderLeft: 'none', borderTopRightRadius: R.md, borderBottomRightRadius: R.md }
              : { borderRight: 'none', borderTopLeftRadius: R.md, borderBottomLeftRadius: R.md }),
            boxShadow: SHADOW.dropdown,
            fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
            // Курсор над подписью — обычная стрелка: это ярлык кнопки, а не текст,
            // который зовут выделять
            cursor: 'default', userSelect: 'none',
            whiteSpace: 'nowrap', gap: 4,
          }}
        >
          {(() => {
            const btn = action && (
              <IconButton size="xs" title={action.title} onClick={action.onClick}>
                <action.Icon size={14} strokeWidth={ICON_STROKE} />
              </IconButton>
            );
            return (
              <>
                {actionFirst && btn}
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</span>
                {!actionFirst && btn}
              </>
            );
          })()}
        </div>,
        document.body,
      )}
    </>
  );
}
