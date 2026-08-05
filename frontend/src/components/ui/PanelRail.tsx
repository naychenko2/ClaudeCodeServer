import { Fragment, useRef, useState, type DragEvent, type HTMLAttributes, type ReactNode } from 'react';
import { ChevronsLeft, ChevronsRight, Columns2, Ellipsis, Pin, Square, X, type LucideIcon } from 'lucide-react';
import { C, FONT, FS, ISLAND, R, Z } from '../../lib/design';
import { ICON_STROKE } from './icons';
import { Menu, MenuItem } from './Menu';
import { RailCapsule, RAIL_W, RAIL_GAP, RAIL_ITEM_GAP } from './RailCapsule';
import { RailIconButton } from './RailIconButton';
import { RailSep } from './RailSep';

// Высота капсулы с ОДНОЙ кнопкой: паддинги 4+4, бокс кнопки 32, рамка 1+1.
// Столько места держим за схлопнутой рельсой, чтобы соседние капсулы (док
// проектов) не подпрыгивали, когда панелей на экране не осталось.
const RAIL_MIN_H = 42;

// Вертикальная рельса иконок у края окна — полукапсула-остров, из которой
// открываются панели-карточки. Общая для ОБЕИХ зон: RightPanelStack (инструменты
// проекта и сессии) и LeftPanelStack (сайдбары разделов).
//
// Зеркальность задаётся одним пропом side: он разворачивает скругления, бордер,
// зазор до центра и стрелки сворачивания (они всегда указывают К краю окна).
// Раньше это были два дословно скопированных блока в обоих стеках — правка
// одного молча расходилась с другим.

// Геометрия капсулы (ширина, зазор до центра) живёт вместе с ней — RailCapsule.
export { RAIL_W, RAIL_GAP };

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
  // Ящик рельсы — кнопка «…» ПОСЛЕДНЕЙ в столбце и меню за ней. Держит редкие
  // кнопки, которые человек сам утащил сюда с рельсы, и управление режимом зоны
  // (своей кнопки в столбце у него больше нет — 40px-полоса дорога).
  overflow?: {
    // Спрятанные кнопки — те же RailItem, что в группах: клик, бейдж и ручка
    // перетаскивания уже собраны вызывающим
    items: RailItem[];
    // Тумблер раскладки зоны: колонки или одна панель
    modeToggle?: { soloMode: boolean; onToggle: () => void };
    // Сумма бейджей спрятанных панелей: их кружки не видны, а число сообщений
    // терять нельзя
    badge?: number | null;
    // Панель тащат по экрану: подложка меню на это время перестаёт ловить события,
    // иначе места дропа под ней не получат ни одного dragover
    dragActive?: boolean;
    // Кнопка «…» как приёмник: дроп сюда убирает кнопку панели в ящик
    drop?: {
      active: boolean;
      over: boolean;
      onDragOver: (e: DragEvent) => void;
      onDragLeave: () => void;
      onDrop: (e: DragEvent) => void;
    };
  };
  // Зазор со стороны центра. Обычно его даёт зона панелей (её сплиттер или
  // крайний разделитель колонок), но при закрытых панелях задаётся здесь —
  // иначе рельса липнет к контенту.
  gapToCenter?: number;
  // Кнопка «свернуть все» (ПЕРВОЙ в столбце). Не передана — не рендерится.
  collapse?: { collapsed: boolean; disabled: boolean; onToggle: () => void };
  // Попап-превью панели, которую сейчас держат под курсором в рельсе. Рисуется
  // рядом с рельсой поверх её открытых панелей; full — тянуть во всю высоту зоны
  // (накрыть то, что под ним), иначе высота по содержимому.
  peek?: { node: ReactNode; full: boolean; onMouseEnter: () => void; onMouseLeave: () => void };
  // Второй остров ПОД капсулой, в той же вертикали у края окна: сейчас это док
  // проектов левой зоны. Живёт своей жизнью (сам решает, что показывать) — рельса
  // лишь отдаёт ему остаток высоты и держит общий вертикальный ритм островов.
  footer?: ReactNode;
  // Рельса как место дропа: пока панель тащат, вся рельса принимает её и на
  // отпускание закрывает, оставляя иконку здесь. Иначе убрать панель во время
  // перетаскивания было нечем — приходилось бросать её обратно и жать крестик.
  drop?: {
    active: boolean;
    over: boolean;
    // Знак на мишени. Дефолт — крестик («панель уберётся»). Когда тащат кнопку
    // ЗАКРЫТОЙ панели, убирать нечего: там знак — иконка самой панели, то есть
    // «её кнопка переедет на эту рельсу».
    icon?: LucideIcon;
    onDragOver: (e: DragEvent) => void;
    onDragLeave: () => void;
    onDrop: (e: DragEvent) => void;
  };
}


