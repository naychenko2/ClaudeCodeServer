import { useCallback, useEffect, useMemo, useState, type CSSProperties, type HTMLAttributes, type ReactNode } from 'react';
import { Children } from 'react';
import { X, type LucideIcon } from 'lucide-react';
import { C, ISLAND } from '../../lib/design';
import { useCanHover } from '../../lib/pointer';
import { ICON_STROKE } from './icons';
import { IconButton } from './IconButton';
import { Island, IslandHeader } from './Island';
import { PanelHeaderSlotContext } from './panelHeaderSlotContext';

// Универсальная оболочка панели — остров с шапкой и контентной зоной.
// Единый "рецепт" и для правой рельсы (PlanSection/FileViewer/TaskBoard...),
// и для левых сайдбаров (SessionList/ProjectSidebar/PersonaList/...).
//
// Атомарна: Island (рамка+тень+скругление) + IslandHeader (40px, icon+title+
// badge+actions) + опциональный toolbar под шапкой + контентная зона bgWhite.
//
// DnD правой рельсы передаётся через rootProps/headerProps/dragged/dropTarget/
// flash — см. RightPanelStack. Левые сайдбары эти пропсы не используют.
//
// bare=true — режим "только контент": Island/шапка не рендерятся. Нужен когда
// панель уже встроена в другой PanelShell (cc-panels, мульти-стек) — иначе
// получится остров в острове.

interface PanelShellProps {
  // === Шапка (обязательна — см. решение по унификации) ===
  icon?: ReactNode;
  title: string;
  badge?: string | null;
  // Контролы справа в шапке — кнопки закрытия, настройки, DnD-хендлы.
  // Это системные кнопки самой оболочки; контролы САМОЙ панели (переключатели
  // видов, фильтры, «создать») кладутся не сюда, а изнутри панели через
  // PanelHeaderSlot — см. PanelHeaderSlot.tsx.
  headerActions?: ReactNode;

  // === Тулбар под шапкой (опционально) ===
  // FilterBar, SegmentedControl, Button "Новый чат" и т.д. — всё что раньше
  // сайдбары делали через div padding borderBottom руками.
  toolbar?: ReactNode;

  // === Контент ===
  children: ReactNode;
  // Контент не скроллится (напр. для fixed-панелей с собственной прокруткой)
  noScroll?: boolean;
  // Доп. стили контентной зоны
  contentStyle?: CSSProperties;

  // === DnD правой рельсы (опционально) ===
  draggable?: boolean;
  dragged?: boolean;
  dropTarget?: boolean;
  // Кратковременная подсветка «панель уже открыта»
  flash?: boolean;
  // Атрибуты корневого Island: onDragOver/onDragLeave/onDrop для DnD
  rootProps?: HTMLAttributes<HTMLDivElement>;
  // Доступ к корневому узлу карточки — для замеров раскладки (высота панели
  // под растяжимый плейсхолдер). Отдельным пропом: ref в HTMLAttributes не входит.
  rootRef?: (el: HTMLDivElement | null) => void;
  // Атрибуты шапки: draggable/onDragStart/onDragEnd
  headerProps?: HTMLAttributes<HTMLDivElement> & { draggable?: boolean };

