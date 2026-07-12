// Мелкие общие элементы задач: флаг приоритета, бейджи, чипы, лейблы секций

import type { ReactNode } from 'react';
import {
  Calendar, Repeat, LayoutGrid, List, CalendarDays, CalendarRange, Rows3, CalendarCheck,
} from 'lucide-react';
import type { Task, TaskAssignee, TaskPriority } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { PRIORITY_COLOR, PRIORITY_FILL, dueLabel, isDueUrgent } from '../../lib/tasks';

// Флаг приоритета (срочный — залитый)
export function PriorityFlag({ priority, size = 13 }: { priority: TaskPriority; size?: number }) {
  const color = PRIORITY_COLOR[priority];
  const fill = PRIORITY_FILL[priority];
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill={fill ? color : 'none'} stroke={color}
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
      <path d="M4 21V4M4 4h12l-2.5 4L16 12H4" />
    </svg>
  );
}

// Иконка-аватар исполнителя Claude — брендовая иконка приложения
export function ClaudeBadge({ size = 20 }: { size?: number }) {
  return (
    <img
      src="/favicon.svg"
      alt=""
      title="Claude"
      width={size}
      height={size}
      style={{ display: 'block', flexShrink: 0 }}
    />
  );
}

// Кружок «Я»
export function MeBadge({ size = 20 }: { size?: number }) {
  return (
    <div title="Я" style={{
      width: size, height: size, borderRadius: '50%', flexShrink: 0,
      background: C.bgPanel, border: `1px solid ${C.border}`, boxSizing: 'border-box',
      color: C.textSecondary, fontSize: size * 0.48, fontWeight: 700, fontFamily: FONT.sans,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>
      я
    </div>
  );
}

export function AssigneeBadge({ assignee, size = 20 }: { assignee?: TaskAssignee; size?: number }) {
  if (assignee === 'claude') return <ClaudeBadge size={size} />;
  if (assignee === 'me') return <MeBadge size={size} />;
  return null;
}

// Иконки видов календаря и группировок списка задач (lucide-react, strokeWidth=2)
export function MonthIcon({ size = 14 }: { size?: number }) {
  return <CalendarDays size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

export function WeekIcon({ size = 14 }: { size?: number }) {
  return <CalendarRange size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

export function AgendaIcon({ size = 14 }: { size?: number }) {
  return <Rows3 size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

// Иконка вида «Доска» (Kanban) — сетка колонок
export function BoardIcon({ size = 14 }: { size?: number }) {
  return <LayoutGrid size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

export function ListIcon({ size = 14 }: { size?: number }) {
  return <List size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

export function ByDateIcon({ size = 14 }: { size?: number }) {
  return <CalendarCheck size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

// Мобильный переключатель видов: сегменты «иконка сверху, подпись снизу»,
// активный — подложка accentLight (как Месяц/Неделя/Агенда в календаре)
export function IconViewSwitcher<T extends string>({ value, options, onChange }: {
  value: T;
  options: { value: T; label: string; icon: ReactNode }[];
  onChange: (v: T) => void;
}) {
  return (
    <div style={{ display: 'flex', gap: 8 }}>
      {options.map(opt => {
        const active = value === opt.value;
        return (
          <button
            key={opt.value}
            onClick={() => onChange(opt.value)}
            style={{
              flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5,
              padding: '11px 0 9px', cursor: 'pointer',
              border: 'none', borderRadius: R.xl,
              background: active ? C.accentLight : 'transparent',
              color: active ? C.accent : C.textSecondary,
              fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
              transition: 'background 0.15s, color 0.15s',
            }}
          >
            {opt.icon}
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}

// Иконка повтора — маркер виртуального (вычисленного) экземпляра регулярной задачи в календаре
export function RepeatIcon({ size = 11 }: { size?: number }) {
  return <Repeat size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

// Иконка календаря для чипов срока
export function CalendarIcon({ size = 11 }: { size?: number }) {
  return <Calendar size={size} strokeWidth={2} style={{ flexShrink: 0 }} />;
}

// Чип срока: «Сегодня» / «Пн» / «18 июн» (+ время). Горящий срок — красным.
export function DueChip({ task, withTime, fontSize = 11 }: { task: Task; withTime?: boolean; fontSize?: number }) {
  if (!task.dueDate) return null;
  const urgent = isDueUrgent(task);
  const label = withTime && task.dueTime
    ? `${dueLabel(task.dueDate)} · ${task.dueTime}`
    : dueLabel(task.dueDate);
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      fontFamily: FONT.sans, fontSize, fontWeight: 600,
      color: urgent ? C.danger : C.textSecondary,
      background: urgent ? C.dangerBg : C.bgSelected,
      border: `1px solid ${urgent ? C.dangerBorder : 'transparent'}`,
      padding: '3px 8px', borderRadius: R.sm + 1, whiteSpace: 'nowrap',
    }}>
      <CalendarIcon size={fontSize} />
      {label}
    </span>
  );
}

// Серый чип метки
export function LabelChip({ label, fontSize = 10.5 }: { label: string; fontSize?: number }) {
  return (
    <span style={{
      fontFamily: FONT.sans, fontSize, color: C.textSecondary,
      background: C.bgSelected, padding: '3px 8px', borderRadius: R.sm, whiteSpace: 'nowrap',
    }}>
      {label}
    </span>
  );
}

// Uppercase-заголовок секции карточки задачи («ЧТО НУЖНО СДЕЛАТЬ», «ПОДЗАДАЧИ»…)
export function SectionLabel({ children, style }: { children: ReactNode; style?: React.CSSProperties }) {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 700, color: C.textMuted,
      textTransform: 'uppercase', letterSpacing: '0.06em', ...style,
    }}>
      {children}
    </div>
  );
}

// Круглый чекбокс-квадратик подзадачи (зелёный с галкой когда готово)
export function SubtaskCheck({ done, size = 20 }: { done: boolean; size?: number }) {
  return (
    <div style={{
      width: size, height: size, borderRadius: 6, flexShrink: 0, boxSizing: 'border-box',
      background: done ? C.success : C.bgWhite,
      border: done ? 'none' : `1.5px solid ${C.border}`,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      transition: 'background 0.12s',
    }}>
      {done && (
        <svg width={size * 0.6} height={size * 0.6} viewBox="0 0 24 24" fill="none" stroke="#fff"
          strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="20 6 9 17 4 12" />
        </svg>
      )}
    </div>
  );
}

// Бейдж расширения файла (для секции «Файлы»)
const EXT_COLORS: Record<string, string> = {
  ts: '#3178C6', tsx: '#3178C6', js: '#F0A500', jsx: '#F0A500',
  cs: '#512BD4', md: '#4A9A5C', css: '#2965F1', json: '#8B6D4E', py: '#3776AB',
};

export function ExtBadge({ filename, size = 24 }: { filename: string; size?: number }) {
  const ext = filename.split('.').pop()?.toLowerCase() ?? '';
  const color = EXT_COLORS[ext] ?? C.textMuted;
  return (
    <div style={{
      width: size, height: size, borderRadius: 6, flexShrink: 0,
      background: color + '18', color,
      fontFamily: FONT.mono, fontSize: size * 0.32, fontWeight: 700,
      display: 'flex', alignItems: 'center', justifyContent: 'center', lineHeight: 1,
    }}>
      {ext.slice(0, 3) || '?'}
    </div>
  );
}
