// Богатый hover-тултип задачи (десктоп): детальная карточка с управлением —
// смена статуса и отметка подзадач прямо из тултипа, не уходя со списка.
//
// Использование: const hover = useTaskHover();
//   <div {...hover.bind(task, projectName)}>…</div>  +  {hover.popover}

import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { Task, TaskStatus } from '../../types';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import {
  PRIORITY_LABEL, STATUS_LABEL, updateTask, useTasks,
} from '../../lib/tasks';
import { AssigneeBadge, DueChip, LabelChip, PriorityFlag, SubtaskCheck } from './bits';

const OPEN_DELAY = 400;
const CLOSE_DELAY = 180;
const WIDTH = 340;
const STATUSES: TaskStatus[] = ['todo', 'inProgress', 'done'];

interface Anchor {
  taskId: string;
  rect: DOMRect;
  projectName?: string;
}

// Сам тултип: живая задача из стора (обновления подзадач/статуса видны сразу)
function HoverCard({ anchor, onKeepAlive, onLeave, onClose }: {
  anchor: Anchor;
  onKeepAlive: () => void;
  onLeave: () => void;
  onClose: () => void;
}) {
  const tasks = useTasks();
  const task = tasks.find(t => t.id === anchor.taskId) ?? null;
  const ref = useRef<HTMLDivElement>(null);
  const [top, setTop] = useState(anchor.rect.top);

  // Вертикальный клэмп по фактической высоте после рендера
  useLayoutEffect(() => {
    const h = ref.current?.offsetHeight ?? 0;
    setTop(Math.max(12, Math.min(anchor.rect.top, window.innerHeight - h - 12)));
  }, [anchor, task?.subtasks.length, task?.description]);

  // Скролл вне тултипа уводит якорь — закрываем
  useEffect(() => {
    const onScroll = (e: Event) => {
      if (!(e.target instanceof HTMLElement) || !ref.current?.contains(e.target)) onClose();
    };
    window.addEventListener('scroll', onScroll, true);
    return () => window.removeEventListener('scroll', onScroll, true);
  }, [onClose]);

  if (!task) return null;

  // Справа от якоря, если не влезает — слева
  const fitsRight = anchor.rect.right + WIDTH + 20 <= window.innerWidth;
  const left = fitsRight
    ? anchor.rect.right + 10
    : Math.max(12, anchor.rect.left - WIDTH - 10);

  const done = task.status === 'done';
  const doneSubs = task.subtasks.filter(s => s.isDone).length;

  return createPortal(
    <div
      ref={ref}
      onMouseEnter={onKeepAlive}
      onMouseLeave={onLeave}
      // Портал баблит по React-дереву — не даём кликам провалиться в якорную карточку
      onClick={e => e.stopPropagation()}
      style={{
        position: 'fixed', left, top, width: WIDTH, boxSizing: 'border-box',
        maxHeight: 'min(480px, calc(100vh - 24px))', overflowY: 'auto',
        background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.dropdown,
        padding: '14px 16px 12px', zIndex: Z.dropdown,
      }}
    >
      {/* Проект */}
      {anchor.projectName && (
        <div style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, marginBottom: 6 }}>
          {anchor.projectName}
        </div>
      )}

      {/* Заголовок */}
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 7, marginBottom: 9 }}>
        <span style={{ marginTop: 3, display: 'flex' }}><PriorityFlag priority={task.priority} /></span>
        <span style={{
          flex: 1, fontFamily: FONT.sans, fontSize: 14.5, fontWeight: 700, lineHeight: 1.35,
          color: done ? C.textMuted : C.textHeading,
          textDecoration: done ? 'line-through' : 'none',
        }}>
          {task.title}
        </span>
        <AssigneeBadge assignee={task.assignee} />
      </div>

      {/* Чипы: приоритет, срок, метки */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 11 }}>
        <LabelChip label={PRIORITY_LABEL[task.priority]} fontSize={11} />
        <DueChip task={task} withTime />
        {task.labels.map(l => <LabelChip key={l} label={l} />)}
      </div>

      {/* Статус — сегмент, кликабельный */}
      <div style={{ display: 'flex', gap: 3, background: C.bgSelected, borderRadius: R.lg, padding: 3, marginBottom: 11 }}>
        {STATUSES.map(s => {
          const active = task.status === s;
          return (
            <button
              key={s}
              onClick={e => { e.stopPropagation(); void updateTask(task.id, { status: s }); }}
              style={{
                flex: 1, padding: '7px 2px', border: 'none', cursor: 'pointer',
                borderRadius: R.lg - 2,
                background: active ? C.accent : 'transparent',
                color: active ? C.onAccent : C.textSecondary,
                fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 600, whiteSpace: 'nowrap',
                transition: 'background 0.15s, color 0.15s',
              }}
            >
              {STATUS_LABEL[s]}
            </button>
          );
        })}
      </div>

      {/* Описание */}
      {task.description && (
        <div style={{
          maxHeight: 150, overflowY: 'auto', marginBottom: 11,
          padding: '9px 11px', background: C.bgCard,
          border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
          fontSize: 12.5,
        }}>
          <MarkdownViewer content={task.description} />
        </div>
      )}

      {/* Подзадачи — кликабельные */}
      {task.subtasks.length > 0 && (
        <div style={{ marginBottom: 4 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
            <span style={{
              fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
              textTransform: 'uppercase', letterSpacing: '0.06em',
            }}>
              Подзадачи
            </span>
            <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>
              {doneSubs}/{task.subtasks.length}
            </span>
            <div style={{ flex: 1, height: 4, background: C.bgSelected, borderRadius: 2, overflow: 'hidden' }}>
              <div style={{ height: '100%', width: `${(doneSubs / task.subtasks.length) * 100}%`, background: C.success, transition: 'width 0.2s' }} />
            </div>
          </div>
          {task.subtasks.map(s => (
            <div
              key={s.id}
              onClick={e => {
                e.stopPropagation();
                void updateTask(task.id, {
                  subtasks: task.subtasks.map(x => x.id === s.id ? { ...x, isDone: !x.isDone } : x),
                });
              }}
              style={{
                display: 'flex', alignItems: 'center', gap: 9,
                padding: '5px 4px', borderRadius: R.md, cursor: 'pointer',
              }}
              onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
            >
              <SubtaskCheck done={s.isDone} size={17} />
              <span style={{
                fontFamily: FONT.sans, fontSize: 13, color: s.isDone ? C.textMuted : C.textPrimary,
                textDecoration: s.isDone ? 'line-through' : 'none',
              }}>
                {s.title}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>,
    document.body,
  );
}

// Хук: bind(task, projectName?) → onMouseEnter/onMouseLeave для якоря; popover — рендерить рядом
export function useTaskHover() {
  const [anchor, setAnchor] = useState<Anchor | null>(null);
  const openTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (openTimer.current) clearTimeout(openTimer.current);
    if (closeTimer.current) clearTimeout(closeTimer.current);
  }, []);

  const cancelClose = () => {
    if (closeTimer.current) { clearTimeout(closeTimer.current); closeTimer.current = null; }
  };

  const scheduleClose = () => {
    cancelClose();
    closeTimer.current = setTimeout(() => setAnchor(null), CLOSE_DELAY);
  };

  const bind = (task: Task, projectName?: string) => ({
    onMouseEnter: (e: React.MouseEvent<HTMLElement>) => {
      // Только устройства с настоящим hover (не тач)
      if (!window.matchMedia('(hover: hover)').matches) return;
      cancelClose();
      const rect = e.currentTarget.getBoundingClientRect();
      if (openTimer.current) clearTimeout(openTimer.current);
      openTimer.current = setTimeout(() => setAnchor({ taskId: task.id, rect, projectName }), OPEN_DELAY);
    },
    onMouseLeave: () => {
      if (openTimer.current) { clearTimeout(openTimer.current); openTimer.current = null; }
      scheduleClose();
    },
  });

  const popover = anchor ? (
    <HoverCard
      anchor={anchor}
      onKeepAlive={cancelClose}
      onLeave={scheduleClose}
      onClose={() => setAnchor(null)}
    />
  ) : null;

  return { bind, popover };
}
