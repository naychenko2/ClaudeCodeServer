// Вид «Месяц»: десктоп/планшет — крупная сетка с чипами задач,
// мобила — компактная сетка с точками + список задач выбранного дня.

import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { Project, Task } from '../../types';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { NO_PROJECT_LABEL, projectColor, todayIso, toIsoDate } from '../../lib/tasks';
import { TaskCard } from './TaskCard';
import { RepeatIcon } from './bits';
import { useTaskHover } from './TaskHoverCard';

interface Props {
  tasks: Task[];
  projectsById: Map<string, Project>;
  navDate: string;                 // якорная дата YYYY-MM-DD (месяц берётся из неё)
  onNavigate: (iso: string) => void;
  onOpenTask: (task: Task) => void;
  // Быстрое создание задачи на день: даблклик/контекстное меню (десктоп), длинное нажатие (мобила)
  onQuickCreate?: (iso: string) => void;
  isMobile?: boolean;
}

const MONTHS = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

// 6 фиксированных недель, покрывающих месяц (как в макете — стабильная высота)
function monthCells(year: number, month: number): { iso: string; inMonth: boolean }[] {
  const first = new Date(year, month, 1);
  const offset = (first.getDay() + 6) % 7;   // Пн = 0
  const start = new Date(year, month, 1 - offset);
  return Array.from({ length: 42 }, (_, i) => {
    const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
    return { iso: toIsoDate(d), inMonth: d.getMonth() === month };
  });
}

// Круглая кнопка навигации ‹ ›
export function NavArrow({ dir, onClick }: { dir: -1 | 1; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      style={{
        width: 34, height: 34, padding: 0, cursor: 'pointer', flexShrink: 0,
        border: `1px solid ${C.border}`, borderRadius: '50%', background: C.bgWhite,
        color: C.textSecondary, display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
        {dir < 0 ? <path d="M15 18l-6-6 6-6" /> : <path d="M9 6l6 6-6 6" />}
      </svg>
    </button>
  );
}

function fullDayLabel(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
}

function taskCountLabel(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return `${n} задача`;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return `${n} задачи`;
  return `${n} задач`;
}

// Поповер всех задач дня («+N ещё» в ячейке месяца). Позиция фиксированная,
// от прямоугольника ячейки; при нехватке места снизу открывается над ячейкой.
function DayOverflowPopover({ iso, rect, tasks, onOpenTask, onClose }: {
  iso: string;
  rect: DOMRect;
  tasks: Task[];
  onOpenTask: (t: Task) => void;
  onClose: () => void;
}) {
  const [hovered, setHovered] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    const onDown = (e: PointerEvent) => {
      if (!(e.target as HTMLElement).closest('[data-day-popover]')) onClose();
    };
    // Скролл страницы уводит якорную ячейку — закрываем; скролл внутри поповера не считается
    const onScroll = (e: Event) => {
      if (!(e.target instanceof HTMLElement) || !e.target.closest('[data-day-popover]')) onClose();
    };
    window.addEventListener('keydown', onKey);
    document.addEventListener('pointerdown', onDown);
    window.addEventListener('resize', onClose);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      window.removeEventListener('keydown', onKey);
      document.removeEventListener('pointerdown', onDown);
      window.removeEventListener('resize', onClose);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [onClose]);

  const width = window.innerWidth < 1024 ? 260 : 280;
  const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12));
  const openUp = window.innerHeight - rect.bottom < 330;

  return createPortal(
    <div
      data-day-popover
      style={{
        position: 'fixed', left, width, zIndex: Z.dropdown, boxSizing: 'border-box',
        ...(openUp ? { top: rect.top - 6, transform: 'translateY(-100%)' } : { top: rect.bottom + 6 }),
        maxHeight: 320, overflowY: 'auto',
        background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.dropdown,
        padding: '10px 10px 8px',
      }}
    >
      {/* Шапка: дата + счётчик */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 8, padding: '0 4px' }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading }}>
          {fullDayLabel(iso)}
        </span>
        <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
          {taskCountLabel(tasks.length)}
        </span>
      </div>
      {tasks.map(t => {
        const color = projectColor(t.projectId);
        const done = t.status === 'done';
        return (
          <div
            key={t.id}
            onClick={() => { onOpenTask(t); onClose(); }}
            onMouseEnter={() => setHovered(t.id)}
            onMouseLeave={() => setHovered(prev => prev === t.id ? null : prev)}
            style={{
              display: 'flex', alignItems: 'center', gap: 7,
              padding: '6px 8px', borderRadius: R.md, cursor: 'pointer',
              background: hovered === t.id ? color.soft : 'transparent',
              transition: 'background 0.1s',
            }}
          >
            <span style={{ width: 3, height: 14, borderRadius: 2, background: color.main, flexShrink: 0 }} />
            <span style={{
              flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600,
              color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              textDecoration: done ? 'line-through' : 'none', opacity: done ? 0.6 : t.virtual ? 0.7 : 1,
            }}>
              {t.title}
            </span>
            {t.virtual && (
              <span title="Повтор" style={{ color: color.main, opacity: 0.75, display: 'flex', flexShrink: 0 }}>
                <RepeatIcon size={10} />
              </span>
            )}
            {t.dueTime && (
              <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
                {t.dueTime}
              </span>
            )}
          </div>
        );
      })}
    </div>,
    document.body,
  );
}

