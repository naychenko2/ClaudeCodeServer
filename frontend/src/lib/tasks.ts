// Задачи: глобальный стор (все задачи пользователя) + вспомогательные функции UI.
// Паттерн стора — как featureFlags.ts: модульное состояние + useSyncExternalStore.
// Realtime: бэк шлёт task_changed в группу user_{userId} — стор обновляется сам.

import { useSyncExternalStore } from 'react';
import type { BoardColumn, CreateTaskDto, Task, TaskPriority, TaskRecurrence, TaskRecurrenceType, TaskStatus, UpdateTaskDto } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';
import { C } from './design';
import { getEffectiveTheme } from './themeMode';
import { isOnline, OfflineError, subscribeOnline } from './offline';
import { idbGet, idbSet } from './idb';
import {
  applyUpdateLocally, buildLocalTask, configureOutbox, drainTaskOutbox,
  enqueue, mergePending, outboxHasPending,
} from './taskOutbox';

// === Стор ===

let _tasks: Task[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();
let _realtimeWired = false;
let _hydrated = false;

// Локальный снапшот стора в IndexedDB (переживает перезагрузку, виден офлайн).
// Ключ отдельный от пассивного GET-кэша /tasks: здесь состояние С наложенными
// офлайн-правками, там — сырой ответ сервера.
const TASKS_SNAPSHOT_KEY = 'tasks:all';
let _persistTimer: ReturnType<typeof setTimeout> | null = null;

function persistTasks() {
  if (_persistTimer) return;
  _persistTimer = setTimeout(() => {
    _persistTimer = null;
    idbSet(TASKS_SNAPSHOT_KEY, { data: _tasks, savedAt: Date.now() }).catch(() => {});
  }, 300);
}

function emit() {
  _listeners.forEach(fn => fn());
  persistTasks();
}

function upsert(task: Task) {
  const idx = _tasks.findIndex(t => t.id === task.id);
  _tasks = idx >= 0
    ? [..._tasks.slice(0, idx), task, ..._tasks.slice(idx + 1)]
    : [..._tasks, task];
  emit();
}

function remove(taskId: string) {
  const next = _tasks.filter(t => t.id !== taskId);
  if (next.length !== _tasks.length) {
    _tasks = next;
    emit();
  }
}

// task_changed шлётся в группу user_{userId} — вступаем в неё
// (повторный JoinUser для того же соединения безопасен)
function joinUserGroup() {
  const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id');
  if (uid) joinUser(uid).catch(() => {});
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(msg => {
    if (msg.type !== 'task_changed') return;
    // Не затираем задачу, по которой есть несинхронизированная локальная правка:
    // pending (наш офлайн) новее входящего события (в т.ч. запоздалого/с др. устройства).
    if (outboxHasPending(msg.task.id)) return;
    if (msg.action === 'deleted') remove(msg.task.id);
    else upsert(msg.task);
  });
  // После разрыва соединения сначала проигрываем офлайн-очередь (reload внутри дренажа
  // канонизирует состояние, когда очередь опустеет — иначе он затрёт pending-правки).
  onReconnected(() => { joinUserGroup(); void drainTaskOutbox(); });
  // Связь может подняться и через probe в offline.ts (без WS-reconnected) — тоже дренажим.
  subscribeOnline(() => { if (isOnline()) void drainTaskOutbox(); });
}

export async function reloadTasks(): Promise<void> {
  const list = await api.tasks.listAll();
  // Поверх серверного списка накатываем ещё не синхронизированные офлайн-правки
  // (защита от reload при непустой очереди — напр. online-эффект опередил дренаж).
  _tasks = await mergePending(list, _tasks);
  _loaded = true;
  emit();
}

// Регистрируем store-хуки очереди один раз — до любого дренажа.
configureOutbox({ upsert, remove, reload: reloadTasks });

// Подхватить локальный снапшот стора из IndexedDB (мгновенно, до сети): офлайн-правки
// и последний известный список видны сразу и переживают перезагрузку страницы.
async function hydrateFromCache(): Promise<void> {
  if (_hydrated) return;
  _hydrated = true;
  if (_loaded) return;   // сеть уже успела наполнить стор
  const cached = await idbGet<Task[]>(TASKS_SNAPSHOT_KEY).catch(() => undefined);
  if (cached?.data && !_loaded) { _tasks = cached.data; emit(); }
}

// Первая загрузка (идемпотентно). Вызывается из компонентов задач при монтировании.
export function ensureTasksLoaded(): Promise<void> {
  wireRealtime();
  // Членство в группе подтверждаем на каждый заход (идемпотентно): если какая-то
  // страница ранее вышла из user_{id}, страница задач переподпишется.
  joinUserGroup();
  void hydrateFromCache(); void drainTaskOutbox();
  if (_loaded) return Promise.resolve();
  if (!_loading)
    _loading = reloadTasks().finally(() => { _loading = null; });
  return _loading;
}

export function useTasks(): Task[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _tasks,
    () => _tasks,
  );
}