  // === Режимы ===
  // bare=true — рендерит только children, без Island/шапки. Для встраивания
  // в другой PanelShell (cc-panels) или в IslandScaffold.
  bare?: boolean;
  // Показать кнопку закрытия в headerActions (короткий путь для рельсы)
  onClose?: () => void;
  // Чем закрывать панель: 'button' — отдельный крестик справа в шапке (нужен там,
  // где нет курсора — тач); 'icon' — иконка панели слева, которая под курсором
  // сама превращается в крестик, не занимая в шапке лишнего места.
  closeMode?: 'button' | 'icon';
  // Своё действие на иконке шапки вместо закрытия: под курсором иконка панели
  // подменяется этой (напр. булавка «закрепить» у попапа-превью). Сильнее
  // closeMode — иконка одна, и делать ей два дела нельзя.
  iconAction?: { Icon: LucideIcon; title: string; onClick: () => void };
  // fill=false — панель не растягивается на всю высоту родителя, занимает
  // по контенту. Применяется в сайдбарах с короткими списками (Чаты с малым
  // количеством). По умолчанию true — растягивается (нужно для рельсы и
  // длинных списков). При fill=false добавляется maxHeight: 100%, чтобы
  // панель не вылезала за пределы окна при большом контенте — срабатывает
  // внутренний скролл контентной зоны.
  fill?: boolean;
  // hideIfEmpty=true — если у PanelShell нет реальных children (массив пуст
  // после фильтрации null/false), не рендерит ничего. Удобно для сайдбаров,
  // которые не имеют смысла без данных: caller оборачивает children в
  // условие, и панель автоматически скрывается.
  hideIfEmpty?: boolean;

  // === Стили ===
  style?: CSSProperties;
  // Направление анимации появления: 'up' (дефолт — снизу вверх, для правой рельсы)
  // или 'left' (справа налево, для левой рельсы). Влияет на transform при mount.
  slideDirection?: 'up' | 'left';
  // false — панель появляется без анимации. Нужно там, где она не «прилетает», а
  // остаётся на месте: закреплённый попап-превью уже стоит перед глазами, и
  // проигрывать ему въезд — значит дёрнуть картинку на ровном месте.
  animate?: boolean;
}

