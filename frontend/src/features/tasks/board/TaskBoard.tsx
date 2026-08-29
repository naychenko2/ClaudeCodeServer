// Вид «Доска» (Kanban): произвольные колонки (кастомные проекта или дефолтные 3),
// drag & drop (смена колонки/категории + ручной порядок), дорожки (swimlanes),
// фильтры (общий стор boardControls), быстрое добавление и WIP-лимиты. Данные — из
// стора задач (реальные, без виртуальных повторов). Тулбар: инлайн (хаб/мобайл) или
// в сайдбаре (десктоп-проект, тогда inlineToolbar=false — тулбар рендерит WorkspacePage).

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  DndContext, DragOverlay, MouseSensor, TouchSensor, useSensor, useSensors,
  closestCorners, type DragEndEvent, type DragStartEvent,
} from '@dnd-kit/core';
import { ChevronRight } from 'lucide-react';
import type { BoardColumn as BoardColumnType, Project, Task, TaskAssignee, TaskPriority, UpdateTaskDto } from '../../../types';
import { C, FONT, R, SHADOW } from '../../../lib/design';
import {
  boardCardSort, boardLanes, columnColor, computeOrder, createTask, reloadTasks,
  taskColumnKey, updateTask, upsertTaskLocal, type BoardGroupBy,
} from '../../../lib/tasks';
import { showToast } from '../../../lib/toast';
import { useBoardControls, setGroupBy } from '../../../lib/boardControls';
import { DRAG_MOUSE_ACTIVATION, DRAG_TOUCH_ACTIVATION } from '../../../lib/dnd';
import { OfflineError } from '../../../lib/offline';
import { useWindowWidth, TABLET_MAX } from '../../../lib/breakpoints';
import { TaskCard } from '../TaskCard';
import { BoardCell, ColumnHeader } from './BoardColumn';
import { BoardToolbar } from './BoardToolbar';

