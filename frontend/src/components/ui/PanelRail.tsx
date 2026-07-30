import { Fragment, useState, type DragEvent, type HTMLAttributes, type ReactNode } from 'react';
import { ChevronsLeft, ChevronsRight, Columns2, Pin, Square, X, type LucideIcon } from 'lucide-react';
import { C, FONT, ISLAND, Z } from '../../lib/design';
import { ICON_STROKE } from './icons';
import { ToolbarIconButton } from '../Toolbar';

// Вертикальная рельса иконок у края окна — полукапсула-остров, из которой
// открываются панели-карточки. Общая для ОБЕИХ зон: RightPanelStack (инструменты
// проекта и сессии) и LeftPanelStack (сайдбары разделов).
//
// Зеркальность задаётся одним пропом side: он разворачивает скругления, бордер,
// зазор до центра и стрелки сворачивания (они всегда указывают К краю окна).
// Раньше это были два дословно скопированных блока в обоих стеках — правка
// одного молча расходилась с другим.

// Ширина рельсы и зазор между рельсой и зоной панелей. Значения общие: рельсы
// обязаны быть зеркальны, иначе одна зона визуально «толще» другой.
export const RAIL_W = 40;
// Зазор 8, а не 4: в него встаёт крайняя направляющая места вставки (толщина 2 плюс
// отступ от кромки панели). При 4 она прижималась к рельсе вплотную и читалась как
// её граница, а не как «сюда встанет колонка».
export const RAIL_GAP = 8;

// Одна иконка рельсы. key нужен только React'у — сам ключ панели рельса не
// трактует, всю логику (что открыто, что видно) решает вызывающий стек.
export interface RailItem {
  key: string;
  title: string;
  Icon: LucideIcon;
  active: boolean;
  // Число в кружке над иконкой. null/0 — кружок не рисуем.
  badge?: number | null;
  onClick: () => void;
  // Иконка — ручка перетаскивания панели: закрытую можно вытащить из рельсы
  // прямо в нужное место раскладки, не открывая её кликом наугад
  dragProps?: HTMLAttributes<HTMLElement> & { draggable?: boolean };
  // Курсор вошёл на иконку / ушёл с неё. Что показать — решает зона: место
  // будущей панели в раскладке (призрак) или попап-превью.
  onHoverStart?: () => void;
  onHoverEnd?: () => void;
  // Под курсором иконка становится булавкой: рядом висит попап-превью, и клик
  // его закрепит. Без попапа булавке взяться неоткуда — обычная иконка панели.
  pinnable?: boolean;
}

interface Props {
  // Сторона окна. Разворачивает капсулу и стрелки сворачивания.
  side: 'left' | 'right';
  // Группы иконок сверху вниз; между НЕПУСТЫМИ группами — сепаратор. Пустые
  // отбрасываются: так разделитель сам исчезает, когда группа скрыта целиком
  // (напр. сессионные кнопки без плана и агентов).
  groups: RailItem[][];
  // false — рельса плавно схлопывается (width→0, opacity→0), оставаясь в DOM.
  // Анимация синхронна с появлением/скрытием панелей рядом.
  visible?: boolean;
  // Зазор со стороны центра. Обычно его даёт зона панелей (её сплиттер или
  // крайний разделитель колонок), но при закрытых панелях задаётся здесь —
  // иначе рельса липнет к контенту.
  gapToCenter?: number;
  // Тумблер режима зоны (сверху). Не передан — не рендерится: так компактный
  // режим правой зоны и одно-панельная левая обходятся без него.
  modeToggle?: { soloMode: boolean; onToggle: () => void };
  // Кнопка «свернуть все» (снизу). Не передана — не рендерится.
  collapse?: { collapsed: boolean; disabled: boolean; onToggle: () => void };
  // Попап-превью панели, которую сейчас держат под курсором в рельсе. Рисуется
  // рядом с рельсой поверх её открытых панелей; full — тянуть во всю высоту зоны
  // (накрыть то, что под ним), иначе высота по содержимому.
  peek?: { node: ReactNode; full: boolean; onMouseEnter: () => void; onMouseLeave: () => void };
  // Рельса как место дропа: пока панель тащат, вся рельса принимает её и на
  // отпускание закрывает, оставляя иконку здесь. Иначе убрать панель во время
  // перетаскивания было нечем — приходилось бросать её обратно и жать крестик.
  drop?: {
    active: boolean;
    over: boolean;
    onDragOver: (e: DragEvent) => void;
    onDragLeave: () => void;
    onDrop: (e: DragEvent) => void;
  };
}

