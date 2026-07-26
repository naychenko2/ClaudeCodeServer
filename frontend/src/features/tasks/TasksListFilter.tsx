// Фильтр списка задач (режим проекта): кнопка-триггер (Funnel) + поповер с полным
// составом фильтров (Статус / Исполнитель / Приоритет / Срок). На мобиле поповер
// превращается в боттом-шит. Стиль и состав — по макету docs/mockups/tasks-filter-variant-a.html.
// Сама фильтрация списка живёт в TasksPanel (applyTaskFilters); здесь — только UI и
// тип состояния. Источник истины по точкам/лейблам — lib/tasks (STATUS_DOT/PRIORITY_*),
// срок — та же группировка, что в TasksPanel.dateGroupKey.

import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Funnel } from 'lucide-react';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import {
  STATUS_DOT, STATUS_LABEL, PRIORITY_COLOR, PRIORITY_LABEL, PRIORITY_ORDER, daysFromToday,
} from '../../lib/tasks';
import type { Task, TaskPriority, TaskStatus } from '../../types';
import { useIsMobile } from '../../lib/breakpoints';

// === Состояние фильтров ===

export type TaskAssigneeFilter = 'all' | 'me' | 'claude';

export interface TaskListFilters {
  status: TaskStatus[];
  assignee: TaskAssigneeFilter;
  priorities: TaskPriority[];
  due: DueKey[];   // ключи taskDueKey: overdue|today|week|later|none
}

export const EMPTY_TASK_FILTERS: TaskListFilters = {
  status: [], assignee: 'all', priorities: [], due: [],
};

// Число активных ГРУПП фильтров — его показывает бейдж на кнопке Funnel
// (как indicator в ToolbarOverflowMenu / BoardToolbar).
export function countActiveFilterGroups(f: TaskListFilters): number {
  let n = 0;
  if (f.status.length > 0) n++;
  if (f.assignee !== 'all') n++;
  if (f.priorities.length > 0) n++;
  if (f.due.length > 0) n++;
  return n;
}

// === Логика фильтрации (общая для TasksPanel и счётчика в WorkspacePage) ===

export type DueKey = 'overdue' | 'today' | 'week' | 'later' | 'none';

// Группа срока задачи — та же логика, что в TasksPanel.dateGroupKey, вынесена сюда,
// чтобы фильтр и список задач пользовались единым правилом группировки.
export function taskDueKey(t: Task): DueKey {
  if (!t.dueDate) return 'none';
  const diff = daysFromToday(t.dueDate);
  if (diff < 0) return 'overdue';
  if (diff === 0) return 'today';
  if (diff < 7) return 'week';
  return 'later';
}

// Применяет фильтры к массиву задач. Пустой набор фильтров — отдаёт массив как есть.
export function applyTaskFilters(tasks: Task[], f: TaskListFilters): Task[] {
  if (countActiveFilterGroups(f) === 0) return tasks;
  return tasks.filter(t => {
    if (f.status.length > 0 && !f.status.includes(t.status)) return false;
    if (f.assignee !== 'all' && t.assignee !== f.assignee) return false;
    if (f.priorities.length > 0 && !f.priorities.includes(t.priority)) return false;
    if (f.due.length > 0 && !f.due.includes(taskDueKey(t))) return false;
    return true;
  });
}

// === Опции секций поповера ===

// Порядок статусов в чипах — как в макете А: В работе / К выполнению / Готово
const STATUS_FILTER_ORDER: TaskStatus[] = ['inProgress', 'todo', 'done'];

const ASSIGNEE_OPTIONS: { value: TaskAssigneeFilter; label: string }[] = [
  { value: 'all',    label: 'Все' },
  { value: 'me',     label: 'Я' },
  { value: 'claude', label: 'AI' },
];

// Срок: все 5 групп taskDueKey (включая «Позже») — иначе задачи «позже» выпадали бы
// при любом активном фильтре срока. Лейблы/цвета — из TasksPanel.DATE_GROUPS.
const DUE_FILTER_OPTIONS: { key: DueKey; label: string; dot: string }[] = [
  { key: 'overdue', label: 'Просрочено',  dot: C.danger },
  { key: 'today',   label: 'Сегодня',     dot: C.accent },
  { key: 'week',    label: 'Эта неделя',  dot: C.warning },
  { key: 'later',   label: 'Позже',       dot: C.textMuted },
  { key: 'none',    label: 'Без срока',   dot: C.textMuted },
];

function toggleIn<T>(arr: T[], v: T): T[] {
  return arr.includes(v) ? arr.filter(x => x !== v) : [...arr, v];
}

// === Стилевые константы (по макету, токены проекта) ===

const SECTION_LABEL: CSSProperties = {
  display: 'block', fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700,
  letterSpacing: '0.07em', textTransform: 'uppercase', color: C.textMuted, marginBottom: 6,
};

// === Кнопка-триггер + поповер ===

