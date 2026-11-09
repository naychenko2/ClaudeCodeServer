// Фильтр списка задач (режим проекта): кнопка-триггер (Funnel) + поповер с полным
// составом фильтров (Статус / Исполнитель / Приоритет / Срок). На мобиле поповер
// превращается в боттом-шит. Стиль и состав — по макету docs/design/mockups/tasks-filter-variant-a.html.
// Сама фильтрация списка живёт в TasksPanel (applyTaskFilters); здесь — только UI и
// тип состояния. Источник истины по точкам/лейблам — lib/tasks (STATUS_DOT/PRIORITY_*),
// срок — та же группировка, что в TasksPanel.dateGroupKey.

import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Funnel } from 'lucide-react';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { IconButton, Chip } from '../../components/ui';
import {
  STATUS_DOT, STATUS_LABEL, PRIORITY_COLOR, PRIORITY_LABEL, PRIORITY_ORDER, daysFromToday,
} from '../../lib/tasks';
import type { Project, Task, TaskPriority, TaskStatus } from '../../types';
import { useIsMobile } from '../../lib/breakpoints';
import { useUserScrollGate } from '../../lib/userScrollGate';

// === Состояние фильтров ===

// 'member' — фильтр по человеку-исполнителю общего проекта (S2b-F5); есть только
// там, где вызывающий дал список участников (people). В личном проекте состава
// 'all' | 'me' | 'claude' достаточно.
export type TaskAssigneeFilter = 'all' | 'me' | 'claude' | 'member';

export interface TaskListFilters {
  status: TaskStatus[];
  assignee: TaskAssigneeFilter;
  // При assignee === 'member': id выбранного участника; null — любой человек
  assigneeUserId?: string | null;
  priorities: TaskPriority[];
  due: DueKey[];   // ключи taskDueKey: overdue|today|week|later|none
}

export const EMPTY_TASK_FILTERS: TaskListFilters = {
  status: [], assignee: 'all', assigneeUserId: null, priorities: [], due: [],
};

// Участник для выбора в фильтре «Участники» и подписи дорожек
export interface AssigneePersonOption { userId: string; name: string }

// Имя человека по строке участников/списка: displayName, запасной — username,
// нет обоих — «Участник» (то же правило, что в TaskUserBadge)
export function memberOptionName(p: { displayName?: string | null; username?: string | null }): string {
  return p.displayName?.trim() || p.username?.trim() || 'Участник';
}

// Общий ли проект для текущего взгляда (тот же критерий, что у SharedBadge):
// myRole авторитетна, когда есть; иначе — сводка участников. Личный проект — false.
export function isSharedProjectView(p: Pick<Project, 'myRole' | 'members'>): boolean {
  return p.myRole != null || (p.members?.count ?? 0) > 0;
}

// Владелец ли текущего взгляда: myRole авторитетна; по ownerId — когда её нет;
// ни того, ни другого (личный проект, старый бэк) — взгляд владельца, как и было
export function isOwnerProjectView(
  p: Pick<Project, 'myRole' | 'ownerId'>, currentUserId: string | null,
): boolean {
  if (p.myRole) return p.myRole === 'owner';
  if (currentUserId && p.ownerId) return currentUserId === p.ownerId;
  return true;
}

// Контекст взгляда для «Я»: кто я и владелец ли я этого проекта. Без контекста
// (личный проект, хаб) «Я» остаётся прежним: t.assignee === 'me'.
export interface AssigneeMatchContext {
  currentUserId?: string | null;
  isOwner?: boolean;
}