export function PanelShell({
  icon,
  title,
  badge,
  headerActions,
  toolbar,
  children,
  noScroll = false,
  contentStyle,
  draggable = false,
  dragged = false,
  dropTarget = false,
  flash = false,
  rootProps,
  rootRef,
  headerProps,
  bare = false,
  onClose,
  closeMode = 'button',
  iconAction,
  fill = true,
  hideIfEmpty = false,
  style,
  slideDirection = 'up',
  animate = true,
}: PanelShellProps) {
  // Плавное появление карточки при открытии/переносе: fade + подъём.
  // Тот же эффект что в исходном PanelShell RightPanelStack.
  //
  // ВАЖНО: хуки объявлены ДО любых ранних return (hideIfEmpty/bare). Иначе при
  // переключении режима (напр. chats.length 0 → 1 у hideIfEmpty) число хуков
  // между рендерами меняется, и React падает с «Rendered more hooks than
  // during the previous render».
  const [mounted, setMounted] = useState(false);
  useEffect(() => {
    const id = requestAnimationFrame(() => setMounted(true));
    return () => cancelAnimationFrame(id);
  }, []);

  // Курсор на шапке — иконка панели предлагает закрыть её (closeMode: 'icon')
  const [headerHover, setHeaderHover] = useState(false);
  const [headerEl, setHeaderEl] = useState<HTMLDivElement | null>(null);

  // Контролы шапки (переключатели вида, действия, крестик) проявляются, только
  // пока курсор на карточке — в покое шапка чистая: иконка панели, заголовок,
  // бейдж. На устройствах без наведения (тач) прятать нечем: контролы видны
  // всегда, иначе к ним было бы не подобраться.
  // Не media query, а реальный ввод: планшет с клавиатурой рапортует «умею
  // наводить», и контролы схлопывались прямо под пальцем — см. lib/pointer
  const hoverCapable = useCanHover();
  const [panelHover, setPanelHover] = useState(false);
  const controlsVisible = !hoverCapable || panelHover;
  // Общий стиль для всех трёх обёрток контролов шапки. Скрытые контролы не только
  // гаснут, но и СХЛОПЫВАЮТСЯ по ширине (maxWidth: 0): прозрачность сама по себе
  // оставляет элемент в потоке, и невидимые кнопки продолжали занимать место —
  // заголовок жался и в узкой колонке резался многоточием («Документация» →
  // «Документа…»), хотя кнопок не видно. В покое всю ширину шапки забирает
  // заголовок, кнопки появляются по наведению. overflow прячем только на время
  // схлопывания, чтобы у видимых контролов ничего не подрезалось.
  const controlsFade: CSSProperties = {
    opacity: controlsVisible ? 1 : 0,
    maxWidth: controlsVisible ? undefined : 0,
    overflow: controlsVisible ? undefined : 'hidden',
    transition: 'opacity 0.1s ease-out',
  };

  // Наведение на всю карточку ловим на корне острова. Нативно (mouseenter/
  // mouseleave не всплывают и стреляют строго на границе узла) — переходы внутрь
  // портальных контролов, что физически лежат в этом же узле, за уход не считаются.
  const [rootEl, setRootEl] = useState<HTMLDivElement | null>(null);
  const setRoots = useCallback((el: HTMLDivElement | null) => {
    setRootEl(el);
    rootRef?.(el);
  }, [rootRef]);
  useEffect(() => {
    if (!rootEl || !hoverCapable) return;
    const on = () => setPanelHover(true);
    const off = () => setPanelHover(false);
    rootEl.addEventListener('mouseenter', on);
    rootEl.addEventListener('mouseleave', off);
    return () => {
      rootEl.removeEventListener('mouseenter', on);
      rootEl.removeEventListener('mouseleave', off);
    };
  }, [rootEl, hoverCapable]);

  // Курсор в зоне контролов шапки (слот панели + системные actions). Пока он там,
  // перетаскивание карточки за шапку выключено — иначе нажатие на кнопку легко
  // превращается в перенос панели
  const [overControls, setOverControls] = useState(false);
  // Для системных actions хватает React-обработчиков: это обычные дети шапки
  const controlsHoverProps = {
    onMouseEnter: () => setOverControls(true),
    onMouseLeave: () => setOverControls(false),
  };

  // Узел-слот в шапке, куда панель-содержимое телепортирует свои контролы
  // (PanelHeaderSlot). Через состояние, а не ref: портал должен отрисоваться
  // после того, как узел появился в DOM, а значит нужен повторный рендер.
  const [slotEl, setSlotEl] = useState<HTMLDivElement | null>(null);
  const [slotLeftEl, setSlotLeftEl] = useState<HTMLDivElement | null>(null);
  const [slotPinnedEl, setSlotPinnedEl] = useState<HTMLDivElement | null>(null);
  const slotValue = useMemo(
    () => ({ hasHeader: true, el: slotEl, elLeft: slotLeftEl, elPinned: slotPinnedEl }),
    [slotEl, slotLeftEl, slotPinnedEl]);

  // Наведение на СЛОТ ловим нативно, а не пропсами React. Контролы приезжают в него
  // порталом, и react-события идут по React-дереву — то есть в саму панель, мимо
  // этого узла: onMouseEnter здесь не срабатывал бы, и панель продолжала уезжать
  // при нажатии на её же кнопки.
  useEffect(() => {
    const nodes = [slotEl, slotLeftEl, slotPinnedEl].filter((n): n is HTMLDivElement => !!n);
    if (!nodes.length) return;
    const on = () => setOverControls(true);
    const off = () => setOverControls(false);
    nodes.forEach(n => { n.addEventListener('mouseenter', on); n.addEventListener('mouseleave', off); });
    return () => nodes.forEach(n => {
      n.removeEventListener('mouseenter', on);
      n.removeEventListener('mouseleave', off);
    });
  }, [slotEl, slotLeftEl, slotPinnedEl]);

  // Действие на иконке: пока курсор на шапке, иконка панели слева подменяется
  // кнопкой и кликается. Так шапка не тратит место на отдельный контрол — ровно
  // как иконка той же панели в рельсе. По умолчанию это закрытие, но попап-превью
  // подставляет сюда булавку «закрепить».
  // Считается ДО ранних return: от него зависит эффект наведения ниже, а хуки
  // обязаны вызываться на каждом рендере.
  const act = iconAction ?? (closeMode === 'icon' && onClose
    ? { Icon: X, title: 'Скрыть панель', onClick: onClose }
    : null);
  const closeByIcon = !!act;

  // Наведение на саму ШАПКУ — тоже нативно, и по той же причине. С react-пропсами
  // переход курсора с шапки на её контрол читался как уход: контролы приезжают
  // порталом, в React-дереве они не потомки шапки, и onMouseLeave срабатывал на
  // каждом таком переходе — крестик на иконке панели мигал, пока ведёшь мышь по
  // ряду кнопок. Нативные mouseenter/mouseleave смотрят на настоящий DOM, где
  // кнопки лежат внутри шапки, и молчат при перемещении внутри неё.
  useEffect(() => {
    if (!headerEl || !closeByIcon) return;
    const on = () => setHeaderHover(true);
    const off = () => setHeaderHover(false);
    headerEl.addEventListener('mouseenter', on);
    headerEl.addEventListener('mouseleave', off);
    return () => {
      headerEl.removeEventListener('mouseenter', on);
      headerEl.removeEventListener('mouseleave', off);
    };
  }, [headerEl, closeByIcon]);

  // hideIfEmpty: Children.toArray уже отбрасывает null/undefined/false/true/""
  // (React делает это автоматически). Если после фильтрации пусто — не рендерим.
  const realChildren = Children.toArray(children);
  if (hideIfEmpty && realChildren.length === 0) {
    return null;
  }

  // Bare-режим — только контент, без обёртки. Используется когда панель
  // уже встроена в другой PanelShell (cc-panels, мульти-стек).
  if (bare) {
    return <>{children}</>;
  }

  // Кнопка закрытия — короткий путь: caller может передать свой headerActions
  // целиком, тогда onClose игнорируется
  const actions = headerActions ?? (onClose && !closeByIcon ? (
    <IconButton size="xs" title="Скрыть панель" onClick={onClose}>
      <X size={14} strokeWidth={ICON_STROKE} />
    </IconButton>
  ) : undefined);

  // Место иконки в потоке шапки — ровно размер самой иконки (15), как было до
  // появления кнопки-действия: иначе иконка и заголовок съезжали бы вправо на
  // половину разницы. Кнопка под курсором крупнее слота и выступает за него
  // симметрично — слева её принимает padding шапки, справа зазор до заголовка.
  const headerIcon = (
    <span style={{
      position: 'relative', width: 15, height: 15, flexShrink: 0,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>
      {act && headerHover ? (
        // Под курсором иконка панели превращается в полноценную кнопку —
        // с подложкой, чтобы читалась как нажимаемая, а не как значок.
        // draggable=false — иначе нажатие начнёт тащить карточку за шапку.
        <span
          draggable={false}
          onDragStart={e => e.preventDefault()}
          style={{ position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', display: 'flex' }}
        >
          <IconButton size="xs" variant="soft" title={act.title} onClick={act.onClick}>
            <act.Icon size={14} strokeWidth={ICON_STROKE} />
          </IconButton>
        </span>
      ) : icon}
    </span>
  );

  return (
    <PanelHeaderSlotContext.Provider value={slotValue}>
    <Island
      bg={ISLAND.bg}
      borderColor={dropTarget || flash ? C.accent : ISLAND.border}
      // Перетаскиваемая панель «вдавливается»: теряет тень и чуть уменьшается,
      // будто прижата к холсту. Полупрозрачной её не делаем — сквозь неё
      // просвечивал контент, и было не понять, что именно едет за курсором.
      shadow={dropTarget ? `0 0 0 1px ${C.accent}` : dragged ? 'none' : ISLAND.shadow}
      style={{
        // fill=true (дефолт) — flex:1, панель растягивается на всю высоту.
        // fill=false — flex: '0 1 auto', панель по контенту, но сжимается
        // если не влезает. maxHeight: '100%' ограничивает сверху высотой
        // родителя — тогда срабатывает внутренний скролл контента.
        flex: fill ? 1 : '0 1 auto',
        maxHeight: fill ? undefined : '100%',
        opacity: (mounted || !animate) ? 1 : 0,
        transform: !(mounted || !animate)
          ? (slideDirection === 'left' ? 'translateX(-5px) scale(0.99)' : 'translateY(5px) scale(0.99)')
          : dragged ? 'scale(0.985)' : 'translateY(0) scale(1)',
        transition: animate
          ? 'border-color 0.1s, box-shadow 0.15s, opacity 0.12s ease-out, transform 0.12s ease-out'
          : 'border-color 0.1s, box-shadow 0.15s, transform 0.12s ease-out',
        ...style,
      }}
      rootProps={{
        ...rootProps,
        className: flash ? `cc-panel-flash ${rootProps?.className ?? ''}`.trim() : rootProps?.className,
      }}
      rootRef={setRoots}
    >
      <IslandHeader
        icon={headerIcon}
        title={title}
        badge={badge}
        actions={actions ? <span {...controlsHoverProps} style={{ display: 'flex', alignItems: 'center', gap: 4, ...controlsFade }}>{actions}</span> : actions}
        // Левый слот — у самого названия панели (PanelHeaderSlot side="left")
        leading={<div ref={setSlotLeftEl} draggable={false} onDragStart={e => e.preventDefault()}
          style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4, ...controlsFade }} />}
        headerProps={{
          ...headerProps,
          ref: setHeaderEl,
          // Над кнопками карточка не тащится: draggable снимается с шапки, пока
          // курсор в зоне контролов. Одного draggable={false} на самих кнопках мало —
          // dragstart рождается на шапке (она источник), их обработчики его не видят,
          // и попытка нажать кнопку уезжала перетаскиванием панели
          draggable: draggable && !overControls,
          title: draggable && !overControls ? 'Перетащите, чтобы поменять панели местами' : headerProps?.title,
          style: {
            ...headerProps?.style,
            cursor: draggable && !overControls ? 'grab' : 'default',
          },
        }}
      >
        {/* Слот контролов панели: сюда порталом приезжает содержимое PanelHeaderSlot.
            Наведение отслеживается нативно (см. эффект выше) */}
        <div
          ref={setSlotEl}
          draggable={false}
          onDragStart={e => e.preventDefault()}
          style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4, ...controlsFade }}
        />
        {/* Закреплённый слот (PanelHeaderSlot pinned) — главное действие панели.
            Живёт вне controlsFade: видно всегда, включая покой. Стоит после общей
            группы, поэтому при скрытых контролах кнопка просто прижимается вправо,
            а не прыгает по шапке. В покое приглушено — залитая accent-кнопка в
            каждой панели перетягивала бы внимание на себя; под курсором выходит
            на полный контраст. Схлопывать по ширине, как controlsFade, нельзя:
            кнопка остаётся кликабельной. */}
        <div
          ref={setSlotPinnedEl}
          draggable={false}
          onDragStart={e => e.preventDefault()}
          style={{
            flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4,
            opacity: controlsVisible ? 1 : 0.55,
            transition: 'opacity 0.1s ease-out',
          }}
        />
      </IslandHeader>

      {/* Тулбар под шапкой — полоса с фильтрами/переключателями/кнопкой "Новый".
          Раньше каждый сайдбар делал это через div padding borderBottom руками. */}
      {toolbar && (
        <div style={{
          flexShrink: 0,
          padding: '8px 10px 9px',
          borderBottom: `1px solid ${C.border}`,
          background: ISLAND.bg,
          display: 'flex',
          flexDirection: 'column',
          gap: 8,
        }}>
          {toolbar}
        </div>
      )}

      {/* Контентная зона — белая. Скроллится внутри (если не noScroll).
          fill=false: flex:'0 1 auto' — занимает по контенту, а не растягивается.
          При этом minHeight:0 позволяет сжиматься, если контент больше maxHeight
          Island'а (100% от родителя) — тогда срабатывает внутренний скролл. */}
      <div style={{
        flex: fill ? 1 : '0 1 auto',
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        overflow: noScroll ? 'visible' : 'hidden',
        background: C.bgWhite,
        ...contentStyle,
      }}>
        {children}
      </div>
    </Island>
    </PanelHeaderSlotContext.Provider>
  );
}