// Синхронный снапшот стора по id (аналог getPersonaById) — для резолва контекста
// («в рамках какой задачи») на плашках чатов, в шапке и в артефактах сессии.
// Полагается на то, что ensureTasksLoaded() уже вызван где-то выше в дереве.
export function getTaskById(id: string): Task | undefined {
  return _tasks.find(t => t.id === id);
}

// === Мутации (обновляют стор сразу из ответа; broadcast продублирует — upsert идемпотентен) ===
// При флаге tasks-offline и отсутствии связи (или сетевом сбое) мутация уходит в
// outbox + оптимистично применяется локально; синхронизация — при возврате связи.

const offlineEnabled = () => true;

// Order для новой локальной задачи — в конец глобального порядка (как NextOrder на бэке)
function localNextOrder(): number {
  return _tasks.reduce((m, t) => Math.max(m, t.order ?? 0), 0) + 1000;
}

// Офлайн-создание: клиентский id (uuid) → нет remap, идемпотентный replay на сервере
function createTaskOffline(projectId: string | null, dto: CreateTaskDto): Task {
  const id = dto.id ?? crypto.randomUUID();
  const task = buildLocalTask(id, projectId, dto, localNextOrder());
  upsert(task);
  void enqueue(id, 'create', dto, { projectId });
  return task;
}

export async function createTask(projectId: string | null, dto: CreateTaskDto): Promise<Task> {
  if (offlineEnabled() && !isOnline()) return createTaskOffline(projectId, dto);
  try {
    const task = await api.tasks.create(projectId, dto);
    upsert(task);
    return task;
  } catch (e) {
    if (offlineEnabled() && e instanceof OfflineError) return createTaskOffline(projectId, dto);
    throw e;
  }
}

function updateTaskOffline(taskId: string, dto: UpdateTaskDto): Task {
  const cur = _tasks.find(t => t.id === taskId);
  const next = cur ? applyUpdateLocally(cur, dto) : undefined;
  if (next) upsert(next);
  void enqueue(taskId, 'update', dto, { baseUpdatedAt: cur?.updatedAt });
  return next ?? (cur as Task);
}

export async function updateTask(taskId: string, dto: UpdateTaskDto): Promise<Task> {
  if (offlineEnabled() && !isOnline()) return updateTaskOffline(taskId, dto);
  try {
    const task = await api.tasks.update(taskId, dto);
    upsert(task);
    return task;
  } catch (e) {
    if (offlineEnabled() && e instanceof OfflineError) return updateTaskOffline(taskId, dto);
    throw e;
  }
}

function deleteTaskOffline(taskId: string): void {
  remove(taskId);
  void enqueue(taskId, 'delete', {});
}

export async function deleteTask(taskId: string): Promise<void> {
  if (offlineEnabled() && !isOnline()) { deleteTaskOffline(taskId); return; }
  try {
    await api.tasks.delete(taskId);
    remove(taskId);
  } catch (e) {
    if (offlineEnabled() && e instanceof OfflineError) { deleteTaskOffline(taskId); return; }
    throw e;
  }
}