// Разделитель групп внутри рельсы. margin разный у верхнего/групповых/нижнего —
// отсюда параметр, чтобы вертикальный ритм иконок остался прежним.
function RailSep({ margin }: { margin: string }) {
  return <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin }} />;
}

// Иконка панели. У ОТКРЫТОЙ панели под курсором иконка подменяется на закрывающую:
// это и есть кнопка «закрыть». Своего крестика в шапке у панели больше нет —
// клик по активной иконке и раньше закрывал панель, теперь он ещё и выглядит
// как закрытие, а шапка не тратит место на дубль.
// hover держим здесь, а не в IconButton: тому он нужен только для собственных
// цветов и наружу не отдаётся.
// soleIcon — рельса из ОДНОЙ иконки: тогда открытая панель показывает не себя, а
// сворачивание, и постоянно, а не по наведению. Выбирать в такой зоне не из чего,
// поэтому иконка панели там ничего не сообщает, а активная подсветка врала бы про
// выбор; кнопки «свернуть все» внизу при одной панели тоже нет — её роль забирает
// эта же кнопка.
function RailButton({ item, soleIcon: SoleIcon }: { item: RailItem; soleIcon?: LucideIcon }) {
  const [hover, setHover] = useState(false);
  const sole = !!SoleIcon && item.active;
  const closing = !sole && item.active && hover;
  // Закрытая панель под курсором показывается попапом, а иконка предлагает её
  // закрепить: клик оставит панель в раскладке, уход курсора — уберёт попап.
  const pinning = !item.active && hover && !!item.pinnable;
  const Icon = sole ? SoleIcon : closing ? X : pinning ? Pin : item.Icon;
  const title = item.active
    ? `Скрыть «${item.title}»`
    : pinning ? `Закрепить «${item.title}»` : item.title;
  return (
    <span
      {...item.dragProps}
      onMouseEnter={() => { setHover(true); item.onHoverStart?.(); }}
      onMouseLeave={() => { setHover(false); item.onHoverEnd?.(); }}
      style={{ display: 'flex' }}
    >
      <ToolbarIconButton
        onClick={item.onClick}
        active={item.active && !sole}
        title={title}
      >
        <div style={{ position: 'relative', display: 'flex' }}>
          <Icon size={17} strokeWidth={ICON_STROKE} />
          {/* Кружок с числом при закрывающей иконке прячем: рядом с «закрыть» счётчик
              читается как часть действия, а не как содержимое панели */}
          {item.badge && !closing && !sole && !pinning ? (
            <span style={{
              position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
              borderRadius: 7, background: C.accent, color: C.onAccent,
              fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
            }}>
              {item.badge}
            </span>
          ) : null}
        </div>
      </ToolbarIconButton>
    </span>
  );
}

