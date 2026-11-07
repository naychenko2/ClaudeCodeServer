// Типы домена «Колонка доски проекта» (переехали из types/index.ts, S5-P9). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

import type { TaskStatus } from '../../types';

// Колонка Kanban-доски проекта. category — семантическая категория статуса
// (за ней recurrence/календарь/Claude/MCP); несколько колонок могут делить категорию.
export interface BoardColumn {
  id: string;
  name: string;
  category: TaskStatus;   // 'todo' | 'inProgress' | 'done'
  color?: string;
}
