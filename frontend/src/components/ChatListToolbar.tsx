import { useEffect, useRef, useState } from 'react';
import {
  Plus, CalendarDays, Tags, Layers, List, ListTree, Check,
  ArrowDownWideNarrow, ArrowUpNarrowWide, CalendarArrowDown, CalendarArrowUp,
  SlidersHorizontal, Archive,
} from 'lucide-react';
import type { Persona, Session } from '../types';
import { C, FONT, FS, SP } from '../lib/design';
import { Button, IconButton, Menu, MenuItem, Modal, PanelHeaderSlot, PillSwitch, Toggle, useHasPanelHeader, usePanelElement, usePanelHeaderHold } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { FilterBar } from './FilterBar';
import { ALL_SORT_ORDERS, type ChatFilters, type ChatGroupBy, type ChatSortOrder } from '../lib/chatFilters';

// === Однострочный тулбар списка чатов (макет chat-unified-view, вариант А) ===
// [+] главное действие → группировка (PillSwitch) → фильтры с бейджем → сортировка →
// иерархия → архив. Поиска здесь нет — он первой секцией поповера фильтров.
// «Архивные» — не фильтр, а РЕЖИМ списка (ось filters.archivedOnly): включён — видно
// только архивные чаты, поэтому переключатель стоит рядом с осями вида, а не в поповере.
// Ступени по ширине панели (ResizeObserver):
//   comfort ≥400: «+ Новый» текстом, IconButton md
//   cozy 260–399: «+» квадрат 32, IconButton sm
//   compact <260: PillSwitch → IconButton с иконкой текущего режима + ui/Menu
//   mobile: «+ Новый чат» flex, IconButton lg; оси — шторкой «Вид» (ui/Modal)
// Группировка на десктопе — всегда PillSwitch iconsOnly (иконки + title-подсказки):
// строка узкая, текст сегментов не влезает даже на comfort.

const GROUP_BY_META: Record<ChatGroupBy, { label: string; title: string; Icon: typeof CalendarDays }> = {
  days: { label: 'Дни', title: 'Группировка по дням', Icon: CalendarDays },
  tags: { label: 'Теги', title: 'Группировка по тегам', Icon: Tags },
  // Темы чатов: опцию в groupByOptions передаёт только список проекта
  subjects: { label: 'Темы', title: 'Группировка по темам', Icon: Layers },
  none: { label: 'Без', title: 'Без группировки', Icon: List },
};

// Ось порядка: стрелки — по активности чата, календарь — по времени создания.
// label — короткая подпись сегмента в мобильной шторке (четыре пункта в строку),
// title — полная подсказка кнопки-переключателя.
const SORT_META: Record<ChatSortOrder, { title: string; label: string; Icon: typeof ArrowDownWideNarrow }> = {
  newest: { title: 'Сортировка: новые сверху', label: 'Новые', Icon: ArrowDownWideNarrow },
  oldest: { title: 'Сортировка: старые сверху', label: 'Старые', Icon: ArrowUpNarrowWide },
  'created-newest': {
    title: 'Сортировка: сначала новые по созданию', label: 'Созданы новые', Icon: CalendarArrowDown,
  },
  'created-oldest': {
    title: 'Сортировка: сначала старые по созданию', label: 'Созданы старые', Icon: CalendarArrowUp,
  },
};

// Кнопка-переключатель циклит все значения оси по кругу (порядок белого списка):
// новые → старые → новые по созданию → старые по созданию → снова новые
const nextSortOrder = (v: ChatSortOrder): ChatSortOrder =>
  ALL_SORT_ORDERS[(ALL_SORT_ORDERS.indexOf(v) + 1) % ALL_SORT_ORDERS.length];

type Tier = 'comfort' | 'cozy' | 'compact';

interface ChatListToolbarProps {
  onNew: () => void;
  creating?: boolean;
  // Оффлайн — кнопка создания не рисуется (создать чат без сети нельзя), тулбар остаётся
  hideNew?: boolean;
  // Полный список области — для счётчиков чипов в поповере фильтров
  sessions: Session[];
  filters: ChatFilters;
  patch: (p: Partial<ChatFilters>) => void;
  allPersonas: Persona[];
  hiddenCount: number;
  isMobile?: boolean;
  // Доступные режимы группировки. У глобального списка (чаты вне проектов) реестра
  // тегов нет — передавай ['days', 'none']
  groupByOptions?: ChatGroupBy[];
}

