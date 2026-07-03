// Вид «Агенда»: группы по дням (Сегодня / Завтра / Сб / 18 июн …),
// строки: время (или «—»), цветная полоса проекта, название + проект, бейдж Claude.

import { useMemo } from 'react';
import type { Project, Task } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { NO_PROJECT_LABEL, daysFromToday, projectColor } from '../../lib/tasks';
import { AssigneeBadge } from './bits';

interface Props {
  tasks: Task[];
  projectsById: Map<string, Project>;
  onOpenTask: (task: Task) => void;
  isMobile?: boolean;
}

const WEEKDAY_SHORT = ['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб'];

function groupTitle(iso: string): string {
  const diff = daysFromToday(iso);
  if (diff === 0) return 'Сегодня';
  if (diff === 1) return 'Завтра';
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  if (diff > 1 && diff < 7) return WEEKDAY_SHORT[date.getDay()];
  return date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace('.', '');
}

// «Чт, 11 июня»
function groupSubtitle(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  const full = date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
  return `${WEEKDAY_SHORT[date.getDay()]}, ${full}`;
}

export function CalendarAgenda({ tasks, projectsById, onOpenTask, isMobile }: Props) {
  // Незавершённые просроченные — отдельной группой сверху, дальше дни с задачами по возрастанию
  const groups = useMemo(() => {
    const dated = tasks.filter(t => t.dueDate);
    const overdue = dated
      .filter(t => t.status !== 'done' && daysFromToday(t.dueDate!) < 0)
      .sort((a, b) => a.dueDate!.localeCompare(b.dueDate!));
    const upcoming = new Map<string, Task[]>();
    for (const t of dated) {
      if (daysFromToday(t.dueDate!) < 0) continue;
      const list = upcoming.get(t.dueDate!) ?? [];
      list.push(t);
      upcoming.set(t.dueDate!, list);
    }
    const days = [...upcoming.keys()].sort();
    for (const d of days)
      upcoming.get(d)!.sort((a, b) => (a.dueTime ?? '99:99').localeCompare(b.dueTime ?? '99:99'));
    return { overdue, days, upcoming };
  }, [tasks]);

  const row = (t: Task) => {
    const color = projectColor(t.projectId);
    const done = t.status === 'done';
    return (
      <div
        key={t.id}
        onClick={() => onOpenTask(t)}
        style={{
          display: 'flex', alignItems: 'center', gap: 12,
          background: C.bgWhite, border: `1px solid ${C.borderLight}`,
          borderRadius: 12, boxShadow: SHADOW.card,
          padding: '12px 14px', cursor: 'pointer',
        }}
      >
        <span style={{
          width: 40, flexShrink: 0, textAlign: 'center',
          fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
          color: t.dueTime ? C.textPrimary : C.textMuted,
        }}>
          {t.dueTime ?? '—'}
        </span>
        <span style={{ width: 3, alignSelf: 'stretch', borderRadius: 2, background: color.main, flexShrink: 0 }} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{
            fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700,
            color: done ? C.textMuted : C.textPrimary,
            textDecoration: done ? 'line-through' : 'none',
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {t.title}
          </div>
          <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, marginTop: 2 }}>
            {t.projectId ? projectsById.get(t.projectId)?.name ?? '' : NO_PROJECT_LABEL}
          </div>
        </div>
        {t.assignee === 'claude' && <AssigneeBadge assignee="claude" size={22} />}
      </div>
    );
  };

  const header = (title: string, subtitle: string, danger?: boolean) => (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, margin: '18px 0 9px' }}>
      <span style={{ fontFamily: FONT.serif, fontSize: isMobile ? 17 : 18, fontWeight: 700, color: danger ? C.danger : C.textHeading }}>
        {title}
      </span>
      <span style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted }}>{subtitle}</span>
    </div>
  );

  const empty = groups.overdue.length === 0 && groups.days.length === 0;

  return (
    <div style={{ maxWidth: isMobile ? undefined : 720, paddingBottom: 32 }}>
      {empty && (
        <div style={{ fontFamily: FONT.sans, fontSize: 13.5, color: C.textMuted, padding: '28px 0', textAlign: 'center' }}>
          Нет задач со сроком — добавьте первую кнопкой «Задача»
        </div>
      )}

      {groups.overdue.length > 0 && (
        <div>
          {header('Просрочено', '', true)}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {groups.overdue.map(row)}
          </div>
        </div>
      )}

      {groups.days.map(day => (
        <div key={day}>
          {header(groupTitle(day), groupSubtitle(day))}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {groups.upcoming.get(day)!.map(row)}
          </div>
        </div>
      ))}
    </div>
  );
}
