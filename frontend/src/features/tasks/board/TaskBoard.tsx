// Вид «Доска» (Kanban): произвольные колонки (кастомные проекта или дефолтные 3),
// drag & drop (смена колонки/категории + ручной порядок), дорожки (swimlanes),
// фильтры (общий стор boardControls), быстрое добавление и WIP-лимиты. Данные — из
// стора задач (реальные, без виртуальных повторов). Тулбар: инлайн (хаб/мобайл) или
// в сайдбаре (десктоп-проект, тогда inlineToolbar=false — тулбар рендерит WorkspacePage).

import { useEffect, useMemo, useState } from 'react';
import {
  DndContext, DragOverlay, MouseSensor, TouchSensor, useSensor, useSensors,
  closestCorners, type DragEndEvent, type DragStartEvent,
} from '@dnd-kit/core';
import type { BoardColumn as BoardColumnType, Project, Task, TaskAssignee, TaskPriority, UpdateTaskDto } from '../../../types';
import { C, FONT } from '../../../lib/design';
import {
  boardCardSort, boardLanes, columnColor, computeOrder, createTask, reloadTasks,
  taskColumnKey, updateTask, upsertTaskLocal, type BoardGroupBy,
} from '../../../lib/tasks';
import { useBoardControls, setGroupBy } from '../../../lib/boardControls';
import { TaskCard } from '../TaskCard';
import { BoardCell, ColumnHeader } from './BoardColumn';
import { BoardToolbar } from './BoardToolbar';

interface Props {
  tasks: Task[];                              // реальные задачи (уже отфильтрованы по группе проектов)
  columns: BoardColumnType[];                 // колонки доски (проектные кастомные или дефолтные 3)
  projectsById: Map<string, Project>;
  onOpenTask: (task: Task) => void;
  isMobile: boolean;
  // Проект для быстрого добавления карточки (null = личная задача). Хаб — null, проект — его id.
  quickAddProjectId?: string | null;
  // 'project' — доска внутри проекта: пишем columnId, убираем группировку «по проекту»
  scope?: 'hub' | 'project';
  inlineToolbar?: boolean;                    // рендерить тулбар над сеткой (иначе он в сайдбаре)
  onEditColumns?: () => void;                 // открыть редактор колонок (проектная доска)
}

