// Вид «Неделя»: полоса дней недели (выбранный — тёмная пилюля, точки под днями
// с задачами) + таймлайн выбранного дня с блоками задач по времени.

import { useMemo, useState } from 'react';
import type { Project, Task } from '../../types';
import { C, FONT } from '../../lib/design';
import { addDaysIso, projectColor, todayIso } from '../../lib/tasks';
import { NavArrow } from './CalendarMonth';

interface Props {
  tasks: Task[];
  projectsById: Map<string, Project>;
  navDate: string;
  onNavigate: (iso: string) => void;
  onOpenTask: (task: Task) => void;
  isMobile?: boolean;
}

const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
const HOUR_START = 8;
const HOUR_END = 20;
const HOUR_H = 54;

// Понедельник недели, содержащей дату
function weekStart(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  const offset = (date.getDay() + 6) % 7;
  return addDaysIso(iso, -offset);
}

const MONTHS_GENITIVE = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];

// «8 – 14 июня» / «29 июня – 5 июля»
function weekTitle(startIso: string): string {
  const endIso = addDaysIso(startIso, 6);
  const [, sm, sd] = startIso.split('-').map(Number);
  const [, em, ed] = endIso.split('-').map(Number);
  return sm === em
    ? `${sd} – ${ed} ${MONTHS_GENITIVE[sm - 1]}`
    : `${sd} ${MONTHS_GENITIVE[sm - 1]} – ${ed} ${MONTHS_GENITIVE[em - 1]}`;
}