// variant:
//   'icon'    — компактная 26×26 в шапке острова (cc-panels)
//   'sidebar' — квадратная 34px в строке действий старого сайдбара
export function TasksListFilterButton({ variant, filters, onFilters, total, found, isMobile }: {
  variant: 'icon' | 'sidebar';
  filters: TaskListFilters;
  onFilters: (f: TaskListFilters) => void;
  total: number;   // всего задач (до фильтров) — для счётчика «N из M»
  found: number;   // найдено после фильтров
  isMobile?: boolean;
}) {
  const mobile = isMobile ?? useIsMobile();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const count = countActiveFilterGroups(filters);
  const active = count > 0;

  // Закрытие: клик вне (десктоп) + Esc (везде) — паттерн ToolbarOverflowMenu
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDown); document.removeEventListener('keydown', onKey); };
  }, [open]);

  const badge: CSSProperties = variant === 'icon'
    ? { top: -4, right: -5, minWidth: 14, height: 14, border: `1.5px solid ${C.bgMain}` }
    : { top: -5, right: -5, minWidth: 15, height: 15, border: `1.5px solid ${C.bgPanel}` };

  const trigger = variant === 'icon' ? (
    <button
      type="button" title="Фильтр" aria-haspopup="menu" aria-expanded={open}
      onClick={() => setOpen(o => !o)}
      style={{
        position: 'relative', width: 28, height: 28, border: 'none', borderRadius: R.sm,
        background: active ? C.accentLight : 'transparent', color: active ? C.accent : C.textMuted,
        cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}
    >
      <Funnel size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {count > 0 && <FilterBadge count={count} style={badge} />}
    </button>
  ) : (
    <button
      type="button" title="Фильтр" aria-haspopup="menu" aria-expanded={open}
      onClick={() => setOpen(o => !o)}
      style={{
        position: 'relative', width: 34, height: 34, boxSizing: 'border-box',
        border: `1px solid ${active ? C.accent : C.border}`, borderRadius: R.lg,
        background: active ? C.accentLight : C.bgWhite, color: active ? C.accent : C.textMuted,
        cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}
    >
      <Funnel size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {count > 0 && <FilterBadge count={count} style={badge} />}
    </button>
  );

  const sections = (
    <FilterSections filters={filters} onFilters={onFilters} mobile={mobile} />
  );

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'inline-flex' }}>
      {trigger}

      {/* Десктоп: поповер прибит к правому краю триггера, поверх списка */}
      {open && !mobile && (
        <div role="menu" style={{
          position: 'absolute', top: 'calc(100% + 4px)', right: 0,
          width: 252, boxSizing: 'border-box',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: '12px 12px 10px', zIndex: Z.dropdown,
        }}>
          {sections}
          <FilterFooter
            onReset={() => onFilters(EMPTY_TASK_FILTERS)}
            found={found} total={total} resetDisabled={count === 0}
          />
        </div>
      )}

      {/* Мобила: боттом-шит с крупными tap-целями и кнопкой «Показать N задач» */}
      {open && mobile && createPortal(
        <div
          style={{ position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal, display: 'flex', alignItems: 'flex-end' }}
          onMouseDown={() => setOpen(false)}
        >
          <div
            style={{
              width: '100%', background: C.bgWhite,
              borderTopLeftRadius: R.sheet, borderTopRightRadius: R.sheet,
              boxShadow: SHADOW.sheet, padding: '8px 14px 14px',
              maxHeight: '82vh', overflowY: 'auto',
              paddingBottom: 'calc(14px + env(safe-area-inset-bottom, 0px))',
            }}
            onMouseDown={e => e.stopPropagation()}
            role="dialog" aria-modal="true" aria-label="Фильтры задач"
          >
            <div style={{ width: 38, height: 4, borderRadius: 999, background: C.border, margin: '6px auto 10px' }} />
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
              <span style={{ fontFamily: FONT.sans, fontSize: 15, fontWeight: 700, color: C.textHeading }}>Фильтры</span>
              <button
                type="button" disabled={count === 0} onClick={() => onFilters(EMPTY_TASK_FILTERS)}
                style={resetBtnStyle(count === 0)}
              >
                Сбросить
              </button>
            </div>
            {sections}
            <button
              type="button" onClick={() => setOpen(false)}
              style={{
                marginTop: 14, width: '100%', padding: '11px 12px', border: 'none', borderRadius: R.xl,
                background: C.accent, color: C.onAccent, fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700, cursor: 'pointer',
              }}
            >
              Показать {found} {pluralTasks(found)}
            </button>
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

function pluralTasks(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'задачу';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'задачи';
  return 'задач';
}

// Родительный падеж для счётчика «N из M …»: 1 → «задачи», иначе «задач»
function pluralGenFor(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'задачи';
  return 'задач';
}

function FilterBadge({ count, style }: { count: number; style: CSSProperties }) {
  return (
    <span style={{
      position: 'absolute', padding: '0 3px', borderRadius: 999,
      background: C.accent, color: C.onAccent,
      fontFamily: FONT.mono, fontSize: 9, fontWeight: 600, lineHeight: '14px', textAlign: 'center',
      pointerEvents: 'none', ...style,
    }}>
      {count}
    </span>
  );
}

// === Секции фильтров ===

function FilterSections({ filters, onFilters, mobile }: {
  filters: TaskListFilters;
  onFilters: (f: TaskListFilters) => void;
  mobile: boolean;
}) {
  return (
    <>
      {/* Статус */}
      <FilterSection label="Статус">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {STATUS_FILTER_ORDER.map(s => (
            <FilterChip
              key={s} mobile={mobile} active={filters.status.includes(s)}
              dot={STATUS_DOT[s]}
              onClick={() => onFilters({ ...filters, status: toggleIn(filters.status, s) })}
            >
              {STATUS_LABEL[s]}
            </FilterChip>
          ))}
        </div>
      </FilterSection>

      {/* Исполнитель — сегмент Все / Я / AI (как в BoardToolbar) */}
      <FilterSection label="Исполнитель">
        <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
          {ASSIGNEE_OPTIONS.map(o => {
            const on = filters.assignee === o.value;
            return (
              <button
                key={o.value} type="button"
                onClick={() => onFilters({ ...filters, assignee: o.value })}
                style={{
                  flex: 1, padding: mobile ? '9px 11px' : '6px 11px', cursor: 'pointer', border: 'none',
                  background: on ? C.accentLight : C.bgWhite, color: on ? C.accent : C.textSecondary,
                  fontFamily: FONT.sans, fontSize: mobile ? 12.5 : 12, fontWeight: on ? 700 : 500,
                }}
              >
                {o.label}
              </button>
            );
          })}
        </div>
      </FilterSection>

      {/* Приоритет */}
      <FilterSection label="Приоритет">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {PRIORITY_ORDER.map(p => (
            <FilterChip
              key={p} mobile={mobile} active={filters.priorities.includes(p)}
              dot={PRIORITY_COLOR[p]}
              onClick={() => onFilters({ ...filters, priorities: toggleIn(filters.priorities, p) })}
            >
              {PRIORITY_LABEL[p]}
            </FilterChip>
          ))}
        </div>
      </FilterSection>

      {/* Срок */}
      <FilterSection label="Срок">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {DUE_FILTER_OPTIONS.map(o => (
            <FilterChip
              key={o.key} mobile={mobile} active={filters.due.includes(o.key)}
              dot={o.dot}
              onClick={() => onFilters({ ...filters, due: toggleIn(filters.due, o.key) })}
            >
              {o.label}
            </FilterChip>
          ))}
        </div>
      </FilterSection>
    </>
  );
}

function FilterSection({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ marginBottom: 12 }}>
      <span style={SECTION_LABEL}>{label}</span>
      {children}
    </div>
  );
}