// Заголовок секции в мобильной шторке «Вид»
function SheetSec({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: FS.xs, fontWeight: 700, color: C.textMuted, fontFamily: FONT.sans,
      textTransform: 'uppercase', letterSpacing: '0.06em', margin: '10px 0 6px',
    }}>
      {children}
    </div>
  );
}

// Кнопка-меню группировки: иконка = текущий режим, клик — меню режимов с галкой.
// Одна реализация на compact-ступень сайдбара и на шапку карточки.
function GroupByMenuButton({ size, value, options, onChange }: {
  size: 'xs' | 'sm';
  value: ChatGroupBy;
  options: ChatGroupBy[];
  onChange: (v: ChatGroupBy) => void;
}) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  // Рамка карточки на момент открытия: меню кнопки справа в узкой панели иначе
  // свисало за её левую кромку
  const [boundary, setBoundary] = useState<DOMRect | null>(null);
  const panel = usePanelElement();
  // Меню живёт порталом в body: курсор уходит с карточки, и шапка гасила бы кнопку
  // вместе с остальными значками — меню повисало без якоря
  usePanelHeaderHold(!!anchor);
  const meta = GROUP_BY_META[value] ?? GROUP_BY_META.days;
  return (
    <>
      <IconButton size={size}
        active={!!anchor} expanded={!!anchor}
        title={`Группировка: ${value === 'none' ? 'без группировки' : meta.label} (сменить)`}
        onClick={e => {
          setBoundary(panel?.getBoundingClientRect() ?? null);
          setAnchor(e.currentTarget.getBoundingClientRect());
        }}>
        <meta.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
      {anchor && (
        // Высота — от числа режимов: строка 38 + заголовок; четыре в 180 не влезали
        <Menu anchor={anchor} boundary={boundary} minWidth={230} maxHeight={options.length * 38 + 40}
          onClose={() => setAnchor(null)}>
          <div style={{
            padding: '7px 10px 3px', fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
            textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
          }}>
            Группировка
          </div>
          {options.map(v => {
            const m = GROUP_BY_META[v];
            return (
              <MenuItem
                key={v}
                icon={<m.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                onClick={() => { onChange(v); setAnchor(null); }}
                label={
                  <span style={{
                    display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                    flex: 1, gap: SP.sm,
                  }}>
                    {v === 'none' ? 'Без группировки' : m.label}
                    {v === value && <Check size={ICON_SIZE.xs} strokeWidth={2.4} style={{ color: C.accent, flexShrink: 0 }} />}
                  </span>
                }
              />
            );
          })}
        </Menu>
      )}
    </>
  );
}