// Оптимистичное локальное обновление задачи (без запроса) — для мгновенного
// отклика доски на drag: карточка встаёт на место сразу, серверный ответ придёт следом.
export function upsertTaskLocal(task: Task): void {
  upsert(task);
}

// Hash-URL задачи в её родном разделе: проектная — вкладка «Задачи» проекта,
// личная — «Календарь». Формат совпадает с бэковым TaskSchedulerService.TaskUrl
// и диплинками уведомлений (App.openNotificationUrl их и обрабатывает).
export function taskHashUrl(task: Task): string {
  return task.projectId
    ? `#/project/${task.projectId}/task/${task.id}`
    : `#/calendar/task/${task.id}`;
}

// Открыть задачу в её разделе из любого места (вкладка «Задачи» персоны и т.п.):
// шлём глобальное событие, App переиспует навигацию уведомлений.
export function openTaskInSection(task: Task): void {
  window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url: taskHashUrl(task) } }));
}

// === Статусы ===

export const STATUS_ORDER: TaskStatus[] = ['inProgress', 'todo', 'done'];

export const STATUS_LABEL: Record<TaskStatus, string> = {
  todo:       'К выполнению',
  inProgress: 'В работе',
  done:       'Готово',
};

export const STATUS_DOT: Record<TaskStatus, string> = {
  todo:       C.textMuted,
  inProgress: C.accent,
  done:       C.success,
};

// === Приоритеты ===

export const PRIORITY_ORDER: TaskPriority[] = ['urgent', 'high', 'medium', 'low'];

export const PRIORITY_LABEL: Record<TaskPriority, string> = {
  urgent: 'Срочно',
  high:   'Высокий',
  medium: 'Средний',
  low:    'Низкий',
};

export const PRIORITY_COLOR: Record<TaskPriority, string> = {
  urgent: C.danger,
  high:   C.accent,
  medium: C.warning,
  low:    C.textMuted,
};

// Срочный флаг рисуется залитым, остальные — контурные
export const PRIORITY_FILL: Record<TaskPriority, boolean> = {
  urgent: true, high: false, medium: false, low: false,
};

// === Цвета проектов (детерминированная палитра по id) ===
// main — точки/полосы/аватары, soft — пастельный фон чипов в календаре

export interface ProjectColor { main: string; soft: string }

// main — точки/полосы/аватары (насыщенный, читается на любом фоне).
// soft — пастельный фон чипов/аватаров для СВЕТЛОЙ темы; softDark — тёмный
// тонированный аналог того же оттенка для ТЁМНОЙ темы (иначе светлые пастели
// становятся светлыми «островками» на тёмном фоне).
interface ProjectPaletteEntry { main: string; soft: string; softDark: string }

const PROJECT_PALETTE: ProjectPaletteEntry[] = [
  { main: '#5E8B4E', soft: '#E3EEDD', softDark: '#26311F' },  // зелёный
  { main: '#3E7CA6', soft: '#DEEAF3', softDark: '#1E2B34' },  // синий
  { main: '#C2693B', soft: '#F7E6D9', softDark: '#342319' },  // оранжевый
  { main: '#8E4A82', soft: '#F0E1ED', softDark: '#301F2C' },  // фиолетовый
  { main: '#4B6BB0', soft: '#E2E8F5', softDark: '#20283A' },  // индиго
  { main: '#3E9A94', soft: '#DDEEEC', softDark: '#1B2E2C' },  // бирюзовый
  { main: '#B4452F', soft: '#F6E1DC', softDark: '#341F1A' },  // терракотовый
  { main: '#7A7250', soft: '#EBE8DB', softDark: '#2B2921' },  // хаки
];

// Нейтральная пара для личных задач (вне проекта): тёплый taupe + тихая пастель
const NO_PROJECT_ENTRY: ProjectPaletteEntry = { main: '#9A8F7E', soft: '#EFEAE0', softDark: '#2A2621' };

