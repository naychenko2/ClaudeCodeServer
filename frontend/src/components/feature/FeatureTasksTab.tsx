// Вкладка «Задачи» страницы фичи (волна 3, Ф4; макет features-overview-3-final §4/§9а).
//
// Перенос блока «Задачи» из волны 2 (FeaturePageBody) в новую вёрстку макета:
//   • строка = аватар исполнителя · название · роль (roleLabel) · статус (факт) ·
//     пара «чат · задача» (NavTargetPair: чат = linkedSessionId, задача = id);
//   • фильтр по статусу: «все» (всегда) + каждый статус с числом > 0 (макет: r/w/x/d/t);
//   • сортировка по времени (последняя активность: completedAt → claudeStartedAt → createdAt);
//   • «Показать все» при длинном списке: порог 8 (макет LIM), по умолчанию — 8 последних;
//   • пустое: «Задач нет» (фича создана, конвейер не запускался).
//
// Честный прогресс (разрез Б-pr1/Б-pr2/Ф-pr): статус показываем как ФАКТ
// («закрыта», «в работе», «ждёт вас», «стоит», «впереди»), а НЕ «принято N из M».
// Прогресс-бар и «принято» — не здесь (это вкладка «Статистика»).

import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { roleLabel } from '../../lib/roleLabel';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { ExecutorAvatar } from './FeatureTimelineBits';
import { NavTargetPair } from './NavTargetButton';
import { Button } from '../ui/Button';
import type { FeatureRun, FeatureTask } from '../../types/featureRun';

// Порог «Показать все» (макет: LIM=8 у событий; тот же порог и у задач).
// По умолчанию — 8 последних (по времени); «Показать все» — весь список.
export const TASKS_COLLAPSED_LIMIT = 8;

// === Чистые функции (тестируются в node, без DOM) ===

// Все задачи фичи в плоском списке: эпик + шаги + ревью + проверки + финал +
// мерж + разрезы + планы. Контракт: UI обязан пережить отсутствие любого списка
// (старые данные) — пустые/отсутствующие раскладываем как [].
export function allFeatureTasks(run: FeatureRun): FeatureTask[] {
  return [
    ...(run.epic ? [run.epic] : []),
    ...(run.steps ?? []),
    ...(run.reviews ?? []),
    ...(run.qa ?? []),
    ...(run.final ? [run.final] : []),
    ...(run.merge ? [run.merge] : []),
    ...(run.cut ?? []),
    ...(run.plan ?? []),
  ];
}

// Статус задачи (макет TST): ключ → подпись.
//   d — готово (done); r — в работе (inProgress, идёт); w — ждёт вас (inProgress,
//   ждёт человека); x — стоит (inProgress, исполнитель встал); t — впереди (todo/нет).
// Это ФАКТ состояния, не «принято»: честный прогресс (разрез Ф-pr).
export type TaskStatusKey = 'd' | 'r' | 'w' | 'x' | 't';
export function taskStatusKey(t: FeatureTask): TaskStatusKey {
  if (t.status === 'done') return 'd';
  if (t.status === 'inProgress') {
    if (t.awaitingHumanSince) return 'w';
    if (t.executorStoppedAt) return 'x';
    return 'r';
  }
  return 't';
}

// Подпись статуса (макет TST, по-русски): готово / в работе / ждёт вас / стоит / впереди.
export function taskStatusLabel(key: TaskStatusKey): string {
  switch (key) {
    case 'd': return 'готово';
    case 'r': return 'в работе';
    case 'w': return 'ждёт вас';
    case 'x': return 'стоит';
    case 't': return 'впереди';
    default: return '';
  }
}

// Ключ сортировки: последняя активность (completedAt → claudeStartedAt → createdAt).
// Пусто/невалидно — '' (сортируется в конец при desc по строке? нет — отдельно).
function taskSortKey(t: FeatureTask): string {
  return t.completedAt ?? t.claudeStartedAt ?? t.createdAt ?? '';
}

// Сортировка по времени: последняя активность — сначала (desc). Стабильная: при
// равенстве ключей — исходный порядок (Array.prototype.sort стабилен в ES2019+).
export function sortTasksByTime(tasks: FeatureTask[]): FeatureTask[] {
  return [...tasks].sort((a, b) => {
    const ka = taskSortKey(a);
    const kb = taskSortKey(b);
    if (ka === kb) return 0;
    return ka < kb ? 1 : -1; // desc: более свежее — раньше
  });
}

// Фильтр по статусу: 'all' — все; иначе — только с этим статусом.
export function filterTasksByStatus(tasks: FeatureTask[], filter: 'all' | TaskStatusKey): FeatureTask[] {
  if (filter === 'all') return tasks;
  return tasks.filter(t => taskStatusKey(t) === filter);
}