// Чип мультивыбора: точка-цвет + лейбл; активный — accentLight + accent-рамка.
function FilterChip({ active, dot, mobile, onClick, children }: {
  active: boolean; dot: string; mobile: boolean; onClick: () => void; children: ReactNode;
}) {
  return (
    <button
      type="button" onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, cursor: 'pointer',
        padding: mobile ? '7px 12px' : '5px 10px',
        border: `1px solid ${active ? C.accent : C.border}`, borderRadius: R.max,
        background: active ? C.accentLight : C.bgWhite,
        fontFamily: FONT.sans, fontSize: mobile ? 12.5 : 12, fontWeight: active ? 700 : 500,
        color: active ? C.textHeading : C.textPrimary,
      }}
    >
      <span style={{ width: 7, height: 7, borderRadius: '50%', background: dot, flexShrink: 0 }} />
      {children}
    </button>
  );
}

function FilterFooter({ onReset, found, total, resetDisabled }: {
  onReset: () => void; found: number; total: number; resetDisabled: boolean;
}) {
  return (
    <div style={{
      marginTop: 12, paddingTop: 10, borderTop: `1px solid ${C.borderLight}`,
      display: 'flex', alignItems: 'center', justifyContent: 'space-between',
    }}>
      <button type="button" disabled={resetDisabled} onClick={onReset} style={resetBtnStyle(resetDisabled)}>
        Сбросить
      </button>
      <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted }}>
        {found} из {total} {pluralGenFor(total)}
      </span>
    </div>
  );
}

function resetBtnStyle(disabled: boolean): CSSProperties {
  return {
    border: 'none', background: 'none', cursor: disabled ? 'default' : 'pointer',
    fontFamily: FONT.sans, fontSize: 12, fontWeight: 600,
    color: disabled ? C.textMuted : C.danger, padding: '2px 0', opacity: disabled ? 0.5 : 1,
  };
}