// Публичная пара для личных задач. ВНИМАНИЕ: значение фиксируется при загрузке
// модуля — безопасно только `.main` (тема-независим). Для soft/полного цвета
// с учётом темы вызывай projectColor(null) в рендере.
export const NO_PROJECT_COLOR: ProjectColor = resolveColor(NO_PROJECT_ENTRY);

// Подпись «проекта» личной задачи в карточках/агенде/списке дня
export const NO_PROJECT_LABEL = 'Личное';

function resolveColor(e: ProjectPaletteEntry): ProjectColor {
  return { main: e.main, soft: getEffectiveTheme() === 'dark' ? e.softDark : e.soft };
}

export function projectColor(projectId?: string | null): ProjectColor {
  if (!projectId) return resolveColor(NO_PROJECT_ENTRY);
  let hash = 0;
  for (let i = 0; i < projectId.length; i++)
    hash = (hash * 31 + projectId.charCodeAt(i)) | 0;
  return resolveColor(PROJECT_PALETTE[Math.abs(hash) % PROJECT_PALETTE.length]);
}

// Инициал проекта для чипа-аватара («A» у «Acme API»)
export function projectInitial(name: string): string {
  return (name.trim()[0] ?? '?').toUpperCase();
}

// === Срок ===

// Локальная дата в ISO (YYYY-MM-DD) без UTC-сдвига
export function toIsoDate(d: Date): string {
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}

export function todayIso(): string {
  return toIsoDate(new Date());
}

export function addDaysIso(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(y, m - 1, d + days);
  return toIsoDate(date);
}

// Разница в днях между сроком и сегодня (отрицательная = просрочено)
export function daysFromToday(dueDate: string): number {
  const [y, m, d] = dueDate.split('-').map(Number);
  const due = new Date(y, m - 1, d);
  const now = new Date();
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  return Math.round((due.getTime() - today.getTime()) / 86400000);
}

const WEEKDAY_SHORT = ['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб'];

// Подпись чипа срока: «Сегодня», «Завтра», «Вчера», «Пн» (в пределах недели), «18 июн»
export function dueLabel(dueDate: string): string {
  const diff = daysFromToday(dueDate);
  if (diff === 0) return 'Сегодня';
  if (diff === 1) return 'Завтра';
  if (diff === -1) return 'Вчера';
  const [y, m, d] = dueDate.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  if (diff > 1 && diff < 7) return WEEKDAY_SHORT[date.getDay()];
  return date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace('.', '');
}

// Срок «горит»: сегодня или просрочен (и задача не готова)
export function isDueUrgent(task: Task): boolean {
  return task.status !== 'done' && !!task.dueDate && daysFromToday(task.dueDate) <= 0;
}

// === Повторение ===

export const RECURRENCE_TYPE_LABEL: Record<Exclude<TaskRecurrenceType, 'none'>, string> = {
  daily:   'Ежедневно',
  weekly:  'Еженедельно',
  monthly: 'Ежемесячно',
  yearly:  'Ежегодно',
};

// Порт TaskRecurrenceCalculator.cs — расчёт даты следующего экземпляра серии.
// Дублирует бэкенд (тест-зеркало в __tests__/recurrence.test.ts держит их в согласии),
// чтобы календарь мог показывать будущие повторы без обращения к серверу.

// ISO-день недели (1=Пн … 7=Вс) для даты YYYY-MM-DD
function isoWeekday(iso: string): number {
  const [y, m, d] = iso.split('-').map(Number);
  const wd = new Date(y, m - 1, d).getDay();   // 0=Вс … 6=Сб
  return wd === 0 ? 7 : wd;
}

// Разница в целых днях между двумя ISO-датами (a - b); UTC, чтобы не влиял переход на летнее время
function diffDaysIso(aIso: string, bIso: string): number {
  const [ay, am, ad] = aIso.split('-').map(Number);
  const [by, bm, bd] = bIso.split('-').map(Number);
  return Math.round((Date.UTC(ay, am - 1, ad) - Date.UTC(by, bm - 1, bd)) / 86400000);
}