// Чипы-фильтр (макет): «все» (всегда, с общим числом) + каждый статус с числом > 0.
// Порядок статусов — как в макете (r, w, x, d, t).
export interface TaskFilterChip {
  key: 'all' | TaskStatusKey;
  label: string;
  count: number;
}
export function taskFilterChips(tasks: FeatureTask[]): TaskFilterChip[] {
  const cnt = (k: TaskStatusKey) => tasks.filter(t => taskStatusKey(t) === k).length;
  const chips: TaskFilterChip[] = [{ key: 'all', label: 'все', count: tasks.length }];
  for (const k of ['r', 'w', 'x', 'd', 't'] as TaskStatusKey[]) {
    if (cnt(k) > 0) chips.push({ key: k, label: taskStatusLabel(k), count: cnt(k) });
  }
  return chips;
}

// === Компоненты ===

// Тег статуса (макет .tst): 11/600, padding 0 6, R.sm, line-height 16; цвет по ключу.
// Проп `status` (не `key` — `key` зарезервирован React'ом и теряется).
function TaskStatusTag({ status, isMobile = false }: { status: TaskStatusKey; isMobile?: boolean }) {
  const style: CSSProperties =
    status === 'd' ? { background: C.successBg, color: C.successText } :
    status === 'r' ? { background: C.bgSelected, color: C.textHeading } :
    status === 'w' ? { background: C.infoBg, color: C.textPrimary } :
    status === 'x' ? { background: C.dangerBg, color: C.dangerText } :
    { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.border}` };
  return (
    <span
      data-testid="task-status"
      style={{
        fontSize: FS.xs, fontWeight: 600, padding: '0 6px', borderRadius: R.sm,
        lineHeight: '16px', whiteSpace: 'nowrap', flexShrink: 0,
        minHeight: isMobile ? 40 : undefined, display: 'inline-flex', alignItems: 'center',
        ...style,
      }}
    >
      {taskStatusLabel(status)}
    </span>
  );
}

// Строка задачи (макет §9а, .srow): аватар · название · роль (roleLabel) · статус ·
// пара «чат · задача». Клик по строке — в задачу; «чат» — в чат исполнителя.
// Честная деградация: без исполнителя — нейтральный аватар; без чата — «чата нет».
function TaskRow({ task, featureId, featureTitle = null, marks = EMPTY_NAV_MARKS, isMobile = false }: {
  task: FeatureTask; featureId: string; featureTitle?: string | null; marks?: NavMarks; isMobile?: boolean;
}) {
  const [hover, setHover] = useState(false);
  const role = roleLabel(task.role ?? null, task.stepRef ?? null, task.lane ?? null);
  const statusKey = taskStatusKey(task);

  // Переход в задачу: openTaskInSection (id + projectId) + точка возврата (Ф9b).
  const openTask = () => {
    recordFeatureReturn(featureId, { kind: 'task', id: task.id }, featureTitle);
    openTaskInSection({ id: task.id, projectId: task.projectId ?? undefined });
  };
  // Переход в чат исполнителя: openChatById(linkedSessionId) + точка возврата.
  const openChat = () => {
    if (!task.linkedSessionId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: task.linkedSessionId }, featureTitle);
    void openChatById(task.linkedSessionId);
  };

  return (
    <div
      data-testid={`task-row-${task.id}`}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
        margin: `0 -${SP.sm}px`, padding: `0 ${SP.sm}px`,
        background: hover ? C.bgSelected : 'transparent',
        borderRadius: R.md,
        minHeight: isMobile ? 52 : 44,
      }}
    >
      <ExecutorAvatar personaId={task.personaId ?? null} />
      <button
        type="button"
        data-testid="task-open"
        onClick={openTask}
        title={`Открыть задачу${statusKey === 'd' ? ' (закрыта — для просмотра)' : ''}. Страница фичи скроется, вернётся по плашке.`}
        aria-label={`Открыть задачу «${task.title}»`}
        style={{
          flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap',
          background: 'transparent', border: 'none', cursor: 'pointer', padding: `${SP.xxs}px 0`,
          fontFamily: FONT.sans, textAlign: 'left',
        }}
      >
        <span
          data-testid="task-title"
          style={{
            fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            minWidth: 0, flex: '1 1 auto',
          }}
        >
          {task.title}
        </span>
        {role && (
          <span
            data-testid="task-role"
            style={{
              fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
              background: C.bgSelected, borderRadius: R.sm, padding: '0 5px',
              whiteSpace: 'nowrap', flexShrink: 0,
            }}
          >
            {role}
          </span>
        )}
        <TaskStatusTag status={statusKey} isMobile={isMobile} />
      </button>
      <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: SP.xs, flexShrink: 0 }}>
        <NavTargetPair
          chatId={task.linkedSessionId ?? null}
          chatArchived={marks.chatArchived(task.linkedSessionId)}
          onOpenChat={openChat}
          chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
          showNoChat
          taskId={task.id}
          taskClosed={statusKey === 'd' || marks.taskClosed(task.id)}
          onOpenTask={openTask}
          taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
          isMobile={isMobile}
        />
      </span>
    </div>
  );
}

// Поясняющая строка (макет §9а, .navnote): куда ведёт строка и кнопка «чат».
function TasksNavNote() {
  return (
    <div
      data-testid="tasks-nav-note"
      style={{
        display: 'flex', gap: 6, alignItems: 'flex-start',
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
        padding: '6px 10px', marginTop: SP.sm, fontFamily: FONT.sans,
        fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
      }}
    >
      <span aria-hidden style={{ flexShrink: 0, marginTop: 1 }}>ⓘ</span>
      <span>
        Строка ведёт в задачу, кнопка «чат» — в чат исполнителя. Страница фичи скроется
        и вернётся по плашке на этой же вкладке.
      </span>
    </div>
  );
}

export interface FeatureTasksTabProps {
  // Агрегат фичи: задачи (epic/steps/reviews/qa/final/merge/cut/plan) — единственный источник.
  run: FeatureRun;
  // Id фичи (навигация: точка возврата).
  featureId: string;
  // Пометки (страница даёт с чатами; без пропа — из агрегата: задачи закрыты, чаты — по стадиям).
  marks?: NavMarks;
  isMobile?: boolean;
}

export function FeatureTasksTab({ run, featureId, marks: marksProp, isMobile = false }: FeatureTasksTabProps) {
  const marks = useMemo(() => marksProp ?? buildNavMarks(run), [marksProp, run]);
  const all = useMemo(() => allFeatureTasks(run), [run]);
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run.epic?.title ?? null;
  const [filter, setFilter] = useState<'all' | TaskStatusKey>('all');
  const [expanded, setExpanded] = useState(false);

  // Фильтр → сортировка по времени → (свёрнуто) 8 последних.
  const filtered = useMemo(() => filterTasksByStatus(all, filter), [all, filter]);
  const sorted = useMemo(() => sortTasksByTime(filtered), [filtered]);
  const shown = expanded ? sorted : sorted.slice(0, TASKS_COLLAPSED_LIMIT);
  const chips = useMemo(() => taskFilterChips(all), [all]);

  // Пустое: задач нет вовсе (фича создана, конвейер не запускался).
  if (all.length === 0) {
    return (
      <div data-testid="tasks-empty" style={{ padding: `${SP.xl}px 0`, textAlign: 'center', fontFamily: FONT.sans }}>
        <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: SP.xs }}>Задач нет</div>
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Фича создана, но конвейер ещё не запускался.</div>
      </div>
    );
  }

  return (
    <div data-testid="tasks" style={{ display: 'flex', flexDirection: 'column', fontFamily: FONT.sans }}>
      {/* Фильтр по статусу (макет .cnts): «все» + статусы с числом > 0. */}
      <div data-testid="tasks-filter" role="group" aria-label="Фильтр задач" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginBottom: SP.sm }}>
        {chips.map(chip => {
          const active = filter === chip.key;
          return (
            <Button
              key={chip.key}
              variant="ghost"
              size="xs"
              dataTestId={`tasks-filter-${chip.key}`}
              ariaPressed={active}
              onClick={() => setFilter(chip.key)}
              style={{
                padding: '1px 8px', borderRadius: R.sm, lineHeight: '18px',
                fontSize: FS.xs, height: 'auto', whiteSpace: 'nowrap',
                background: active ? C.accent : C.bgSelected,
                color: active ? C.onAccent : C.textSecondary,
                border: 'none',
                minHeight: isMobile ? 40 : undefined,
              }}
            >
              {chip.label} {chip.count}
            </Button>
          );
        })}
      </div>

      {/* Список: 8 последних (по времени) или все (раскрыто). */}
      <div data-testid="tasks-list" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
        {shown.length === 0 ? (
          <div data-testid="tasks-filter-empty" style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.xs }}>
            Задач с этим статусом нет.
          </div>
        ) : (
          shown.map(t => (
            <TaskRow key={t.id} task={t} featureId={featureId} featureTitle={featureTitle} marks={marks} isMobile={isMobile} />
          ))
        )}
      </div>

      {/* «Показать все» / «Свернуть» при длинном списке (порог 8). */}
      {sorted.length > TASKS_COLLAPSED_LIMIT && (
        <Button
          variant="ghost"
          size="sm"
          fullWidth
          dataTestId="tasks-show-all"
          onClick={() => setExpanded(v => !v)}
          style={{
            marginTop: SP.sm,
            border: `1px solid ${C.border}`, background: C.bgSelected,
            borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
            fontSize: FS.sm, color: C.textHeading,
            minHeight: isMobile ? 40 : 32,
          }}
        >
          {expanded ? 'Свернуть' : `Показать все · ${sorted.length}`}
        </Button>
      )}

      <TasksNavNote />
    </div>
  );
}