// Кружок с числом над иконкой рельсы. Общий для кнопок панелей и кнопки ящика:
// у спрятанных панелей свои кружки не видны, и «…» показывает их сумму.
// inline — кружок в строке меню ящика: там он не наездник на иконке, а обычный
// элемент строки справа от названия.
function RailBadge({ value, inline }: { value: number; inline?: boolean }) {
  return (
    <span style={{
      ...(inline ? null : { position: 'absolute', top: -6, right: -7 }),
      minWidth: 14, height: 14, padding: '0 3px', flexShrink: 0,
      borderRadius: 7, background: C.accent, color: C.onAccent,
      fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
    }}>
      {value}
    </span>
  );
}

// Ящик рельсы: кнопка «…» и меню за ней. Держит кнопки, которые человек утащил с
// рельсы (перетаскиванием на эту кнопку), и тумблер режима зоны — своей кнопки в
// столбце у режима больше нет.
//
// Кнопка САМА принимает дроп: пока панель тащат, вокруг неё стоит пунктирная
// мишень, под курсором — акцентная. Мишень капсулы («убрать панель с глаз») её не
// накрывает — оверлей дропа рисуется только над столбцом иконок.
function RailOverflow({ side, overflow }: { side: 'left' | 'right'; overflow: NonNullable<Props['overflow']> }) {
  const { items, modeToggle, badge, dragActive, drop } = overflow;
  const hostRef = useRef<HTMLDivElement>(null);
  // rect кнопки — якорь меню. Держим сам rect, а не флаг: меню живёт порталом и
  // считает своё место от координат окна.
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const close = () => setAnchor(null);
  const dropping = !!drop?.active;

  return (
    <div
      ref={hostRef}
      // Событие ОБЯЗАНО остановиться здесь: те же обработчики висят на капсуле
      // (вся рельса — приёмник), и всплывший дроп отработал бы вторым, отменив
      // только что случившееся — кнопка вместо ящика оказывалась бы в столбце.
      // Когда своей мишени у ящика нет (дроп сюда ничего не изменит), событие,
      // наоборот, пропускаем наверх — пусть его примет рельса.
      onDragOver={drop ? e => { e.stopPropagation(); drop.onDragOver(e); } : undefined}
      onDragLeave={drop ? () => drop.onDragLeave() : undefined}
      onDrop={drop ? e => { e.stopPropagation(); drop.onDrop(e); close(); } : undefined}
      style={{
        display: 'flex', borderRadius: R.md, boxSizing: 'border-box',
        border: dropping
          ? (drop?.over ? `1px solid ${C.accent}` : `1px dashed ${C.textSecondary}`)
          : '1px solid transparent',
        background: dropping && drop?.over ? C.accentMuted : undefined,
        color: dropping && drop?.over ? C.accent : undefined,
      }}
    >
      <RailIconButton
        side={side}
        label="Ещё"
        active={anchor != null}
        onClick={() => setAnchor(anchor ? null : hostRef.current?.getBoundingClientRect() ?? null)}
      >
        <div style={{ position: 'relative', display: 'flex' }}>
          <Ellipsis size={17} strokeWidth={ICON_STROKE} />
          {badge ? <RailBadge value={badge} /> : null}
        </div>
      </RailIconButton>

      {anchor && (
        <Menu
          anchor={anchor}
          anchorSide={side}
          onClose={close}
          minWidth={230}
          maxHeight={360}
          // Строку из меню можно ВЫТАЩИТЬ обратно на рельсу или сразу в раскладку:
          // на это время подложка перестаёт ловить события, иначе места дропа под
          // ней не получат ни одного dragover
          inertBackdrop={dragActive}
        >
          {/* Содержимое ящика — сами спрятанные кнопки. Скроллится ИМЕННО этот
              список, а не карточка целиком: иначе футер с режимом уезжал бы за
              нижнюю кромку вместе с длинным списком. */}
          <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column' }}>
            {items.length === 0 ? (
              <div style={{
                padding: '8px 10px', maxWidth: 220,
                fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.4,
              }}>
                Перетащите сюда кнопки панелей, которыми пользуетесь редко
              </div>
            ) : items.map(it => {
              const { onDragEnd, ...dragRest } = it.dragProps ?? {};
              return (
                <MenuItem
                  key={it.key}
                  icon={<it.Icon size={15} strokeWidth={ICON_STROKE} />}
                  label={
                    <>
                      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {it.title}
                      </span>
                      {it.badge ? <RailBadge value={it.badge} inline /> : null}
                    </>
                  }
                  onClick={() => { it.onClick(); close(); }}
                  // Строка — ручка перетаскивания. Меню закрываем в КОНЦЕ жеста, а не
                  // на старте: исчезнувший источник не дождался бы dragend, и
                  // состояние перетаскивания залипло бы на весь экран.
                  wrapper={it.dragProps && {
                    ...dragRest,
                    onDragEnd: (e: DragEvent<HTMLElement>) => { onDragEnd?.(e); close(); },
                  }}
                />
              );
            })}
          </div>

          {/* Режим зоны — ФУТЕР попапа: это настройка раскладки, а не одна из
              спрятанных кнопок, поэтому стоит отдельно внизу и на своём фоне —
              тоне оправы интерфейса (шапки островов, рельсы), а не утопленном:
              полоса должна отделяться от карточки, а не проваливаться под неё.
              Отрицательные поля вытягивают
              полосу до кромок карточки — иначе фон висел бы островком в её паддинге.
              Пункт называет ДЕЙСТВИЕ, а не текущее состояние: тумблером, как в
              рельсе, строка меню быть не умеет. */}
          {modeToggle && (
            <div style={{
              margin: '5px -5px -5px', padding: 4,
              background: C.bgMain, borderTop: `1px solid ${C.borderLight}`,
              borderBottomLeftRadius: R.lg, borderBottomRightRadius: R.lg,
              flexShrink: 0,
            }}>
              <MenuItem
                icon={modeToggle.soloMode
                  ? <Columns2 size={15} strokeWidth={ICON_STROKE} />
                  : <Square size={15} strokeWidth={ICON_STROKE} />}
                label={modeToggle.soloMode ? 'Колонки' : 'Одна панель'}
                onClick={() => { modeToggle.onToggle(); close(); }}
              />
            </div>
          )}
        </Menu>
      )}
    </div>
  );
}