// Прибавить месяцы с клипованием дня на конец месяца (как DateTime.AddMonths в .NET):
// 31 января + 1 мес → 28/29 февраля; 29 февраля + 12 мес → 28 февраля
function addMonthsIso(iso: string, months: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const total = (m - 1) + months;
  const ny = y + Math.floor(total / 12);
  const nm = ((total % 12) + 12) % 12;   // 0-based месяц
  const daysInMonth = new Date(ny, nm + 1, 0).getDate();
  return toIsoDate(new Date(ny, nm, Math.min(d, daysInMonth)));
}

// Следующий срок для weekly: каждые N недель по дням недели (или по дню исходного срока)
function nextWeekly(currentIso: string, interval: number, weekdays?: number[]): string {
  let days = weekdays && weekdays.length
    ? [...new Set(weekdays.filter(d => d >= 1 && d <= 7))].sort((a, b) => a - b)
    : [isoWeekday(currentIso)];
  if (days.length === 0) days = [isoWeekday(currentIso)];

  const currentWeekMonday = addDaysIso(currentIso, 1 - isoWeekday(currentIso));
  for (let offset = 1; offset <= interval * 14; offset++) {
    const candidate = addDaysIso(currentIso, offset);
    if (!days.includes(isoWeekday(candidate))) continue;
    const candidateWeekMonday = addDaysIso(candidate, 1 - isoWeekday(candidate));
    const weeksBetween = diffDaysIso(candidateWeekMonday, currentWeekMonday) / 7;
    if (weeksBetween % interval === 0) return candidate;
  }
  return addDaysIso(currentIso, 7 * interval);
}

// Дата следующего экземпляра (YYYY-MM-DD) или null — серия закончена (until) либо правило пустое
export function nextDueDate(currentIso: string, rule: TaskRecurrence): string | null {
  const interval = Math.max(1, rule.interval);
  let next: string | null;
  switch (rule.type) {
    case 'daily':   next = addDaysIso(currentIso, interval); break;
    case 'weekly':  next = nextWeekly(currentIso, interval, rule.weekdays); break;
    case 'monthly': next = addMonthsIso(currentIso, interval); break;
    case 'yearly':  next = addMonthsIso(currentIso, interval * 12); break;
    default:        next = null;
  }
  if (next === null) return null;
  if (rule.until && next > rule.until) return null;
  return next;
}

// Развернуть повторяющиеся задачи в набор для календаря: реальные задачи + вычисленные
// будущие повторы (virtual) в окне [fromIso, toIso]. Реально существует один экземпляр серии,
// поэтому у проекций occurrenceOf = id реального экземпляра (его и открываем по клику).
const MAX_OCCURRENCES = 400;   // предохранитель от зацикливания на битом правиле

export function expandRecurringTasks(tasks: Task[], fromIso: string, toIso: string): Task[] {
  const result: Task[] = [];
  for (const t of tasks) {
    result.push(t);
    // Завершённый экземпляр не проецируем — следующий уже создан как реальная задача
    if (!t.recurrence || t.recurrence.type === 'none' || !t.dueDate || t.status === 'done') continue;

    let date = t.dueDate;
    for (let guard = 0; guard < MAX_OCCURRENCES; guard++) {
      const next = nextDueDate(date, t.recurrence);
      if (next === null || next > toIso) break;
      date = next;
      if (next < fromIso) continue;   // повтор раньше окна — пропускаем, но продолжаем перебор
      result.push({ ...t, id: `${t.id}#${next}`, dueDate: next, occurrenceOf: t.id, virtual: true });
    }
  }
  return result;
}

