import type { BoardColumn, CreateTaskDto, Task, TaskPriority, TaskRecurrence, TaskRecurrenceType, TaskStatus, UpdateTaskDto } from '../types';
export declare function reloadTasks(): Promise<void>;
export declare function ensureTasksLoaded(): Promise<void>;
export declare function useTasks(): Task[];
export declare function getTaskById(id: string): Task | undefined;
export declare function tasksLoaded(): boolean;
export declare function createTask(projectId: string | null, dto: CreateTaskDto): Promise<Task>;
export declare function updateTask(taskId: string, dto: UpdateTaskDto): Promise<Task>;
export declare function deleteTask(taskId: string): Promise<void>;
export declare function upsertTaskLocal(task: Task): void;
export declare function taskHashUrl(task: Pick<Task, 'id' | 'projectId'>): string;
export declare function openTaskInSection(task: Pick<Task, 'id' | 'projectId'>): void;
export declare const STATUS_ORDER: TaskStatus[];
export declare const STATUS_LABEL: Record<TaskStatus, string>;
export declare const STATUS_DOT: Record<TaskStatus, string>;
export declare const PRIORITY_ORDER: TaskPriority[];
export declare const PRIORITY_LABEL: Record<TaskPriority, string>;
export declare const PRIORITY_COLOR: Record<TaskPriority, string>;
export declare const PRIORITY_FILL: Record<TaskPriority, boolean>;
export interface ProjectColor {
    main: string;
    soft: string;
}
export declare const NO_PROJECT_COLOR: ProjectColor;
export declare const NO_PROJECT_LABEL = "\u041B\u0438\u0447\u043D\u043E\u0435";
export declare function projectColor(projectId?: string | null): ProjectColor;
export declare function projectInitial(name: string): string;
export declare function toIsoDate(d: Date): string;
export declare function todayIso(): string;
export declare function addDaysIso(iso: string, days: number): string;
export declare function daysFromToday(dueDate: string): number;
export declare function dueLabel(dueDate: string): string;
export declare function isDueUrgent(task: Task): boolean;
export declare const RECURRENCE_TYPE_LABEL: Record<Exclude<TaskRecurrenceType, 'none'>, string>;
export declare function nextDueDate(currentIso: string, rule: TaskRecurrence): string | null;
export declare function expandRecurringTasks(tasks: Task[], fromIso: string, toIso: string): Task[];
export declare function recurrenceLabel(r: TaskRecurrence): string;
export declare const REMINDER_PRESETS: readonly [0, 15, 60, 1440];
export declare function reminderLabel(minutes: number): string;
import type { DefectOutcome, DefectRepro, TaskKind, TaskVerification } from '../types';
export type { TaskKind, DefectOutcome, DefectRepro, TaskVerification };
export declare const KIND_LABEL: Record<TaskKind, string>;
export declare const OUTCOME_LABEL: Record<DefectOutcome, string>;
export declare function isDeviceWaiting(task: Pick<Task, 'deviceWaitSince' | 'status'>): boolean;
export declare function deviceWaitDuration(sinceIso: string, now?: number): string;
export declare function executorStopText(reason: string | null | undefined, deviceName?: string | null): string;
export declare const REVIEW_COLUMN_ROLE = "review";
export declare class DefectRuleError extends Error {
    constructor(message: string);
}
export declare function isOpenDefect(task: {
    kind?: TaskKind;
    status?: TaskStatus;
}): boolean;
export declare function ensureNotClosedAtCreate(task: {
    kind?: TaskKind;
    status?: TaskStatus;
}): void;
export declare function ensureVerificationOnClose(task: {
    kind?: TaskKind;
    status?: TaskStatus;
    verification?: TaskVerification | null;
    outcome?: DefectOutcome | null;
}): void;
export declare function ensureReproOnReview(task: {
    kind?: TaskKind;
    repro?: DefectRepro | null;
}, targetColumn: {
    role?: string | null;
} | null | undefined): void;
export declare function computeClosedWithoutCheck(): DefectOutcome;
export declare function computeOrder(prev?: number, next?: number): number;
export declare function boardCardSort(a: Task, b: Task): number;
export declare const DEFAULT_BOARD_COLUMNS: BoardColumn[];
export declare function resolveColumns(columns?: BoardColumn[]): BoardColumn[];
export declare function columnColor(col: BoardColumn): string;
export declare function taskColumnKey(task: Task, columns: BoardColumn[]): string;
export type BoardGroupBy = 'none' | 'priority' | 'assignee' | 'project' | 'due';
export declare const BOARD_GROUP_LABEL: Record<BoardGroupBy, string>;
export interface BoardLane {
    key: string;
    label: string;
    color?: string;
    tasks: Task[];
}
export declare const ASSIGNEE_LABEL: Record<string, string>;
export declare function boardLanes(tasks: Task[], groupBy: BoardGroupBy, projectsById: Map<string, {
    name: string;
}>): BoardLane[];