export function PanelRail({ side, groups, visible = true, gapToCenter = 0, modeToggle, collapse, peek, drop }: Props) {
  const isLeft = side === 'left';
  const dropping = !!drop?.active;

  // Пустые группы отбрасываем ДО отрисовки сепараторов — иначе между скрытой
  // группой и соседней остался бы висячий разделитель.
  const shownGroups = groups.filter(g => g.length > 0);

  // Вся рельса — одна иконка: её открытая панель показывает сворачивание (стрелки
  // к краю окна, как у кнопки «свернуть все»). Иначе иконки обычные, а крестик
  // подставляется по наведению.
  const soleItem = shownGroups.reduce((n, g) => n + g.length, 0) === 1;
  const soleIcon: LucideIcon | undefined = soleItem ? (isLeft ? ChevronsLeft : ChevronsRight) : undefined;

  // Обработчики дропа висят и на капсуле, и на мишени под ней: целиться удобнее в
  // мишень, но и вся рельса принимает панель — промахнуться мимо 40px-полосы
  // труднее, чем мимо квадрата.
  const dropProps = {
    onDragOver: drop?.onDragOver,
    onDragLeave: drop?.onDragLeave,
    onDrop: drop?.onDrop,
  };

  const railBorder = dropping
    ? `1px ${drop?.over ? 'solid' : 'dashed'} ${C.accent}`
    : `1px solid ${C.border}`;

  const rail = (
    <div
      {...dropProps}
      style={{
      width: visible ? RAIL_W : 0,
      opacity: visible ? 1 : 0,
      pointerEvents: visible ? 'auto' : 'none',
      transition: 'width 0.15s ease-out, opacity 0.12s ease-out',
      flexShrink: 0, position: 'relative',
      display: 'flex', flexDirection: 'column', alignItems: 'center',
      // Тон шапок островов и сайдбаров — единая «оправа» интерфейса.
      // Вертикальный отступ подобран так, чтобы капсула с ОДНОЙ иконкой была
      // ровно в высоту шапки панели (ISLAND.headerH), а центр первой кнопки
      // сел на линию её заголовка: рельса теперь всегда на виду рядом с шапкой.
      gap: 6, paddingTop: 4, paddingBottom: 4,
      // Пока панель тащат, рельса — приёмник: обводка пунктирная, под курсором —
      // сплошная акцентная с подложкой. Границу везде задаём ОДНОЙ строкой
      // railBorder, а не правим потом borderColor/borderStyle: React запрещает
      // мешать сокращённые свойства с посторонними (borderTop и т.п.) — снимая
      // одно, он не восстанавливает другое.
      background: dropping && drop?.over ? C.accentMuted : C.bgMain,
      borderTop: railBorder, borderBottom: railBorder,
      boxSizing: 'border-box', overflow: 'hidden',
      // Рельса — полукапсула-остров у края окна: тень как у остальных островов
      boxShadow: ISLAND.shadow,
      // Скруглена и обведена только сторона, обращённая к центру; прижатая к
      // краю окна — прямая и без бордера.
      ...(isLeft
        ? {
            borderRight: railBorder,
            borderTopRightRadius: ISLAND.radius, borderBottomRightRadius: ISLAND.radius,
            marginRight: gapToCenter,
          }
        : {
            borderLeft: railBorder,
            borderTopLeftRadius: ISLAND.radius, borderBottomLeftRadius: ISLAND.radius,
            marginLeft: gapToCenter,
          }),
    }}>
      {/* Переключатель режима зоны. multi — раскладка КОЛОНКАМИ; обе зоны её
          умеют, поэтому иконка и подсказка у них одни и те же. */}
      {modeToggle && (
        <>
          <ToolbarIconButton
            onClick={modeToggle.onToggle}
            title={modeToggle.soloMode
              ? 'Одна панель — нажмите для раскладки колонками'
              : 'Раскладка колонками — нажмите для режима одной панели'}
          >
            {modeToggle.soloMode
              ? <Square size={15} strokeWidth={ICON_STROKE} />
              : <Columns2 size={15} strokeWidth={ICON_STROKE} />}
          </ToolbarIconButton>
          <RailSep margin="1px 0 2px" />
        </>
      )}

      {shownGroups.map((group, gi) => (
        <Fragment key={gi}>
          {gi > 0 && <RailSep margin="2px 0" />}
          {group.map(it => <RailButton key={it.key} item={it} soleIcon={soleIcon} />)}
        </Fragment>
      ))}

      {/* Свернуть все панели / вернуть спрятанный набор как был. Стрелки всегда
          указывают К краю окна при сворачивании и от него — при разворачивании. */}
      {collapse && (() => {
        const CollapseIcon = collapse.collapsed
          ? (isLeft ? ChevronsRight : ChevronsLeft)
          : (isLeft ? ChevronsLeft : ChevronsRight);
        return (
          <>
            <RailSep margin="2px 0 1px" />
            <div style={{ opacity: collapse.disabled ? 0.3 : 1 }}>
              <ToolbarIconButton
                onClick={collapse.onToggle}
                disabled={collapse.disabled}
                title={collapse.collapsed ? 'Открыть свёрнутые панели' : 'Свернуть все панели'}
              >
                <div style={{ display: 'flex', color: collapse.disabled ? C.textMuted : undefined }}>
                  <CollapseIcon size={16} strokeWidth={ICON_STROKE} />
                </div>
              </ToolbarIconButton>
            </div>
          </>
        );
      })()}

      {/* Пока панель тащат, рельса СТАНОВИТСЯ мишенью: иконки закрываются
          непрозрачным слоем с крестиком — «отпусти здесь, и панель уберётся».
          Именно слоем поверх, а не отдельным блоком: так мишень наследует место и
          высоту рельсы, ничего не двигая на экране в момент, когда в неё целятся. */}
      {dropping && (
        <div style={{
          position: 'absolute', inset: 0, zIndex: 2,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          background: drop?.over ? C.accentMuted : C.bgMain,
          color: drop?.over ? C.accent : C.textMuted,
          transition: 'background 0.12s, color 0.12s',
        }}>
          <X size={18} strokeWidth={ICON_STROKE} />
        </div>
      )}
    </div>
  );

  // Мишень стоит ПОД рельсой, на холсте, а не внутри столбца иконок: внутри она
  // растила бы капсулу, и рельса подпрыгивала бы ровно в тот момент, когда в неё
  // уже целятся курсором.
  //
  // Обёртка тянется на всю высоту зоны (не по капсуле): от неё считается высота
  // попапа-превью. Сквозная для мыши — иначе пустая полоса под рельсой перехватывала
  // бы клики по контенту; события ловят сами дети.
  return (
    <div style={{
      alignSelf: 'stretch', flexShrink: 0, position: 'relative', pointerEvents: 'none',
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: ISLAND.gap,
    }}>
      {rail}

      {/* Превью: панель во всю высоту зоны рядом с рельсой, ПОВЕРХ открытых
          панелей. Читается как временное окно, а не часть раскладки — тень
          модалки и акцентная рамка.
          Коробка начинается вплотную к рельсе, а зазор до карточки делается её
          паддингом: иначе курсор, идущий от иконки к попапу, пересекал бы полосу
          «ничьей» земли, и попап закрывался бы на полпути. */}
      {peek && (
        <div
          onMouseEnter={peek.onMouseEnter}
          onMouseLeave={peek.onMouseLeave}
          style={{
            position: 'absolute', zIndex: Z.dropdown, top: 0,
            ...(peek.full ? { bottom: 0 } : { maxHeight: '100%' }),
            // Отсчёт от ШИРИНЫ КАПСУЛЫ, а не от её текущего положения: при закрытых
            // панелях рельса отодвинута от центра на gapToCenter, и попап, повторяя
            // этот отступ, вставал на 4px мимо места, куда панель встанет после
            // закрепления — закрепление выглядело как рывок.
            ...(isLeft
              ? { left: RAIL_W, paddingLeft: RAIL_GAP }
              : { right: RAIL_W, paddingRight: RAIL_GAP }),
            display: 'flex', flexDirection: 'column', pointerEvents: 'auto',
          }}
        >
          {peek.node}
        </div>
      )}
    </div>
  );
}