export function CalendarWeek({ tasks, projectsById, navDate, onNavigate, onOpenTask, isMobile }: Props) {
  const today = todayIso();
  const start = weekStart(navDate);
  const days = useMemo(() => Array.from({ length: 7 }, (_, i) => addDaysIso(start, i)), [start]);
  const [selectedDay, setSelectedDay] = useState(() => days.includes(today) ? today : days[0]);

  // При навигации по неделям держим выбранный день внутри видимой недели
  const effectiveDay = days.includes(selectedDay) ? selectedDay : days[0];

  const byDay = useMemo(() => {
    const map = new Map<string, Task[]>();
    for (const t of tasks) {
      if (!t.dueDate) continue;
      const list = map.get(t.dueDate) ?? [];
      list.push(t);
      map.set(t.dueDate, list);
    }
    return map;
  }, [tasks]);

  const dayTasks = (byDay.get(effectiveDay) ?? [])
    .slice()
    .sort((a, b) => (a.dueTime ?? '99:99').localeCompare(b.dueTime ?? '99:99'));
  const timed = dayTasks.filter(t => t.dueTime);
  const untimed = dayTasks.filter(t => !t.dueTime);

  const hours = Array.from({ length: HOUR_END - HOUR_START + 1 }, (_, i) => HOUR_START + i);

  const eventTop = (time: string): number => {
    const [h, m] = time.split(':').map(Number);
    return (Math.min(Math.max(h, HOUR_START), HOUR_END) - HOUR_START) * HOUR_H + (m / 60) * HOUR_H;
  };

  return (
    <div>
      {/* Заголовок недели + навигация + «к сегодня» */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, margin: isMobile ? '14px 0 12px' : '18px 0 14px' }}>
        <NavArrow dir={-1} onClick={() => onNavigate(addDaysIso(start, -7))} />
        <span style={{
          fontFamily: FONT.serif, fontSize: isMobile ? 19 : 23, fontWeight: 500, color: C.textHeading,
          flex: isMobile ? 1 : undefined, textAlign: isMobile ? 'center' : undefined,
        }}>
          {weekTitle(start)}
        </span>
        {!isMobile && (
          <button
            onClick={() => { onNavigate(today); setSelectedDay(today); }}
            title="К сегодняшнему дню"
            style={{
              width: 34, height: 34, padding: 0, cursor: 'pointer', flexShrink: 0,
              border: `1px solid ${C.border}`, borderRadius: '50%', background: C.bgWhite,
              color: C.textSecondary, display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}
          >
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M17 17L7 7M7 17V7h10" />
            </svg>
          </button>
        )}
        {!isMobile && <div style={{ flex: 1 }} />}
        <NavArrow dir={1} onClick={() => onNavigate(addDaysIso(start, 7))} />
      </div>

      {/* Полоса дней */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: isMobile ? 4 : 10, marginBottom: 8 }}>
        {days.map((iso, i) => {
          const selected = iso === effectiveDay;
          const hasTasks = (byDay.get(iso) ?? []).length > 0;
          const day = Number(iso.split('-')[2]);
          return (
            <button
              key={iso}
              onClick={() => setSelectedDay(iso)}
              style={{
                display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 3,
                padding: isMobile ? '8px 0 7px' : '10px 0 9px', border: 'none', cursor: 'pointer',
                background: selected ? C.textHeading : 'transparent',
                borderRadius: isMobile ? 12 : 14,
              }}
            >
              <span style={{
                fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 600,
                color: selected ? 'rgba(255,255,255,0.75)' : C.textMuted,
              }}>
                {WEEKDAYS[i]}
              </span>
              <span style={{
                fontFamily: FONT.sans, fontSize: 16, fontWeight: selected || iso === today ? 700 : 500,
                color: selected ? '#fff' : iso === today ? C.accent : C.textPrimary,
              }}>
                {day}
              </span>
              <span style={{
                width: 4, height: 4, borderRadius: '50%',
                background: hasTasks ? (selected ? '#fff' : C.accent) : 'transparent',
              }} />
            </button>
          );
        })}
      </div>

      {/* Задачи без времени */}
      {untimed.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 5, marginBottom: 10 }}>
          {untimed.map(t => {
            const color = projectColor(t.projectId);
            return (
              <div
                key={t.id}
                onClick={() => onOpenTask(t)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 8,
                  background: color.soft, borderRadius: 10, padding: '8px 12px 8px 9px', cursor: 'pointer',
                }}
              >
                <span style={{ width: 3, height: 18, borderRadius: 2, background: color.main, flexShrink: 0 }} />
                <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textPrimary }}>
                  {t.title}
                </span>
                <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textSecondary }}>
                  весь день · {projectsById.get(t.projectId)?.name ?? ''}
                </span>
              </div>
            );
          })}
        </div>
      )}

      {/* Таймлайн */}
      <div style={{ position: 'relative', height: (hours.length - 1) * HOUR_H, marginBottom: 32 }}>
        {hours.slice(0, -1).map((h, i) => (
          <div key={h} style={{ position: 'absolute', top: i * HOUR_H, left: 0, right: 0, height: HOUR_H, boxSizing: 'border-box' }}>
            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, height: '100%' }}>
              <span style={{
                fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, width: 42, flexShrink: 0,
                transform: 'translateY(-6px)', textAlign: 'right',
              }}>
                {String(h).padStart(2, '0')}:00
              </span>
              <div style={{ flex: 1, borderTop: `1px solid ${C.borderLight}` }} />
            </div>
          </div>
        ))}

        {/* Блоки задач */}
        {timed.map(t => {
          const color = projectColor(t.projectId);
          return (
            <div
              key={t.id}
              onClick={() => onOpenTask(t)}
              style={{
                position: 'absolute', top: eventTop(t.dueTime!), left: 56, right: 4,
                minHeight: 40, boxSizing: 'border-box',
                background: color.soft, borderRadius: 10,
                padding: '7px 12px 7px 9px', cursor: 'pointer',
                display: 'flex', alignItems: 'center', gap: 9,
              }}
            >
              <span style={{ width: 3, alignSelf: 'stretch', borderRadius: 2, background: color.main, flexShrink: 0 }} />
              <div style={{ minWidth: 0 }}>
                <div style={{
                  fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textPrimary,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  textDecoration: t.status === 'done' ? 'line-through' : 'none',
                }}>
                  {t.title}
                </div>
                <div style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textSecondary }}>
                  {t.dueTime} · {projectsById.get(t.projectId)?.name ?? ''}
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