// Совпадает ли исполнитель задачи с фильтром (общая логика списка и доски, S2b-F5).
// К9: человек-исполнитель — assignee 'me' + assigneeUserId; нет assigneeUserId →
// старый «me» владельца. «Участники» без выбранного — любой человек с id.
export function matchTaskAssignee(
  t: Task, assignee: TaskAssigneeFilter, assigneeUserId: string | null | undefined,
  ctx?: AssigneeMatchContext,
): boolean {
  if (assignee === 'all') return true;
  if (assignee === 'claude') return t.assignee === 'claude';
  if (assignee === 'member') return assigneeUserId ? t.assigneeUserId === assigneeUserId : !!t.assigneeUserId;
  // 'me'. С контекстом (доска/список проекта) — как бэк (TaskManager.ResolveMeUserId):
  // любая человеческая задача без id — владельца; это и старый 'me' без id (К9), и задача
  // вообще без assignee (прежний дефолт Create). Без контекста (хаб) — прежнее поведение master.
  if (!ctx) return t.assignee === 'me';
  if (t.assignee === 'claude') return false;
  if (t.assigneeUserId) return t.assigneeUserId === (ctx.currentUserId ?? null);
  return ctx.isOwner === true;
}

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
// ctx — взгляд общего проекта («Я» по assigneeUserId, К9); без него — прежнее поведение.
export function applyTaskFilters(tasks: Task[], f: TaskListFilters, ctx?: AssigneeMatchContext): Task[] {
  if (countActiveFilterGroups(f) === 0) return tasks;
  return tasks.filter(t => {
    if (f.status.length > 0 && !f.status.includes(t.status)) return false;
    if (!matchTaskAssignee(t, f.assignee, f.assigneeUserId, ctx)) return false;
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

// «Участники» добавляется к сегменту только там, где есть список участников
// (общий проект): как в списке задач, так и в тулбаре доски (S2b-F5)
export function assigneeFilterOptions(people?: AssigneePersonOption[]): { value: TaskAssigneeFilter; label: string }[] {
  return people && people.length > 0
    ? [...ASSIGNEE_OPTIONS, { value: 'member', label: 'Участники' }]
    : ASSIGNEE_OPTIONS;
}

// Выбор конкретного участника под сегментом «Участники»: общий ui/Chip.
// Наружный отступ несёт вызывающий — в поповере и в строке тулбара он разный.
export function MemberChips({ people, selectedUserId, onPick, mobile }: {
  people: AssigneePersonOption[];
  selectedUserId?: string | null;
  onPick: (userId: string) => void;
  mobile?: boolean;
}) {
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
      {people.map(p => (
        <Chip
          key={p.userId} variant="toggle" large={mobile} minHeight={mobile ? 40 : undefined}
          selected={selectedUserId === p.userId}
          onClick={() => onPick(p.userId)}
        >
          {p.name}
        </Chip>
      ))}
    </div>
  );
}

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

// Ширина десктопного поповера (нужна до рендера — по ней клампится позиция)
const POPOVER_W = 252;

// variant:
//   'icon'    — компактная 26×26 в шапке острова (cc-panels)
//   'sidebar' — квадратная 34px в строке действий старого сайдбара
// people — участники общего проекта (загружает вызывающий, TasksPanel): с ними
// сегмент «Исполнитель» получает «Участники». Личный проект — people нет.
export function TasksListFilterButton({ variant, filters, onFilters, total, found, isMobile, people }: {
  variant: 'icon' | 'sidebar';
  filters: TaskListFilters;
  onFilters: (f: TaskListFilters) => void;
  total: number;   // всего задач (до фильтров) — для счётчика «N из M»
  found: number;   // найдено после фильтров
  isMobile?: boolean;
  people?: AssigneePersonOption[];
}) {
  // Хук вызывается безусловно (rules-of-hooks): prop лишь переопределяет его
  // значение, но не отменяет сам вызов — иначе порядок хуков плавал бы между
  // рендерами в зависимости от того, передали isMobile или нет.
  const hookMobile = useIsMobile();
  const mobile = isMobile ?? hookMobile;
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  // Рамка триггера на момент открытия — поповер живёт в портале и позиционируется
  // по ней (см. ниже, почему не absolute)
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const isUserScroll = useUserScrollGate();
  const count = countActiveFilterGroups(filters);
  const active = count > 0;

  const toggleOpen = () => {
    setAnchor(rootRef.current?.getBoundingClientRect() ?? null);
    setOpen(o => !o);
  };

  // Закрытие: клик вне (десктоп) + Esc (везде) — паттерн ToolbarOverflowMenu.
  // Поповер вынесен в портал, поэтому «вне» — это вне И триггера, И карточки.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (rootRef.current?.contains(t) || popRef.current?.contains(t)) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    // Прокрутка страницы/панели уводит карточку от кнопки — проще закрыть, чем
    // догонять. Но прокрутка ВНУТРИ самой карточки (список фильтров длиннее
    // экрана) — обычная работа с ней, а не уход: её пропускаем. Гейт «только
    // пользовательский скролл»: программный прижим ленты чата (активность Claude)
    // поповер не закрывает.
    const onScroll = (e: Event) => {
      if (!isUserScroll()) return;
      if (popRef.current?.contains(e.target as Node)) return;
      setOpen(false);
    };
    const onResize = () => setOpen(false);
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    window.addEventListener('resize', onResize);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('resize', onResize);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [open, isUserScroll]);

  const badge: CSSProperties = variant === 'icon'
    ? { top: -4, right: -5, minWidth: 14, height: 14, border: `1.5px solid ${C.bgMain}` }
    : { top: -5, right: -5, minWidth: 15, height: 15, border: `1.5px solid ${C.bgPanel}` };

  const trigger = variant === 'icon' ? (
    // Высота 24 — общая для всего ряда контролов шапки панели (см. design-guidelines)
    <IconButton
      size="xs" active={active} title="Фильтр" onClick={toggleOpen}
      style={{ position: 'relative' }}
    >
      <Funnel size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {count > 0 && <FilterBadge count={count} style={badge} />}
    </IconButton>
  ) : (
    <button
      type="button" title="Фильтр" aria-haspopup="menu" aria-expanded={open}
      onClick={toggleOpen}
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
    <FilterSections filters={filters} onFilters={onFilters} mobile={mobile} people={people} />
  );

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'inline-flex' }}>
      {trigger}

      {/* Десктоп: поповер висит от правого края триггера — но в ПОРТАЛЕ и fixed.
          Кнопка живёт в шапке панели, а карточка панели клиппует содержимое
          (Island: overflow hidden) и вдобавок несёт transform, из-за которого
          даже position:fixed привязался бы к ней. Абсолютный поповер такую
          панель не переживает — его срезает по её краю. */}
      {open && !mobile && anchor && createPortal(
        <div ref={popRef} role="menu" style={{
          position: 'fixed',
          top: anchor.bottom + 4,
          left: Math.max(8, Math.min(anchor.right - POPOVER_W, window.innerWidth - POPOVER_W - 8)),
          width: POPOVER_W, boxSizing: 'border-box',
          maxHeight: `calc(100vh - ${anchor.bottom + 16}px)`, overflowY: 'auto',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: '12px 12px 10px', zIndex: Z.dropdown,
        }}>
          {sections}
          <FilterFooter
            onReset={() => onFilters(EMPTY_TASK_FILTERS)}
            found={found} total={total} resetDisabled={count === 0}
          />
        </div>,
        document.body,
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

// Экспортируется для рендер-теста: чи́пы-фильтры (статус/приоритет/срок) — общий ui/Chip
// variant="toggle"; клик переключает группу. «Исполнитель» — отдельный сегмент, не чип.
// people (общий проект) добавляет к сегменту «Участники» и выбор конкретного участника.
export function FilterSections({ filters, onFilters, mobile, people }: {
  filters: TaskListFilters;
  onFilters: (f: TaskListFilters) => void;
  mobile: boolean;
  people?: AssigneePersonOption[];
}) {
  return (
    <>
      {/* Статус */}
      <FilterSection label="Статус">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {STATUS_FILTER_ORDER.map(s => {
            const selected = filters.status.includes(s);
            return (
              <Chip
                key={s} variant="toggle" large={mobile} minHeight={mobile ? 40 : undefined}
                selected={selected}
                onClick={() => onFilters({ ...filters, status: toggleIn(filters.status, s) })}
              >
                {/* Точка: на включённом чипе (заливка accent) — currentColor (onAccent),
                    иначе свой цвет; иначе accent-точка «В работе» тонула в accent-фоне */}
                <span style={{ width: 7, height: 7, borderRadius: '50%', background: selected ? 'currentColor' : STATUS_DOT[s], flexShrink: 0 }} />
                {STATUS_LABEL[s]}
              </Chip>
            );
          })}
        </div>
      </FilterSection>

      {/* Исполнитель — сегмент Все / Я / AI (+ «Участники» в общем проекте, как в BoardToolbar) */}
      <FilterSection label="Исполнитель">
        <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
          {assigneeFilterOptions(people).map(o => {
            const on = filters.assignee === o.value;
            return (
              <button
                key={o.value} type="button"
                // любой переход по сегменту начинает выбор участника заново
                onClick={() => onFilters({ ...filters, assignee: o.value, assigneeUserId: null })}
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
        {filters.assignee === 'member' && people && people.length > 0 && (
          <div style={{ marginTop: 6 }}>
            <MemberChips
              people={people} selectedUserId={filters.assigneeUserId} mobile={mobile}
              onPick={userId => onFilters({ ...filters, assignee: 'member', assigneeUserId: userId })}
            />
          </div>
        )}
      </FilterSection>

      {/* Приоритет */}
      <FilterSection label="Приоритет">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {PRIORITY_ORDER.map(p => {
            const selected = filters.priorities.includes(p);
            return (
              <Chip
                key={p} variant="toggle" large={mobile} minHeight={mobile ? 40 : undefined}
                selected={selected}
                onClick={() => onFilters({ ...filters, priorities: toggleIn(filters.priorities, p) })}
              >
                <span style={{ width: 7, height: 7, borderRadius: '50%', background: selected ? 'currentColor' : PRIORITY_COLOR[p], flexShrink: 0 }} />
                {PRIORITY_LABEL[p]}
              </Chip>
            );
          })}
        </div>
      </FilterSection>

      {/* Срок */}
      <FilterSection label="Срок">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
          {DUE_FILTER_OPTIONS.map(o => {
            const selected = filters.due.includes(o.key);
            return (
              <Chip
                key={o.key} variant="toggle" large={mobile} minHeight={mobile ? 40 : undefined}
                selected={selected}
                onClick={() => onFilters({ ...filters, due: toggleIn(filters.due, o.key) })}
              >
                <span style={{ width: 7, height: 7, borderRadius: '50%', background: selected ? 'currentColor' : o.dot, flexShrink: 0 }} />
                {o.label}
              </Chip>
            );
          })}
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