// Иконка панели. У ОТКРЫТОЙ панели под курсором иконка подменяется на закрывающую:
// это и есть кнопка «закрыть». Своего крестика в шапке у панели больше нет —
// клик по активной иконке и раньше закрывал панель, теперь он ещё и выглядит
// как закрытие, а шапка не тратит место на дубль.
// hover держим здесь, а не в IconButton: тому он нужен только для собственных
// цветов и наружу не отдаётся.
//
// Иконка ВСЕГДА своя, сколько бы кнопок ни осталось в столбце. Раньше единственная
// иконка зоны подменялась стрелками сворачивания насовсем — и панель выглядела
// пропавшей: на её месте стояла стрелка, в которой человек свою панель не узнавал.
function RailButton({ item, side }: { item: RailItem; side: 'left' | 'right' }) {
  // Во время HTML5-drag браузер не шлёт mouse-события, поэтому hover, поднятый при
  // захвате иконки, залипает: после дропа панель могла стать активной, и залипший
  // hover рисовал бы на её иконке крестик закрытия. Поэтому наведение гасится в
  // КОНЦЕ жеста; кнопка держит hover у себя, и «погасить» здесь — это сообщить ей,
  // что курсор ушёл.
  //
  // На СТАРТЕ гасить нельзя: у открытой панели под курсором стоит крестик, и сброс
  // наведения тут же менял бы <svg> внутри самого источника перетаскивания — Chrome
  // на такую подмену жест обрывал, и открытую панель нельзя было утащить за её
  // кнопку вовсе (закрытая тащилась: там подменять нечего). Подпись на время жеста
  // убирает hoverSuppressed — она мешала бы, показывая «Скрыть…» поверх места
  // вставки, но hover при этом остаётся, и иконка не дёргается.
  const { onDragStart: dragStart, onDragEnd: dragEnd, ...dragRest } = item.dragProps ?? {};
  const [dragging, setDragging] = useState(false);
  const dragProps = item.dragProps && {
    ...dragRest,
    // Своё состояние поднимаем СЛЕДУЮЩИМ кадром: перерисовка кнопки в самом
    // обработчике dragstart меняет DOM под захваченным элементом, и браузер жест
    // отменяет — dragstart приходит, а следом сразу dragend, без единого события
    // drag. Отсюда же порядок: сперва авторский обработчик (он ставит dataTransfer
    // и общее состояние перетаскивания), и только потом наше.
    onDragStart: (e: DragEvent<HTMLElement>) => {
      dragStart?.(e);
      requestAnimationFrame(() => setDragging(true));
    },
    onDragEnd: (e: DragEvent<HTMLElement>) => {
      setDragging(false);
      (e.currentTarget as HTMLElement).dispatchEvent(new MouseEvent('mouseleave', { bubbles: false }));
      dragEnd?.(e);
    },
  };
  return (
    <RailIconButton
      side={side}
      // Подпись меняется по наведению вместе с иконкой: у открытой панели клик
      // закрывает, у закрытой с попапом — закрепляет
      label={item.active ? `Скрыть «${item.title}»` : item.title}
      active={item.active}
      onClick={item.onClick}
      // Пока кнопку тащат, подпись не нужна: она вылезала бы поверх места вставки
      hoverSuppressed={dragging}
      onHoverChange={h => (h ? item.onHoverStart?.() : item.onHoverEnd?.())}
      wrapper={{
        ...dragProps,
        // Метка для зоны: по ней она проверяет, под курсором ли ещё иконка.
        // Одного onMouseLeave мало — если кнопка перестроилась или исчезла под
        // курсором, событие не приходит вовсе и подсказка зоны залипает.
        'data-rail-item': item.key,
      } as HTMLAttributes<HTMLElement>}
    >
      {hover => {
        const closing = item.active && hover;
        // Закрытая панель под курсором показывается попапом, а иконка предлагает её
        // закрепить: клик оставит панель в раскладке, уход курсора — уберёт попап.
        const pinning = !item.active && hover && !!item.pinnable;
        const Icon = closing ? X : pinning ? Pin : item.Icon;
        return (
          <div style={{ position: 'relative', display: 'flex' }}>
            <Icon size={17} strokeWidth={ICON_STROKE} />
            {/* Кружок с числом при закрывающей иконке прячем: рядом с «закрыть» счётчик
                читается как часть действия, а не как содержимое панели */}
            {item.badge && !closing && !pinning ? <RailBadge value={item.badge} /> : null}
          </div>
        );
      }}
    </RailIconButton>
  );
}