export function CalendarMonth({ tasks, projectsById, navDate, onNavigate, onOpenTask, onQuickCreate, isMobile }: Props) {
  const today = todayIso();
  const [year, month] = [Number(navDate.slice(0, 4)), Number(navDate.slice(5, 7)) - 1];
  const [selectedDay, setSelectedDay] = useState(today);
  // Раскрытый через «+N ещё» день (десктоп): дата + прямоугольник ячейки для позиционирования
  const [overflowDay, setOverflowDay] = useState<{ iso: string; rect: DOMRect } | null>(null);
  // Контекстное меню дня (десктоп): «+ Задача на …»
  const [ctxMenu, setCtxMenu] = useState<{ iso: string; x: number; y: number } | null>(null);
  // Длинное нажатие по дню (мобила): таймер + флаг, гасящий последующий click
  const longPress = useRef<{ timer: ReturnType<typeof setTimeout> | null; fired: boolean }>({ timer: null, fired: false });
  const hover = useTaskHover();
  const nameOf = (t: Task) =>
    t.projectId ? projectsById.get(t.projectId)?.name ?? '' : NO_PROJECT_LABEL;

  // Закрытие контекстного меню: клик мимо, Esc, скролл
  useEffect(() => {
    if (!ctxMenu) return;
    const close = () => setCtxMenu(null);
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') close(); };
    document.addEventListener('pointerdown', close);
    window.addEventListener('keydown', onKey);
    window.addEventListener('scroll', close, true);
    return () => {
      document.removeEventListener('pointerdown', close);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', close, true);
    };
  }, [ctxMenu]);

  const startLongPress = (iso: string) => {
    if (!onQuickCreate) return;
    longPress.current.fired = false;
    longPress.current.timer = setTimeout(() => {
      longPress.current.fired = true;
      if ('vibrate' in navigator) navigator.vibrate?.(15);
      onQuickCreate(iso);
    }, 550);
  };

  const cancelLongPress = () => {
    if (longPress.current.timer) { clearTimeout(longPress.current.timer); longPress.current.timer = null; }
  };

  const cells = useMemo(() => monthCells(year, month), [year, month]);

  const byDay = useMemo(() => {
    const map = new Map<string, Task[]>();
    for (const t of tasks) {
      if (!t.dueDate) continue;
      const list = map.get(t.dueDate) ?? [];
      list.push(t);
      map.set(t.dueDate, list);
    }
    for (const list of map.values())
      list.sort((a, b) => (a.dueTime ?? '99:99').localeCompare(b.dueTime ?? '99:99'));
    return map;
  }, [tasks]);

  const shiftMonth = (dir: -1 | 1) => {
    const d = new Date(year, month + dir, 1);
    setOverflowDay(null);
    onNavigate(toIsoDate(d));
  };

  const monthTitle = `${MONTHS[month]} ${year}`;

  // === Мобила: компактная сетка с точками + список задач выбранного дня ===
  if (isMobile) {
    const dayTasks = byDay.get(selectedDay) ?? [];
    return (
      <div>
        {/* Заголовок месяца + навигация */}
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', margin: '14px 0 10px' }}>
          <span style={{ fontFamily: FONT.serif, fontSize: 19, fontWeight: 500, color: C.textHeading }}>{monthTitle}</span>
          <div style={{ display: 'flex', gap: 8 }}>
            <NavArrow dir={-1} onClick={() => shiftMonth(-1)} />
            <NavArrow dir={1} onClick={() => shiftMonth(1)} />
          </div>
        </div>

        {/* Дни недели */}
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', marginBottom: 2 }}>
          {WEEKDAYS.map(w => (
            <div key={w} style={{ textAlign: 'center', fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 600, color: C.textMuted, padding: '4px 0' }}>
              {w}
            </div>
          ))}
        </div>

        {/* Сетка: число + точки задач */}
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', rowGap: 4 }}>
          {cells.map(({ iso, inMonth }) => {
            const dayTasksHere = byDay.get(iso) ?? [];
            const selected = iso === selectedDay;
            const day = Number(iso.split('-')[2]);
            return (
              <button
                key={iso}
                // Тап — выбрать день; длинное нажатие — новая задача на этот день
                onClick={() => {
                  if (longPress.current.fired) { longPress.current.fired = false; return; }
                  setSelectedDay(iso);
                }}
                onPointerDown={() => startLongPress(iso)}
                onPointerUp={cancelLongPress}
                onPointerLeave={cancelLongPress}
                onPointerCancel={cancelLongPress}
                onContextMenu={e => e.preventDefault()}
                style={{
                  height: 46, padding: 0, border: 'none', cursor: 'pointer',
                  background: selected ? C.textHeading : 'transparent',
                  borderRadius: 12,
                  display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 3,
                  WebkitTouchCallout: 'none', WebkitUserSelect: 'none', userSelect: 'none',
                  touchAction: 'pan-y',
                }}
              >
                <span style={{
                  fontFamily: FONT.sans, fontSize: 14, fontWeight: selected || iso === today ? 700 : 400,
                  color: selected ? C.bgCard : !inMonth ? C.textMuted + '80' : iso === today ? C.accent : C.textPrimary,
                }}>
                  {day}
                </span>
                {/* Точки задач (до 3), у выбранного дня — белые */}
                {dayTasksHere.length > 0 && (
                  <span style={{ display: 'flex', gap: 3, height: 4 }}>
                    {dayTasksHere.slice(0, 3).map((t, i) => (
                      <span key={i} style={{
                        width: 4, height: 4, borderRadius: '50%',
                        background: selected ? C.bgCard : projectColor(t.projectId).main,
                      }} />
                    ))}
                  </span>
                )}
              </button>
            );
          })}
        </div>

        {/* Список задач выбранного дня */}
        <div style={{ marginTop: 20 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 10 }}>
            <span style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 500, color: C.textHeading }}>
              {selectedDay === today ? 'Сегодня' : ''}{selectedDay === today ? ' · ' : ''}{fullDayLabel(selectedDay)}
            </span>
            {dayTasks.length > 0 && (
              <span style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted }}>{taskCountLabel(dayTasks.length)}</span>
            )}
          </div>
          {dayTasks.length === 0 ? (
            <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, padding: '10px 0 20px' }}>
              Нет задач на этот день
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8, paddingBottom: 20 }}>
              {dayTasks.map(t => (
                <TaskCard
                  key={t.id}
                  task={t}
                  onClick={() => onOpenTask(t)}
                  projectName={t.projectId ? projectsById.get(t.projectId)?.name : NO_PROJECT_LABEL}
                />
              ))}
            </div>
          )}
        </div>
      </div>
    );
  }

  // === Десктоп/планшет: крупная сетка с чипами ===
  const MAX_CHIPS = 3;
  return (
    <div>
      {/* Заголовок месяца + навигация */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', margin: '18px 0 14px' }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 23, fontWeight: 500, color: C.textHeading }}>{monthTitle}</span>
        <div style={{ display: 'flex', gap: 9 }}>
          <NavArrow dir={-1} onClick={() => shiftMonth(-1)} />
          <NavArrow dir={1} onClick={() => shiftMonth(1)} />
        </div>
      </div>

      {/* Дни недели */}
      {/* minmax(0,1fr): колонки не распираются min-content'ом чипов — равная ширина без скролла */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: 8, marginBottom: 6 }}>
        {WEEKDAYS.map(w => (
          <div key={w} style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, padding: '0 2px' }}>
            {w}
          </div>
        ))}
      </div>

      {/* Сетка */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: 8, paddingBottom: 28 }}>
        {cells.map(({ iso, inMonth }) => {
          const dayTasksHere = byDay.get(iso) ?? [];
          const isToday = iso === today;
          const day = Number(iso.split('-')[2]);
          const overflow = dayTasksHere.length - MAX_CHIPS;
          return (
            <div
              key={iso}
              data-day-cell
              // Даблклик по ячейке — новая задача на день; правый клик — контекстное меню
              onDoubleClick={onQuickCreate ? () => onQuickCreate(iso) : undefined}
              onContextMenu={onQuickCreate ? e => {
                e.preventDefault();
                setCtxMenu({ iso, x: e.clientX, y: e.clientY });
              } : undefined}
              style={{
                minHeight: 86, minWidth: 0, overflow: 'hidden', boxSizing: 'border-box', padding: '7px 8px',
                background: isToday ? C.accentLight : inMonth ? C.bgWhite : 'transparent',
                border: `1px solid ${isToday ? C.accentMuted : inMonth ? C.borderLight : C.borderLight + '90'}`,
                borderRadius: 10,
                opacity: inMonth ? 1 : 0.55,
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 5 }}>
                <span style={{
                  fontFamily: FONT.sans, fontSize: 12, fontWeight: isToday ? 700 : 500,
                  color: inMonth ? C.textPrimary : C.textMuted,
                }}>
                  {day}
                </span>
                {isToday && (
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 9.5, fontWeight: 700, color: C.onAccent,
                    background: C.accent, borderRadius: 999, padding: '2px 7px',
                    textTransform: 'lowercase', letterSpacing: '0.02em',
                  }}>
                    сегодня
                  </span>
                )}
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
                {dayTasksHere.slice(0, MAX_CHIPS).map(t => {
                  const color = projectColor(t.projectId);
                  return (
                    <div
                      key={t.id}
                      onClick={() => onOpenTask(t)}
                      {...hover.bind(t, nameOf(t))}
                      style={{
                        display: 'flex', alignItems: 'center', gap: 5,
                        background: color.soft, borderRadius: 6,
                        padding: '3px 6px 3px 4px', cursor: 'pointer',
                      }}
                    >
                      <span style={{ width: 3, height: 12, borderRadius: 2, background: color.main, flexShrink: 0 }} />
                      <span style={{
                        flex: 1, minWidth: 0,
                        fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 600, color: C.textPrimary,
                        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                        textDecoration: t.status === 'done' ? 'line-through' : 'none',
                        opacity: t.status === 'done' ? 0.6 : t.virtual ? 0.7 : 1,
                      }}>
                        {t.title}
                      </span>
                      {t.virtual && (
                        <span title="Повтор" style={{ color: color.main, opacity: 0.75, display: 'flex', flexShrink: 0 }}>
                          <RepeatIcon size={9} />
                        </span>
                      )}
                    </div>
                  );
                })}
                {overflow > 0 && (
                  <button
                    onClick={e => {
                      const cell = (e.currentTarget as HTMLElement).closest('[data-day-cell]');
                      if (cell) setOverflowDay({ iso, rect: cell.getBoundingClientRect() });
                    }}
                    onMouseEnter={e => {
                      e.currentTarget.style.color = C.textPrimary;
                      e.currentTarget.style.textDecoration = 'underline';
                    }}
                    onMouseLeave={e => {
                      e.currentTarget.style.color = C.textMuted;
                      e.currentTarget.style.textDecoration = 'none';
                    }}
                    style={{
                      alignSelf: 'flex-start', border: 'none', background: 'none', cursor: 'pointer',
                      fontFamily: FONT.sans, fontSize: 10, color: C.textMuted,
                      padding: '2px 4px', margin: '-2px 0',
                    }}
                  >
                    +{overflow} ещё
                  </button>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {overflowDay && (
        <DayOverflowPopover
          iso={overflowDay.iso}
          rect={overflowDay.rect}
          tasks={byDay.get(overflowDay.iso) ?? []}
          onOpenTask={onOpenTask}
          onClose={() => setOverflowDay(null)}
        />
      )}

      {/* Контекстное меню дня: «+ Задача на …» */}
      {ctxMenu && (
        <div
          onPointerDown={e => e.stopPropagation()}
          style={{
            position: 'fixed', zIndex: Z.dropdown,
            left: Math.min(ctxMenu.x, window.innerWidth - 240),
            top: Math.min(ctxMenu.y, window.innerHeight - 60),
            background: C.bgWhite, border: `1px solid ${C.border}`,
            borderRadius: R.lg, boxShadow: SHADOW.dropdown, padding: 4,
          }}
        >
          <button
            onClick={() => { onQuickCreate?.(ctxMenu.iso); setCtxMenu(null); }}
            onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
            style={{
              display: 'flex', alignItems: 'center', gap: 8,
              padding: '8px 14px', border: 'none', borderRadius: R.md,
              background: 'transparent', cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textPrimary,
              whiteSpace: 'nowrap',
            }}
          >
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="2.5" strokeLinecap="round">
              <line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" />
            </svg>
            Задача на {fullDayLabel(ctxMenu.iso)}
          </button>
        </div>
      )}

      {hover.popover}
    </div>
  );
}
