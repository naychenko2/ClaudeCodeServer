import { useEffect, useRef, useState } from 'react';
import {
  Plus, CalendarDays, Tags, List, ListTree, Check, MonitorSmartphone,
  ArrowDownWideNarrow, ArrowUpNarrowWide, SlidersHorizontal, Archive,
} from 'lucide-react';
import type { Persona, Session } from '../types';
import { C, FONT, FS, SP } from '../lib/design';
import { Button, IconButton, Menu, MenuItem, Modal, PanelHeaderSlot, PillSwitch, Toggle, useHasPanelHeader } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { FilterBar } from './FilterBar';
import type { ChatFilters, ChatGroupBy, ChatSortOrder } from '../lib/chatFilters';

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
  none: { label: 'Без', title: 'Без группировки', Icon: List },
};

const SORT_META: Record<ChatSortOrder, { title: string; Icon: typeof ArrowDownWideNarrow }> = {
  newest: { title: 'Сортировка: новые сверху', Icon: ArrowDownWideNarrow },
  oldest: { title: 'Сортировка: старые сверху', Icon: ArrowUpNarrowWide },
};

type Tier = 'comfort' | 'cozy' | 'compact';

interface ChatListToolbarProps {
  onNew: () => void;
  // Второй тип чата — десктопный (ADR-008): отдельная кнопка рядом с «+», а не выбор
  // в диалоге. Тип задаётся ТОЛЬКО при создании и потом не меняется, поэтому дверей две.
  // undefined — грань в этом проекте не включена, кнопки нет вовсе
  onNewDesktop?: () => void;
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

export function ChatListToolbar({
  onNew, onNewDesktop, creating, hideNew, sessions, filters, patch, allPersonas, hiddenCount,
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

  // Меню группировки на compact-ступени (иконка кнопки = текущий режим)
  const [groupMenu, setGroupMenu] = useState<DOMRect | null>(null);
  // Мобильная шторка «Вид»
  const [viewSheet, setViewSheet] = useState(false);

  const newIcon = <Plus size={15} strokeWidth={2.4} />;
  // Кнопка десктопного чата: нейтральная иконка рядом с главным действием — накат
  // акцента на второй тип чата сделал бы из него равное главное действие
  const desktopBtn = (size: 'xs' | 'sm' | 'lg') => onNewDesktop && !hideNew ? (
    <IconButton size={size} title="Новый десктопный чат (руки на вашем компьютере)"
      onClick={onNewDesktop}>
      <MonitorSmartphone size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </IconButton>
  ) : null;
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
        {desktopBtn('lg')}
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
            <PillSwitch
              value={groupBy}
              options={pillOptions}
              onChange={v => patch({ groupBy: v })}
              fill isMobile
            />
            <SheetSec>Сортировка</SheetSec>
            <PillSwitch<ChatSortOrder>
              value={sortOrder}
              options={(['newest', 'oldest'] as ChatSortOrder[]).map(v => {
                const SortIcon = SORT_META[v].Icon;
                return {
                  value: v,
                  label: v === 'newest' ? 'Новые сверху' : 'Старые сверху',
                  icon: <SortIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
                };
              })}
              onChange={v => patch({ sortOrder: v })}
              fill isMobile
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

  // === Контролы в шапке карточки: [фильтр] [сортировка] [иерархия] [архив] [+ Чат] ===
  // Ряд нейтральных иконок 24px, главное действие последним и залитым — общий
  // порядок с «Задачами» и «Проектами». Пилюля группировки сюда не влезает и
  // уехала секцией в поповер фильтров: из трёх осей вида она самая редкая, а
  // фильтр, сортировка и иерархия остаются на виду одним кликом.
  // «+ Чат» — в закреплённом слоте (pinned): нейтральные иконки проявляются по
  // наведению, а главное действие видно всегда.
  if (inHeader) {
    return (
      <>
      <PanelHeaderSlot>
        <FilterBar
          sessions={sessions} filters={filters} patch={patch} allPersonas={allPersonas}
          hiddenCount={hiddenCount} triggerSize="xs"
          grouping={{
            value: groupBy,
            options: groupByOptions.map(v => {
              const m = GROUP_BY_META[v];
              return {
                value: v,
                label: v === 'none' ? 'Без группировки' : m.label,
                icon: <m.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
              };
            }),
            onChange: v => patch({ groupBy: v }),
          }}
        />
        <IconButton
          size="xs"
          active={sortOrder !== 'newest'}
          title={sort.title}
          onClick={() => patch({ sortOrder: sortOrder === 'newest' ? 'oldest' : 'newest' })}
        >
          <sort.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
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
          {desktopBtn('xs')}
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
  const groupByMeta = GROUP_BY_META[groupBy] ?? GROUP_BY_META.days;
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

      {desktopBtn('sm')}

      {/* Группировка: PillSwitch только иконками (comfort/cozy) или кнопка-меню на compact */}
      {tier === 'compact' ? (
        <>
          <IconButton size="sm" title={`Группировка: ${groupByMeta.label} (сменить)`}
            onClick={e => setGroupMenu(e.currentTarget.getBoundingClientRect())}>
            <groupByMeta.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
          {groupMenu && (
            <Menu anchor={groupMenu} minWidth={230} maxHeight={180} onClose={() => setGroupMenu(null)}>
              <div style={{
                padding: '7px 10px 3px', fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
                textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
              }}>
                Группировка
              </div>
              {groupByOptions.map(v => {
                const m = GROUP_BY_META[v];
                const active = v === groupBy;
                return (
                  <MenuItem
                    key={v}
                    icon={<m.Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                    onClick={() => { patch({ groupBy: v }); setGroupMenu(null); }}
                    label={
                      <span style={{
                        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                        flex: 1, gap: SP.sm,
                      }}>
                        {v === 'none' ? 'Без группировки' : m.label}
                        {active && <Check size={ICON_SIZE.xs} strokeWidth={2.4} style={{ color: C.accent, flexShrink: 0 }} />}
                      </span>
                    }
                  />
                );
              })}
            </Menu>
          )}
        </>
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
        onClick={() => patch({ sortOrder: sortOrder === 'newest' ? 'oldest' : 'newest' })}
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