export function ChatListToolbar({
  onNew, creating, hideNew, sessions, filters, patch, allPersonas, hiddenCount,
  isMobile = false, groupByOptions = ['days', 'tags', 'none'],
}: ChatListToolbarProps) {
  const rootRef = useRef<HTMLDivElement>(null);
  // Список живёт в карточке с шапкой — контролы уезжают туда (как у «Задач» и
  // «Изменений»), и собственная строка тулбара в теле не нужна вовсе
  const inHeader = useHasPanelHeader();
  // Ширина панели для ступени; 288 — дефолт сайдбара, чтобы не моргнуть compact на старте
  const [width, setWidth] = useState(288);
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setWidth(el.offsetWidth));
    ro.observe(el);
    setWidth(el.offsetWidth);
    return () => ro.disconnect();
  }, []);

  const tier: Tier = width >= 400 ? 'comfort' : width >= 260 ? 'cozy' : 'compact';
  const iconBtnSize = tier === 'comfort' ? 'md' : 'sm';

  const { groupBy, sortOrder, hierarchy, archivedOnly } = filters;
  // groupBy из хранилища может отсутствовать в groupByOptions (напр. 'tags' у
  // глобального списка после переезда чата) — PillSwitch без активного сегмента
  // не рисует пилюлю, это валидное состояние; первый же выбор всё чинит.
  const pillOptions = groupByOptions.map(v => {
    const m = GROUP_BY_META[v];
    const Icon = m.Icon;
    return {
      value: v,
      label: m.label,
      title: m.title,
      icon: <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
    };
  });

  // Мобильная шторка «Вид»
  const [viewSheet, setViewSheet] = useState(false);

  const newIcon = <Plus size={15} strokeWidth={2.4} />;
  const sort = SORT_META[sortOrder];

  // === Мобильная ступень: [+ текстом flex] [Ф lg] [Вид lg] ===
  if (isMobile) {
    return (
      <div ref={rootRef} style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        padding: `8px ${SP.lg}px`, borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0,
      }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          {!hideNew && (
            <Button variant="primary" size="md" glow fullWidth loading={creating}
              onClick={onNew} leftIcon={newIcon}>
              Новый чат
            </Button>
          )}
        </div>
        <FilterBar
          sessions={sessions} filters={filters} patch={patch} allPersonas={allPersonas}
          hiddenCount={hiddenCount} isMobile triggerSize="lg"
        />
        <IconButton size="lg" title="Вид: группировка, сортировка, иерархия, архивные"
          onClick={() => setViewSheet(true)}>
          <SlidersHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>

        {viewSheet && (
          <Modal
            title="Вид списка"
            onClose={() => setViewSheet(false)}
            footer={
              <Button variant="primary" size="md" fullWidth onClick={() => setViewSheet(false)}>
                Готово
              </Button>
            }
          >
            <SheetSec>Группировка</SheetSec>
            {/* Четыре режима (с «Темами» в списке проекта) на 320 CSS подписями не влезают —
                autoCompact, как у сортировки */}
            <PillSwitch
              value={groupBy}
              options={pillOptions}
              onChange={v => patch({ groupBy: v })}
              fill isMobile autoCompact={pillOptions.length > 3}
            />
            <SheetSec>Сортировка</SheetSec>
            {/* Четыре пункта в строку: autoCompact оставляет подпись активному,
                остальные сегменты на узком экране сжимаются до иконок */}
            <PillSwitch<ChatSortOrder>
              value={sortOrder}
              options={ALL_SORT_ORDERS.map(v => {
                const m = SORT_META[v];
                const SortIcon = m.Icon;
                return {
                  value: v,
                  label: m.label,
                  title: m.title,
                  icon: <SortIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
                };
              })}
              onChange={v => patch({ sortOrder: v })}
              fill isMobile autoCompact
            />
            <SheetSec>Структура</SheetSec>
            <div style={{
              display: 'flex', alignItems: 'center', justifyContent: 'space-between',
              minHeight: 48, gap: SP.sm,
            }}>
              <span style={{ fontSize: FS.md, color: C.textPrimary, fontFamily: FONT.sans }}>
                Иерархия (вложенные чаты)
              </span>
              <Toggle checked={hierarchy} onChange={v => patch({ hierarchy: v })}
                ariaLabel="Иерархия (вложенные чаты)" />
            </div>
            <SheetSec>Архив</SheetSec>
            <div style={{
              display: 'flex', alignItems: 'center', justifyContent: 'space-between',
              minHeight: 48, gap: SP.sm,
            }}>
              <span style={{ fontSize: FS.md, color: C.textPrimary, fontFamily: FONT.sans }}>
                Архивные
              </span>
              <Toggle checked={archivedOnly} onChange={v => patch({ archivedOnly: v })}
                ariaLabel="Архивные" />
            </div>
          </Modal>
        )}
      </div>
    );
  }

  // === Контролы в шапке карточки: [фильтр] [сортировка] [группировка] [иерархия] [архив] [+ Чат] ===
  // Ряд нейтральных иконок 24px, главное действие последним и залитым — общий
  // порядок с «Задачами» и «Проектами». Пилюля группировки сюда не влезает —
  // вместо неё та же кнопка-меню, что на compact-ступени (иконка = текущий режим),
  // и это единственное место группировки в шапке — в поповере фильтров её нет.
  // Кнопка стоит и в списке вне проекта: там два режима (Дни / Без).
  // «+ Чат» — в закреплённом слоте (pinned): нейтральные иконки проявляются по
  // наведению, а главное действие видно всегда.
  if (inHeader) {
    return (
      <>
      <PanelHeaderSlot>
        <FilterBar
          sessions={sessions} filters={filters} patch={patch} allPersonas={allPersonas}
          hiddenCount={hiddenCount} triggerSize="xs"
        />
        <IconButton
          size="xs"
          active={sortOrder !== 'newest'}
          title={sort.title}
          onClick={() => patch({ sortOrder: nextSortOrder(sortOrder) })}
        >
          <sort.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        <GroupByMenuButton size="xs" value={groupBy} options={groupByOptions}
          onChange={v => patch({ groupBy: v })} />
        <IconButton
          size="xs"
          active={hierarchy}
          title={`Иерархия: ${hierarchy ? 'вкл' : 'выкл'}`}
          onClick={() => patch({ hierarchy: !hierarchy })}
        >
          <ListTree size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton
          size="xs"
          active={archivedOnly}
          title="Архивные"
          onClick={() => patch({ archivedOnly: !archivedOnly })}
        >
          <Archive size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </PanelHeaderSlot>
      {!hideNew && (
        <PanelHeaderSlot pinned>
          <Button
            variant="primary" size="xs" title="Новый чат" loading={creating}
            leftIcon={<Plus size={13} strokeWidth={ICON_STROKE} />}
            onClick={onNew}
          >
            Чат
          </Button>
        </PanelHeaderSlot>
      )}
      </>
    );
  }

  // === Десктопные ступени (без шапки: сайдбар раздела «Чаты», dev-витрина) ===
  return (
    <div ref={rootRef} style={{
      display: 'flex', alignItems: 'center', gap: tier === 'compact' ? 4 : 6,
      padding: tier === 'compact' ? '8px 8px' : '8px 12px',
      borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0,
    }}>
      {/* Главное действие — единственный залитый элемент строки */}
      {!hideNew && (tier === 'comfort' ? (
        <Button variant="primary" size="sm" glow loading={creating} onClick={onNew} leftIcon={newIcon}>
          Новый
        </Button>
      ) : (
        <Button variant="primary" size="sm" glow loading={creating} onClick={onNew}
          title="Новый чат" style={{ width: 32, padding: 0, flexShrink: 0 }}>
          {newIcon}
        </Button>
      ))}

      {/* Группировка: PillSwitch только иконками (comfort/cozy) или кнопка-меню на compact */}
      {tier === 'compact' ? (
        <GroupByMenuButton size="sm" value={groupBy} options={groupByOptions}
          onChange={v => patch({ groupBy: v })} />
      ) : (
        <PillSwitch
          value={groupBy}
          options={pillOptions}
          onChange={v => patch({ groupBy: v })}
          iconsOnly
        />
      )}

      <span style={{ flex: 1 }} />

      <FilterBar
        sessions={sessions} filters={filters} patch={patch} allPersonas={allPersonas}
        hiddenCount={hiddenCount} triggerSize={iconBtnSize}
      />
      <IconButton
        size={iconBtnSize}
        active={sortOrder !== 'newest'}
        title={sort.title}
        onClick={() => patch({ sortOrder: nextSortOrder(sortOrder) })}
      >
        <sort.Icon size={iconBtnSize === 'md' ? ICON_SIZE.sm : ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
      <IconButton
        size={iconBtnSize}
        active={hierarchy}
        title={`Иерархия: ${hierarchy ? 'вкл' : 'выкл'}`}
        onClick={() => patch({ hierarchy: !hierarchy })}
      >
        <ListTree size={iconBtnSize === 'md' ? ICON_SIZE.sm : ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
      <IconButton
        size={iconBtnSize}
        active={archivedOnly}
        title="Архивные"
        onClick={() => patch({ archivedOnly: !archivedOnly })}
      >
        <Archive size={iconBtnSize === 'md' ? ICON_SIZE.sm : ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
    </div>
  );
}
