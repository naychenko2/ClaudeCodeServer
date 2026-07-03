// Задачи: глобальный стор (все задачи пользователя) + вспомогательные функции UI.
// Паттерн стора — как featureFlags.ts: модульное состояние + useSyncExternalStore.
// Realtime: бэк шлёт task_changed в группу user_{userId} — стор обновляется сам.

import { useSyncExternalStore } from 'react';
import type { CreateTaskDto, Task, TaskPriority, TaskStatus, UpdateTaskDto } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';
import { C } from './design';

// === Стор ===

let _tasks: Task[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() {
  _listeners.forEach(fn => fn());
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
  joinUserGroup();
  onMessage(msg => {
    if (msg.type !== 'task_changed') return;
    if (msg.action === 'deleted') remove(msg.task.id);
    else upsert(msg.task);
  });
  // После разрыва соединения могли потеряться события — перечитываем целиком
  onReconnected(() => { joinUserGroup(); void reloadTasks(); });
}

export async function reloadTasks(): Promise<void> {
  const list = await api.tasks.listAll();
  _tasks = list;
  _loaded = true;
  emit();
}

// Первая загрузка (идемпотентно). Вызывается из компонентов задач при монтировании.
export function ensureTasksLoaded(): Promise<void> {
  wireRealtime();
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

// === Мутации (обновляют стор сразу из ответа; broadcast продублирует — upsert идемпотентен) ===

export async function createTask(projectId: string | null, dto: CreateTaskDto): Promise<Task> {
  const task = await api.tasks.create(projectId, dto);
  upsert(task);
  return task;
}

export async function updateTask(taskId: string, dto: UpdateTaskDto): Promise<Task> {
  const task = await api.tasks.update(taskId, dto);
  upsert(task);
  return task;
}

export async function deleteTask(taskId: string): Promise<void> {
  await api.tasks.delete(taskId);
  remove(taskId);
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

const PROJECT_PALETTE: ProjectColor[] = [
  { main: '#5E8B4E', soft: '#E3EEDD' },  // зелёный
  { main: '#3E7CA6', soft: '#DEEAF3' },  // синий
  { main: '#C2693B', soft: '#F7E6D9' },  // оранжевый
  { main: '#8E4A82', soft: '#F0E1ED' },  // фиолетовый
  { main: '#4B6BB0', soft: '#E2E8F5' },  // индиго
  { main: '#3E9A94', soft: '#DDEEEC' },  // бирюзовый
  { main: '#B4452F', soft: '#F6E1DC' },  // терракотовый
  { main: '#7A7250', soft: '#EBE8DB' },  // хаки
];

// Нейтральная пара для личных задач (вне проекта): тёплый taupe + тихая пастель
export const NO_PROJECT_COLOR: ProjectColor = { main: '#9A8F7E', soft: '#EFEAE0' };

// Подпись «проекта» личной задачи в карточках/агенде/списке дня
export const NO_PROJECT_LABEL = 'Личное';

export function projectColor(projectId?: string | null): ProjectColor {
  if (!projectId) return NO_PROJECT_COLOR;
  let hash = 0;
  for (let i = 0; i < projectId.length; i++)
    hash = (hash * 31 + projectId.charCodeAt(i)) | 0;
  return PROJECT_PALETTE[Math.abs(hash) % PROJECT_PALETTE.length];
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
