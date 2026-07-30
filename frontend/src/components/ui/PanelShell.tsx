import { useEffect, useMemo, useState, type CSSProperties, type HTMLAttributes, type ReactNode } from 'react';
import { Children } from 'react';
import { X, type LucideIcon } from 'lucide-react';
import { C, ISLAND } from '../../lib/design';
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

  // Узел-слот в шапке, куда панель-содержимое телепортирует свои контролы
  // (PanelHeaderSlot). Через состояние, а не ref: портал должен отрисоваться
  // после того, как узел появился в DOM, а значит нужен повторный рендер.
  const [slotEl, setSlotEl] = useState<HTMLDivElement | null>(null);
  const slotValue = useMemo(() => ({ hasHeader: true, el: slotEl }), [slotEl]);

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

  // Действие на иконке: пока курсор на шапке, иконка панели слева подменяется
  // кнопкой и кликается. Так шапка не тратит место на отдельный контрол — ровно
  // как иконка той же панели в рельсе. По умолчанию это закрытие, но попап-превью
  // подставляет сюда булавку «закрепить».
  const act = iconAction ?? (closeMode === 'icon' && onClose
    ? { Icon: X, title: 'Скрыть панель', onClick: onClose }
    : null);
  const closeByIcon = !!act;

  // Кнопка закрытия — короткий путь: caller может передать свой headerActions
  // целиком, тогда onClose игнорируется
  const actions = headerActions ?? (onClose && !closeByIcon ? (
    <IconButton size="xs" title="Скрыть панель" onClick={onClose}>
      <X size={14} strokeWidth={ICON_STROKE} />
    </IconButton>
  ) : undefined);

  // Место иконки в шапке — всегда 24×24, независимо от того, лежит там иконка
  // панели или кнопка действия. Иначе при наведении подмена меняла бы ширину
  // и заголовок дёргался вбок.
  const headerIcon = (
    <span style={{
      width: 24, height: 24, flexShrink: 0,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>
      {act && headerHover ? (
        // Под курсором иконка панели превращается в полноценную кнопку —
        // с подложкой, чтобы читалась как нажимаемая, а не как значок.
        // draggable=false — иначе нажатие начнёт тащить карточку за шапку.
        <span draggable={false} onDragStart={e => e.preventDefault()} style={{ display: 'flex' }}>
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
      rootRef={rootRef}
    >
      <IslandHeader
        icon={headerIcon}
        title={title}
        badge={badge}
        actions={actions}
        headerProps={{
          ...headerProps,
          draggable,
          title: draggable ? 'Перетащите, чтобы поменять панели местами' : headerProps?.title,
          style: { ...headerProps?.style, cursor: draggable ? 'grab' : 'default' },
          ...(closeByIcon ? {
            onMouseEnter: () => setHeaderHover(true),
            onMouseLeave: () => setHeaderHover(false),
          } : null),
        }}
      >
        {/* Слот контролов панели: сюда порталом приезжает содержимое
            PanelHeaderSlot. draggable=false — чтобы взаимодействие с кнопками
            не инициировало перетаскивание карточки за шапку. */}
        <div
          ref={setSlotEl}
          draggable={false}
          onDragStart={e => e.preventDefault()}
          style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4 }}
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
