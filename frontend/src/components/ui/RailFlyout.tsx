import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import type { LucideIcon } from 'lucide-react';
import { C, R, FS, FONT, SHADOW, Z } from '../../lib/design';
import { IconButton } from './IconButton';
import { ICON_STROKE } from './icons';
import type { BadgeTone } from './CountBadge';

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

export function RailFlyout({ side, label, hint, open, action, railWidth, hostStyle, standalone, children }: {
  // Сторона окна: у левой рельсы плашка растёт вправо, у правой — влево
  side: 'left' | 'right';
  label: string;
  // Подзаголовок под названием (расшифровка чисел-кружков). Строка — одна линия с
  // оранжевой точкой; массив — по линии на каждый индикатор, точка в цвет кружка на
  // иконке (accent/primary, muted/secondary). Не задан — плашка из одной строки.
  hint?: string | readonly { text: string; tone?: BadgeTone }[];
  // Курсор на кнопке. Состояние держит вызывающий — он же гасит его на старте
  // перетаскивания (браузер во время drag мышиных событий не шлёт, и hover залипает).
  open: boolean;
  // Кнопка в плашке. Не передана — плашка просто подписывает иконку.
  action?: RailFlyoutAction;
  // Ширина капсулы рельсы: от её ВНЕШНЕЙ кромки выезжает плашка. Не от кнопки —
  // язычок выходит из-под панели, а не прирастает к иконке.
  railWidth: number;
  // Стиль обёртки-якоря. Нужен тем, кто занимает ВСЮ ширину капсулы (шляпка рельсы
  // с её чертой от кромки до кромки): по умолчанию якорь ужимается по содержимому,
  // как кнопка.
  hostStyle?: CSSProperties;
  // Плашка-таблетка: рамка и скругления со ВСЕХ сторон, а не язычок, выехавший
  // из-под капсулы. Так подписаны шляпки рельс — они называют не кнопку, а рельсу
  // целиком, и отдельная форма отличает их от подписей кнопок.
  standalone?: boolean;
  children: ReactNode;   // сама кнопка рельсы
}) {
  const hostRef = useRef<HTMLSpanElement>(null);
  const flyoutRef = useRef<HTMLDivElement>(null);
  // Курсор ушёл с кнопки, но мог пойти к действию — держим плашку ещё мгновение.
  // Плашке БЕЗ действия тянуться незачем: она ничего не предлагает нажать.
  const hasAction = !!action;
  const [lingering, setLingering] = useState(false);
  const [onFlyout, setOnFlyout] = useState(false);
  // Сторож погасил плашку сам, вопреки состоянию наведения (см. эффект ниже)
  const [killed, setKilled] = useState(false);
  // Вертикальный центр кнопки в координатах окна — по нему плашка встаёт напротив.
  // По горизонтали её место задаёт кромка рельсы (railWidth), а не кнопка.
  const [top, setTop] = useState(0);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- таймер задержки скрытия плашки после закрытия
    if (open) { setLingering(true); return; }
    if (!hasAction) { setLingering(false); return; }
    const id = setTimeout(() => setLingering(false), HIDE_DELAY);
    return () => clearTimeout(id);
  }, [open, hasAction]);

  // Пока курсор на самой плашке, она живёт независимо от таймера — иначе исчезала
  // бы прямо под ним, на полпути к кнопке
  const wanted = open || onFlyout || lingering;
  const shown = wanted && !killed;

  // Сторож на случай, когда ухода курсора не случилось вовсе. Браузер шлёт
  // mouseleave только при ДВИЖЕНИИ мыши, а рельса переставляет себя сама: клик по
  // иконке открыл панель — кнопка уехала из-под неподвижного курсора; действие в
  // плашке открыло модалку — плашку накрыл оверлей. Ухода нет, hover у вызывающего
  // залипает, и язычок висит на экране до следующего осознанного наведения.
  // Поэтому сверяем положение курсора с реальностью и гасим сами; вернулся курсор
  // на кнопку — снимаем запрет (залипшее у вызывающего состояние он сам не починит).
  useEffect(() => {
    if (!wanted) { setKilled(false); return; }
    let kill: number | null = null;
    const stopKill = () => { if (kill != null) { clearTimeout(kill); kill = null; } };
    const inside = (t: EventTarget | null) => t instanceof Node
      && (!!hostRef.current?.contains(t) || !!flyoutRef.current?.contains(t));
    // Курсор больше не наш (Alt+Tab, другое приложение) или содержимое уехало
    // прокруткой — держать плашку не за что, и ждать движения мыши незачем.
    const gone = () => { stopKill(); setOnFlyout(false); setLingering(false); setKilled(true); };
    const onMove = (e: MouseEvent) => {
      if (inside(e.target)) { stopKill(); setKilled(false); return; }
      // Пауза та же, что у обычного гашения: путь от кнопки к действию идёт над
      // полем капсулы, и мгновенный сторож рубил бы плашку на полпути.
      if (kill == null) kill = window.setTimeout(() => { kill = null; gone(); }, HIDE_DELAY);
    };
    document.addEventListener('mousemove', onMove);
    window.addEventListener('blur', gone);
    window.addEventListener('scroll', gone, true);
    return () => {
      stopKill();
      document.removeEventListener('mousemove', onMove);
      window.removeEventListener('blur', gone);
      window.removeEventListener('scroll', gone, true);
    };
  }, [wanted]);

  // Кнопка-действие стоит на стороне, обращённой К ЦЕНТРУ окна: плашка правой
  // рельсы растёт влево, и действие в её хвосте оказалось бы зажатым между текстом
  // и кромкой капсулы — то есть у самого края экрана, дальше всего от того места,
  // куда идёт курсор. У левой рельсы центр справа, и хвост как раз туда и смотрит.
  const actionFirst = side === 'right';

  const measure = useCallback(() => {
    const el = hostRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setTop(r.top + r.height / 2);
  }, []);

  useLayoutEffect(() => {
    if (!shown) return;
    measure();
  }, [shown, label, hint, measure]);

  // Окно меняет размер при живой плашке — кнопка переезжает, а плашка осталась бы
  // на прежней высоте, оторванной от своей иконки.
  useEffect(() => {
    if (!shown) return;
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, [shown, measure]);

  return (
    <>
      <span ref={hostRef} style={{ display: 'flex', ...hostStyle }}>{children}</span>
      {shown && createPortal(
        <div
          ref={flyoutRef}
          onMouseEnter={() => setOnFlyout(true)}
          onMouseLeave={() => { setOnFlyout(false); setLingering(false); }}
          style={{
            position: 'fixed', top, transform: 'translateY(-50%)', zIndex: Z.dropdown,
            // От внешней кромки рельсы: язычок выезжает ИЗ-ПОД панели
            ...(side === 'left' ? { left: railWidth } : { right: railWidth }),
            // С подзаголовком плашка двухэтажная — высота по содержимому (минимум как
            // кнопка), без него — фиксированная FLYOUT_H (одна строка)
            ...(hint ? {
              height: 'auto', minHeight: FLYOUT_H,
              padding: action ? (actionFirst ? '2px 10px 2px 3px' : '2px 3px 2px 10px') : '2px 10px',
              flexDirection: 'column', alignItems: 'stretch', justifyContent: 'center',
            } : {
              height: FLYOUT_H,
              // Поле у кнопки уже текстового: она сама держит свой бокс
              padding: action ? (actionFirst ? '0 10px 0 3px' : '0 3px 0 10px') : '0 10px',
            }),
            display: 'flex', alignItems: 'center',
            maxWidth: 280, boxSizing: 'border-box',
            background: C.bgWhite,
            border: `1px solid ${C.border}`,
            // Сторона, обращённая к рельсе, открыта — оттуда плашка и выехала.
            // Таблетке (standalone) открывать нечего: она скруглена кругом.
            ...(standalone
              ? { borderRadius: R.md }
              : side === 'left'
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
            const titleRow = (
              <>
                {actionFirst && btn}
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</span>
                {!actionFirst && btn}
              </>
            );
            // Подзаголовок — отдельными строками под названием, по линии на каждый
            // индикатор. Перед текстом — мини-кружок в цвет соответствующего индикатора
            // на иконке (accent=оранжевый/primary, muted=серый/secondary): так в тултипе
            // видно, к какому кружку относится число. Строка-синоним — одна accent-линия.
            if (!hint) return titleRow;
            const lines = typeof hint === 'string' ? [{ text: hint }] : hint;
            return (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 0, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 4, minWidth: 0 }}>{titleRow}</div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 2, marginTop: 2 }}>
                  {lines.map((ln, i) => (
                    <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 5, minWidth: 0 }}>
                      <span style={{
                        width: 6, height: 6, borderRadius: '50%', flexShrink: 0,
                        // Тон точки повторяет кружок на иконке: accent (оранжевый,
                        // primary), muted (серый, secondary) или warning (жёлтый)
                        background: ln.tone === 'muted' ? C.textMuted
                          : ln.tone === 'warning' ? C.warning : C.accent,
                      }} />
                      <span style={{
                        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.3,
                        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                      }}>{ln.text}</span>
                    </div>
                  ))}
                </div>
              </div>
            );
          })()}
        </div>,
        document.body,
      )}
    </>
  );
}