const ISO_WEEKDAY_SHORT = ['', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

// «Ежедневно», «Каждые 2 нед · Пн, Ср», «Ежемесячно», «Каждые 3 г»
export function recurrenceLabel(r: TaskRecurrence): string {
  if (r.type === 'none') return '';
  const base = r.interval > 1
    ? { daily: `Каждые ${r.interval} дн`, weekly: `Каждые ${r.interval} нед`, monthly: `Каждые ${r.interval} мес`, yearly: `Каждые ${r.interval} г` }[r.type]
    : RECURRENCE_TYPE_LABEL[r.type];
  const days = r.type === 'weekly' && r.weekdays?.length
    ? ' · ' + [...r.weekdays].sort((a, b) => a - b).map(d => ISO_WEEKDAY_SHORT[d] ?? '').filter(Boolean).join(', ')
    : '';
  return base + days;
}

// === Напоминания ===

// Пресеты офсета напоминания (минуты до срока) для чипов формы
export const REMINDER_PRESETS = [0, 15, 60, 1440] as const;

// «В момент срока», «За 15 мин», «За 2 ч», «За 3 дн»
export function reminderLabel(minutes: number): string {
  if (minutes === 0) return 'В момент срока';
  if (minutes % 1440 === 0) return `За ${minutes / 1440} дн`;
  if (minutes % 60 === 0) return `За ${minutes / 60} ч`;
  return `За ${minutes} мин`;
}

// === Kanban-доска: порядок карточек, сортировка, дорожки (swimlanes) ===

// Значение Order для вставки карточки между соседями (midpoint) или на край колонки.
// prev — order карточки сверху слота, next — снизу. Шаг края 1000 (как на бэке).
export function computeOrder(prev?: number, next?: number): number {
  if (prev !== undefined && next !== undefined) return (prev + next) / 2;
  if (prev !== undefined) return prev + 1000;
  if (next !== undefined) return next - 1000;
  return 1000;
}

// Сортировка карточек в колонке: по Order, тай-брейк приоритет → срок → создание.
// Тай-брейк даёт осмысленный дефолт для нулевых (legacy) Order до первого drag.
export function boardCardSort(a: Task, b: Task): number {
  const ao = a.order ?? 0, bo = b.order ?? 0;
  if (ao !== bo) return ao - bo;
  const ap = PRIORITY_ORDER.indexOf(a.priority), bp = PRIORITY_ORDER.indexOf(b.priority);
  if (ap !== bp) return ap - bp;
  const ad = a.dueDate ?? '9999', bd = b.dueDate ?? '9999';
  if (ad !== bd) return ad < bd ? -1 : 1;
  return a.createdAt < b.createdAt ? -1 : a.createdAt > b.createdAt ? 1 : 0;
}

// Дефолтные колонки доски (когда у проекта нет кастомных, а также хаб-доска и личные задачи):
// 3 колонки = 3 категории статусов. Id = категория (совпадает со status задачи).
export const DEFAULT_BOARD_COLUMNS: BoardColumn[] = [
  { id: 'todo', name: STATUS_LABEL.todo, category: 'todo' },
  { id: 'inProgress', name: STATUS_LABEL.inProgress, category: 'inProgress' },
  { id: 'done', name: STATUS_LABEL.done, category: 'done' },
];

// Колонки для рендера: кастомные проекта или дефолтные 3
export function resolveColumns(columns?: BoardColumn[]): BoardColumn[] {
  return columns && columns.length ? columns : DEFAULT_BOARD_COLUMNS;
}

// Цвет колонки: явный или по её категории
export function columnColor(col: BoardColumn): string {
  return col.color || STATUS_DOT[col.category];
}

// В какую колонку попадает задача: явная (если её категория совпадает со статусом),
// иначе — первая колонка категории статуса; фолбэк — первая колонка.
export function taskColumnKey(task: Task, columns: BoardColumn[]): string {
  if (task.columnId) {
    const c = columns.find(x => x.id === task.columnId);
    if (c && c.category === task.status) return c.id;
  }
  const byCat = columns.find(x => x.category === task.status);
  return byCat?.id ?? columns[0]?.id ?? task.status;
}

export type BoardGroupBy = 'none' | 'priority' | 'assignee' | 'project' | 'due';

export const BOARD_GROUP_LABEL: Record<BoardGroupBy, string> = {
  none: 'Без дорожек',
  priority: 'По приоритету',
  assignee: 'По исполнителю',
  project: 'По проекту',
  due: 'По сроку',
};

// Дорожка доски (swimlane): набор задач + подпись/цвет строки
export interface BoardLane {
  key: string;          // стабильный ключ дорожки (для droppable id и cross-lane правок)
  label: string;
  color?: string;       // точка-маркер строки
  tasks: Task[];
}

// Ведёрки для группировки «по сроку»
type DueBucket = 'overdue' | 'today' | 'tomorrow' | 'week' | 'later' | 'none';
function dueBucket(task: Task): DueBucket {
  if (!task.dueDate) return 'none';
  const d = daysFromToday(task.dueDate);
  if (d < 0) return 'overdue';
  if (d === 0) return 'today';
  if (d === 1) return 'tomorrow';
  if (d <= 7) return 'week';
  return 'later';
}
const DUE_BUCKET_ORDER: DueBucket[] = ['overdue', 'today', 'tomorrow', 'week', 'later', 'none'];
const DUE_BUCKET_LABEL: Record<DueBucket, string> = {
  overdue: 'Просрочено', today: 'Сегодня', tomorrow: 'Завтра',
  week: 'На этой неделе', later: 'Позже', none: 'Без срока',
};
const DUE_BUCKET_COLOR: Record<DueBucket, string> = {
  overdue: C.danger, today: C.accent, tomorrow: C.warning,
  week: C.success, later: C.textMuted, none: C.textMuted,
};

export const ASSIGNEE_LABEL: Record<string, string> = { me: 'Я', claude: 'AI', none: 'Не назначен' };

// Разбить задачи на дорожки согласно groupBy. Порядок дорожек фиксирован
// (приоритет/срок — по важности), у проектов — личные первыми. Пустые дорожки не рисуем.
export function boardLanes(
  tasks: Task[], groupBy: BoardGroupBy, projectsById: Map<string, { name: string }>,
): BoardLane[] {
  if (groupBy === 'none') return [{ key: 'all', label: '', tasks }];

  const byKey = new Map<string, Task[]>();
  const push = (k: string, t: Task) => {
    const arr = byKey.get(k); if (arr) arr.push(t); else byKey.set(k, [t]);
  };

  if (groupBy === 'priority') {
    tasks.forEach(t => push(t.priority, t));
    return PRIORITY_ORDER.filter(p => byKey.has(p))
      .map(p => ({ key: p, label: PRIORITY_LABEL[p], color: PRIORITY_COLOR[p], tasks: byKey.get(p)! }));
  }
  if (groupBy === 'assignee') {
    tasks.forEach(t => push(t.assignee ?? 'none', t));
    return ['claude', 'me', 'none'].filter(k => byKey.has(k))
      .map(k => ({ key: k, label: ASSIGNEE_LABEL[k], tasks: byKey.get(k)! }));
  }
  if (groupBy === 'due') {
    tasks.forEach(t => push(dueBucket(t), t));
    return DUE_BUCKET_ORDER.filter(b => byKey.has(b))
      .map(b => ({ key: b, label: DUE_BUCKET_LABEL[b], color: DUE_BUCKET_COLOR[b], tasks: byKey.get(b)! }));
  }
  // project: личные (none) первыми, затем проекты в порядке появления
  tasks.forEach(t => push(t.projectId ?? 'none', t));
  const keys = [...byKey.keys()].sort((a, b) => (a === 'none' ? -1 : b === 'none' ? 1 : 0));
  return keys.map(k => ({
    key: k,
    label: k === 'none' ? NO_PROJECT_LABEL : projectsById.get(k)?.name ?? 'Проект',
    color: projectColor(k === 'none' ? null : k).main,
    tasks: byKey.get(k)!,
  }));
}