export function PanelRail({ side, groups, visible = true, gapToCenter = 0, overflow, collapse, peek, drop, footer }: Props) {
  const isLeft = side === 'left';
  const dropping = !!drop?.active;


  // Пустые группы отбрасываем ДО отрисовки сепараторов — иначе между скрытой
  // группой и соседней остался бы висячий разделитель.
  const shownGroups = groups.filter(g => g.length > 0);

  const columnCount = shownGroups.reduce((n, g) => n + g.length, 0);

  // При единственной иконке отдельной кнопки «свернуть все» быть не должно: её
  // роль забирает сама иконка панели (клик по ней закрывает), а две кнопки рядом
  // делали бы одно и то же. Пустой столбец (все кнопки уехали в ящик) её тоже не
  // показывает: сворачивать там нечего — открытая панель всегда держит свою кнопку
  // в столбце. Решается здесь, а не у вызывающего: сколько иконок реально осталось
  // в столбце, знает только рельса.
  const showCollapse = collapse && columnCount > 1;

  // Ящик показываем, только когда ему есть что предложить: при ЕДИНСТВЕННОЙ кнопке
  // в столбце и пустом ящике прятать нечего (спрятав её, человек остался бы с
  // пустой рельсой), а режим зоны из одной панели ничего не решает. Как только в
  // ящике что-то лежит, кнопка обязана быть — иначе спрятанное не достать.
  const showOverflow = overflow && (columnCount > 1 || overflow.items.length > 0);

  // Обработчики дропа висят и на капсуле, и на мишени под ней: целиться удобнее в
  // мишень, но и вся рельса принимает панель — промахнуться мимо 40px-полосы
  // труднее, чем мимо квадрата.
  const dropProps = {
    onDragOver: drop?.onDragOver,
    onDragLeave: drop?.onDragLeave,
    onDrop: drop?.onDrop,
  };

  // Штриховая обводка мишени в покое — серая, тем же цветом, что знаки места
  // вставки в зоне (PanelDropGuide): «сюда можно» на всём экране выглядит
  // одинаково, а акцент приберегается для «отпустишь — попадёт вот сюда».
  // Схлопнутая рельса не должна оставлять после себя ни линии, ни полоски padding:
  // под ней стоит второй остров, и любой её след отодвинул бы его от верха зоны.
  const railBorder = !visible
    ? '0 none transparent'
    : dropping
      ? (drop?.over ? `1px solid ${C.accent}` : `1px dashed ${C.textSecondary}`)
      : `1px solid ${C.border}`;

  // Секции столбца сверху вниз: служебная кнопка сворачивания и группы кнопок
  // панелей. Разделители расставляются МЕЖДУ фактическими секциями — так две черты
  // не встают рядом, когда соседняя секция схлопнулась (все кнопки уехали в ящик,
  // группа скрыта целиком, «свернуть все» уступила место единственной иконке).
  // inner — граница ПОДГРУППЫ (пунктир между группами кнопок панелей); остальные
  // границы служебные, сплошные.
  const columnSections: { key: string; inner?: boolean; node: ReactNode }[] = [];

  if (showCollapse) {
    // Свернуть все панели / вернуть спрятанный набор как был — ПЕРВАЯ кнопка
    // столбца. Стрелки всегда указывают К краю окна при сворачивании и от него —
    // при разворачивании.
    const CollapseIcon = collapse.collapsed
      ? (isLeft ? ChevronsRight : ChevronsLeft)
      : (isLeft ? ChevronsLeft : ChevronsRight);
    columnSections.push({
      key: 'collapse',
      node: (
        <div style={{ opacity: collapse.disabled ? 0.3 : 1 }}>
          <RailIconButton
            side={side}
            label={collapse.collapsed ? 'Открыть свёрнутые панели' : 'Свернуть все панели'}
            onClick={collapse.onToggle}
            disabled={collapse.disabled}
          >
            <div style={{ display: 'flex', color: collapse.disabled ? C.textMuted : undefined }}>
              <CollapseIcon size={16} strokeWidth={ICON_STROKE} />
            </div>
          </RailIconButton>
        </div>
      ),
    });
  }

  shownGroups.forEach((group, gi) => columnSections.push({
    key: `group-${gi}`,
    // Инструменты проекта и панели сессии — подгруппы ОДНОГО набора кнопок панелей,
    // а не разные наборы: между ними пунктир. Сплошная черта остаётся там, где
    // граница настоящая: у служебных кнопок рельсы.
    inner: gi > 0,
    node: group.map(it => <RailButton key={it.key} item={it} side={side} />),
  }));

  const rail = (
    <RailCapsule
      {...dropProps}
      side={side}
      visible={visible}
      gapToCenter={gapToCenter}
      // Пока панель тащат, рельса — приёмник: обводка пунктирная, под курсором —
      // сплошная акцентная с подложкой
      border={railBorder}
      background={dropping && drop?.over ? C.accentMuted : undefined}
    >
      {/* Столбец кнопок панелей вместе со «свернуть все». Отдельным блоком он стоит
          ради мишени дропа: слой «убрать панель» накрывает ИМЕННО его, оставляя
          кнопку ящика внизу собственной мишенью («спрятать кнопку»). */}
      {columnSections.length > 0 && (
      <div style={{
        position: 'relative', display: 'flex', flexDirection: 'column', alignItems: 'center',
        gap: RAIL_ITEM_GAP,
      }}>
        {columnSections.map((s, i) => (
          <Fragment key={s.key}>
            {i > 0 && (s.inner ? <RailSep variant="inner" /> : <RailSep margin="1px 0 2px" />)}
            {s.node}
          </Fragment>
        ))}

        {/* Пока панель тащат, столбец СТАНОВИТСЯ мишенью: иконки закрываются
            непрозрачным слоем со знаком дропа — крестик «панель уберётся» либо
            иконка панели «её кнопка переедет сюда».
            Именно слоем поверх, а не отдельным блоком: так мишень наследует место и
            высоту столбца, ничего не двигая на экране в момент, когда в неё целятся. */}
        {dropping && (() => {
          const DropIcon = drop?.icon ?? X;
          return (
            <div style={{
              position: 'absolute', inset: 0, zIndex: 2,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: drop?.over ? C.accentMuted : C.bgMain,
              color: drop?.over ? C.accent : C.textMuted,
              transition: 'background 0.12s, color 0.12s',
            }}>
              <DropIcon size={18} strokeWidth={ICON_STROKE} />
            </div>
          );
        })()}
      </div>
      )}

      {/* Ящик — ПОСЛЕДНЯЯ кнопка рельсы: редкие кнопки панелей и режим зоны.
          Разделитель перед ним — только когда выше есть что отделять: пустой столбец
          (все кнопки в ящике) иначе оставил бы черту у самой кромки капсулы. */}
      {showOverflow && (
        <>
          {columnSections.length > 0 && <RailSep margin="2px 0 1px" />}
          <RailOverflow side={side} overflow={overflow} />
        </>
      )}
    </RailCapsule>
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
      display: 'flex', flexDirection: 'column', alignItems: 'center',
    }}>
      {rail}

      {/* Второй остров у края окна (док проектов). Забирает ОСТАТОК высоты зоны: по
          нему он сам считает, сколько иконок показать. Боковой отступ повторяет
          рельсу — иначе при закрытых панелях (где рельса отодвинута gapToCenter)
          капсулы разъехались бы по вертикали. Зазор между островами существует,
          только пока рельса на экране: без неё док встаёт на её место, у самого
          верха зоны, а не под пустотой. */}
      {footer && (
        <div style={{
          flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column',
          // Рельса схлопнулась (панелей на экране нет) — её место всё равно
          // резервируем высотой капсулы с одной кнопкой: иначе док проектов
          // подпрыгивает к верхней кромке, стоит панелям исчезнуть, и вертикаль
          // рельс «дышит» на каждом переключении экрана.
          marginTop: visible ? ISLAND.gap : RAIL_MIN_H + ISLAND.gap,
          ...(isLeft ? { marginRight: gapToCenter } : { marginLeft: gapToCenter }),
        }}>
          {footer}
        </div>
      )}

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