// Геометрия доски: гэп между колонками и зазор справа под бегунок 6px (см. скролл-контейнер
// ниже). Колонка живёт в диапазоне [COL_MIN, COL_MAX] — конкретная ширина считается от
// фактической ширины контейнера, см. colMin.
const BOARD_GAP = 14;
const BOARD_PAD_RIGHT = 12;
const COL_MIN = 200;
const COL_MAX = 280;

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
    useSensor(MouseSensor, { activationConstraint: DRAG_MOUSE_ACTIVATION }),
    useSensor(TouchSensor, { activationConstraint: DRAG_TOUCH_ACTIVATION }),
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

    // Откат drag только на реальной ошибке. При офлайне (OfflineError / выключенный
    // флаг) не откатываем — иначе карточка отлетает назад; офлайн-путь уже сохранил её.
    // Для отказа бэка (например, дефект в Done без Verification, попадание в review без
    // Repro.steps) показываем тост с серверным текстом и перезагружаем стор — это
    // откатит оптимистичное перемещение и вернёт карточку на место
    void updateTask(activeTask.id, dto).catch(e => {
      if (e instanceof OfflineError) return;
      showToast('Не удалось переместить карточку',
        e instanceof Error ? e.message : 'Сервер отклонил изменение', 'error');
      void reloadTasks();
    });
  };

  const grouped = groupBy !== 'none';
  const minEmptyHeight = grouped ? 76 : 260;
  const activeTask = activeId ? taskById.get(activeId) : null;

  // QA Fold 8 (round 2, F1): ширину колонок считаем от ФАКТИЧЕСКОЙ ширины доски, а не от
  // брейкпоинта окна. На 832 боковая панель воркспейса отъедает ~53–250px, контейнер
  // получает 726 вместо 832, и «3 × 240 + гэпы = 748» уже не влезали — третья колонка
  // обрезалась. Теперь колонка = (ширина − гэпы) / N, зажатая в [COL_MIN, COL_MAX]:
  // на 1440 это по-прежнему 280, на 832 — ~228, на телефоне упирается в 200 и работает
  // горизонтальный скролл с edge-fade.
  const windowWidth = useWindowWidth();
  const isCompact = windowWidth <= TABLET_MAX;
  const [boardWidth, setBoardWidth] = useState(0);
  const colMin = useMemo(() => {
    // До первого замера — прежнее поведение по брейкпоинту (один кадр, без прыжка)
    if (boardWidth <= 0 || columns.length === 0) return isCompact ? 240 : COL_MAX;
    const avail = boardWidth - BOARD_PAD_RIGHT - (columns.length - 1) * BOARD_GAP;
    if (avail <= 0) return COL_MIN;
    return Math.max(COL_MIN, Math.min(Math.floor(avail / columns.length), COL_MAX));
  }, [boardWidth, columns.length, isCompact]);
  // Доска — фиксированная N-колонная сетка под карточки заранее известной ширины;
  // `repeat(N, ${colMin}px)` (без `1fr`) держит колонку ровно `colMin` и на 832, и на
  // 1440 — иначе `1fr` на широком окне расползает колонку до 366px и ломает
  // исходное обещание «280px на 1440». Зазоры между колонками визуально растут.
  // Ровно N колонок и N−1 гэпов: лишний гэп в минимальной ширине давал скролл на 14px
  // даже когда колонки идеально помещались
  const gridTemplateColumns = columns.length > 0
    ? `repeat(${columns.length}, ${colMin}px)`
    : 'none';
  const gridMinWidth = columns.length > 0
    ? columns.length * colMin + (columns.length - 1) * BOARD_GAP
    : colMin;

  // Edge-fade: при горизонтальном переполнении доски показываем тонкий градиент +
  // круглую «→» у правого края, пока не доскроллили до конца. Левый — симметрично.
  // Сам градиент лежит на скролл-контейнере (position:relative), стрелка — в нём
  // абсолютно. ResizeObserver ловит ресайз окна и панелей (появление/уход скролла)
  // и заодно снимает ширину контейнера для расчёта colMin выше.
  const scrollRef = useRef<HTMLDivElement>(null);
  const [fadeLeft, setFadeLeft] = useState(false);
  const [fadeRight, setFadeRight] = useState(false);
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const update = () => {
      const max = el.scrollWidth - el.clientWidth;
      setFadeLeft(el.scrollLeft > 1);
      setFadeRight(el.scrollLeft < max - 1);
      setBoardWidth(el.clientWidth);
    };
    update();
    el.addEventListener('scroll', update, { passive: true });
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => { el.removeEventListener('scroll', update); ro.disconnect(); };
  }, [columns.length, windowWidth]);

  return (
    <DndContext sensors={sensors} collisionDetection={closestCorners} onDragStart={onDragStart} onDragEnd={onDragEnd}>
      {inlineToolbar && <BoardToolbar layout="inline" groupOptions={groupOptions} onEditColumns={onEditColumns} />}

      {/* Скролл-контейнер доски. Класс `cc-hide-scrollbar` снят (QA Fold 8): на планшете
          минимум колонки 240, и три колонки влезают без скролла; при переполнении (4+
          колонок, телефон) видна тонкая полоса и edge-fade со стрелкой. Контейнер —
          `position: relative` для абсолютных fade-плашек, paddingRight 12 оставляет
          зазор под бегунок 6px у самого края. */}
      <div ref={scrollRef} className="cc-board-scroll" style={{
        position: 'relative', overflowX: 'auto', overflowY: 'hidden',
        paddingBottom: 24, paddingRight: BOARD_PAD_RIGHT,
      }}>
        <div style={{
          display: 'grid', gridTemplateColumns,
          gap: BOARD_GAP, width: '100%', minWidth: gridMinWidth, alignItems: 'start',
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
        {/* Edge-fade: 28px градиент + круглая «→» 22px. Показываются пока
            соответствующая кромка скрыта за пределами вьюпорта. pointerEvents:none —
            иначе градиент ловил бы клики по колонкам у края. */}
        {fadeLeft && (
          <>
            <div aria-hidden style={{
              position: 'absolute', top: 0, bottom: 24, left: 0, width: 28,
              background: `linear-gradient(to right, ${C.bgMain}, ${C.bgMain}00)`,
              pointerEvents: 'none', transition: 'opacity 0.15s',
            }} />
            <div aria-hidden style={{
              position: 'absolute', top: '50%', left: 4, width: 22, height: 22,
              borderRadius: R.full, transform: 'translateY(-50%) rotate(180deg)',
              background: C.bgWhite, border: `1px solid ${C.border}`,
              boxShadow: SHADOW.card, color: C.textSecondary,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              pointerEvents: 'none',
            }}>
              <ChevronRight size={13} strokeWidth={2.2} />
            </div>
          </>
        )}
        {fadeRight && (
          <>
            <div aria-hidden style={{
              position: 'absolute', top: 0, bottom: 24, right: 0, width: 28,
              background: `linear-gradient(to left, ${C.bgMain}, ${C.bgMain}00)`,
              pointerEvents: 'none', transition: 'opacity 0.15s',
            }} />
            <div aria-hidden style={{
              position: 'absolute', top: '50%', right: 4, width: 22, height: 22,
              borderRadius: R.full, transform: 'translateY(-50%)',
              background: C.bgWhite, border: `1px solid ${C.border}`,
              boxShadow: SHADOW.card, color: C.textSecondary,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              pointerEvents: 'none',
            }}>
              <ChevronRight size={13} strokeWidth={2.2} />
            </div>
          </>
        )}
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