export function TaskBoard({
  tasks, columns, projectsById, onOpenTask,
  quickAddProjectId = null, scope = 'hub', inlineToolbar = true, onEditColumns,
}: Props) {
  const { groupBy, search, priorities, assignee, wip } = useBoardControls();
  const [activeId, setActiveId] = useState<string | null>(null);

  // В проекте группировка «по проекту» бессмысленна — сбрасываем на «без дорожек»
  const groupOptions: BoardGroupBy[] = scope === 'project'
    ? ['none', 'priority', 'assignee', 'due']
    : ['none', 'priority', 'assignee', 'project', 'due'];
  useEffect(() => { if (scope === 'project' && groupBy === 'project') setGroupBy('none'); }, [scope, groupBy]);

  const sensors = useSensors(
    useSensor(MouseSensor, { activationConstraint: { distance: 6 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 180, tolerance: 6 } }),
  );

  const projectNameOf = (t: Task) => (t.projectId ? projectsById.get(t.projectId)?.name : undefined);
  const columnById = useMemo(() => new Map(columns.map(c => [c.id, c])), [columns]);

  // Фильтрация (клиентская, по стору)
  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return tasks.filter(t => {
      if (t.virtual) return false;
      if (priorities.length && !priorities.includes(t.priority)) return false;
      if (assignee !== 'all' && t.assignee !== assignee) return false;
      if (q) {
        const hay = `${t.title} ${t.description} ${t.labels.join(' ')}`.toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    });
  }, [tasks, search, priorities, assignee]);

  const lanes = useMemo(() => boardLanes(filtered, groupBy, projectsById), [filtered, groupBy, projectsById]);

  // Структуры доски: ячейка(lane::column)→карточки, карточка→ячейка, тоталы по колонкам
  const { cellCards, cellByCard, columnTotals, taskById } = useMemo(() => {
    const cellCards = new Map<string, Task[]>();
    const cellByCard = new Map<string, { laneKey: string; columnId: string }>();
    const taskById = new Map<string, Task>();
    const columnTotals: Record<string, number> = {};
    columns.forEach(c => { columnTotals[c.id] = 0; });
    filtered.forEach(t => { taskById.set(t.id, t); columnTotals[taskColumnKey(t, columns)]++; });
    lanes.forEach(lane => {
      const byCol = new Map<string, Task[]>();
      lane.tasks.forEach(t => {
        const key = taskColumnKey(t, columns);
        (byCol.get(key) ?? byCol.set(key, []).get(key)!).push(t);
      });
      columns.forEach(col => {
        const cards = (byCol.get(col.id) ?? []).sort(boardCardSort);
        cellCards.set(`${lane.key}::${col.id}`, cards);
        cards.forEach(c => cellByCard.set(c.id, { laneKey: lane.key, columnId: col.id }));
      });
    });
    return { cellCards, cellByCard, columnTotals, taskById };
  }, [lanes, filtered, columns]);

  const onDragStart = (e: DragStartEvent) => setActiveId(e.active.id as string);

  const onDragEnd = (e: DragEndEvent) => {
    setActiveId(null);
    const { active, over } = e;
    if (!over || over.id === active.id) return;
    const activeTask = taskById.get(active.id as string);
    if (!activeTask) return;

    // Целевая ячейка: over — droppable ячейки (lane::columnId) или карточка
    const overId = over.id as string;
    let destLaneKey: string, destColId: string, overCardId: string | undefined;
    if (overId.includes('::')) {
      const [lk, cid] = overId.split('::');
      destLaneKey = lk; destColId = cid; overCardId = undefined;
    } else {
      const cell = cellByCard.get(overId);
      if (!cell) return;
      destLaneKey = cell.laneKey; destColId = cell.columnId; overCardId = overId;
    }
    const destCol = columnById.get(destColId);
    if (!destCol) return;

    const sourceLaneKey = cellByCard.get(active.id as string)?.laneKey;
    const destCards = (cellCards.get(`${destLaneKey}::${destColId}`) ?? []).filter(c => c.id !== active.id);
    let idx = overCardId ? destCards.findIndex(c => c.id === overCardId) : destCards.length;
    if (idx < 0) idx = destCards.length;
    const order = computeOrder(destCards[idx - 1]?.order, destCards[idx]?.order);

    const dto: UpdateTaskDto = { order };
    // Категория колонки становится статусом; проектная доска фиксирует конкретную колонку
    if (destCol.category !== activeTask.status) dto.status = destCol.category;
    if (scope === 'project') dto.columnId = destColId;
    // Перенос между дорожками меняет поле — только priority/assignee (drag-to-change)
    if (sourceLaneKey && destLaneKey !== sourceLaneKey) {
      if (groupBy === 'priority') dto.priority = destLaneKey as TaskPriority;
      else if (groupBy === 'assignee' && destLaneKey !== 'none') dto.assignee = destLaneKey as TaskAssignee;
    }

    // Оптимистично двигаем карточку в сторе сразу — иначе оверлей «отлетает» назад
    const optimistic: Task = { ...activeTask, order };
    if (dto.status) optimistic.status = dto.status;
    if (scope === 'project') optimistic.columnId = destColId;
    if (dto.priority) optimistic.priority = dto.priority;
    if (dto.assignee) optimistic.assignee = dto.assignee;
    upsertTaskLocal(optimistic);

    void updateTask(activeTask.id, dto).catch(() => { void reloadTasks(); });
  };

  const grouped = groupBy !== 'none';
  const minEmptyHeight = grouped ? 76 : 260;
  const activeTask = activeId ? taskById.get(activeId) : null;
  const gridMinWidth = Math.max(columns.length * 296, 296);

  return (
    <DndContext sensors={sensors} collisionDetection={closestCorners} onDragStart={onDragStart} onDragEnd={onDragEnd}>
      {inlineToolbar && <BoardToolbar layout="inline" groupOptions={groupOptions} onEditColumns={onEditColumns} />}

      {/* Скролл-контейнер доски */}
      <div style={{ overflowX: 'auto', paddingBottom: 24 }} className="cc-hide-scrollbar">
        <div style={{
          display: 'grid', gridTemplateColumns: `repeat(${columns.length}, minmax(280px, 1fr))`,
          gap: 14, width: '100%', minWidth: gridMinWidth, alignItems: 'start',
        }}>
          {/* Заголовки колонок */}
          {columns.map(col => (
            <ColumnHeader
              key={`h-${col.id}`}
              name={col.name}
              color={columnColor(col)}
              count={columnTotals[col.id] ?? 0}
              wip={wip[col.id]}
              over={!!wip[col.id] && (columnTotals[col.id] ?? 0) > wip[col.id]}
              columnId={col.id}
            />
          ))}

          {/* Дорожки */}
          {lanes.map(lane => (
            <div key={lane.key} style={{ display: 'contents' }}>
              {grouped && (
                <div style={{
                  gridColumn: '1 / -1', display: 'flex', alignItems: 'center', gap: 8, margin: '4px 2px 0',
                }}>
                  {lane.color && <span style={{ width: 8, height: 8, borderRadius: '50%', background: lane.color, flexShrink: 0 }} />}
                  <span style={{ fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 700, color: C.textSecondary }}>{lane.label}</span>
                  <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>{lane.tasks.length}</span>
                  <span style={{ flex: 1, height: 1, background: C.borderLight }} />
                </div>
              )}
              {columns.map(col => (
                <BoardCell
                  key={`${lane.key}::${col.id}`}
                  cellId={`${lane.key}::${col.id}`}
                  cards={cellCards.get(`${lane.key}::${col.id}`) ?? []}
                  projectNameOf={projectNameOf}
                  onOpen={onOpenTask}
                  onQuickAdd={grouped ? undefined : title => void createTask(quickAddProjectId, { title, status: col.category, columnId: scope === 'project' ? col.id : undefined })}
                  minEmptyHeight={minEmptyHeight}
                />
              ))}
            </div>
          ))}
        </div>
      </div>

      <DragOverlay dropAnimation={null}>
        {activeTask ? (
          <div style={{ cursor: 'grabbing', width: 300 }}>
            <TaskCard task={activeTask} projectName={projectNameOf(activeTask)} onClick={() => {}} />
          </div>
        ) : null}
      </DragOverlay>
    </DndContext>
  );
}
