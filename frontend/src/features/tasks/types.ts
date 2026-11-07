// Типы домена «Задач»: «Колонка доски проекта» (переехали из types/index.ts, S5-P9) и «Резюме фичи» (хвост S5-P, вместе с методом из api.ts). types/index.ts ре-экспортирует их через `export type *`, lib/api.ts — FeatureSummaryResult именованным реэкспортом: внешние импорты не меняются.

import type { TaskStatus } from '../../types';

// Колонка Kanban-доски проекта. category — семантическая категория статуса
// (за ней recurrence/календарь/Claude/MCP); несколько колонок могут делить категорию.
export interface BoardColumn {
  id: string;
  name: string;
  category: TaskStatus;   // 'todo' | 'inProgress' | 'done'
  color?: string;
}

// Резюме фичи (W5-F2, «Точка 6 · резюме»): результат модельного анализа хронологии.
// text — markdown с разделами «Сделано», «Где застряло», «Дальше» (или голые пункты
// без заголовков — пока так просит промпт feature-summary); model=null — локальная
// модель; cached — результат взят из кэша бэка, без нового прогона. Ошибки: 502 —
// модель не ответила (причина в error), 503 — подсистема выключена, 404 — фичи нет.
export interface FeatureSummaryResult {
  text: string;
  generatedAt: string;
  model: string | null;
  cached: boolean;
  usage?: unknown;
  // Число событий хронологии, на которых построено резюме (W4-ANB): для шапки
  // «по N событиям» (бейдж устаревания — без числа). Бэк отдаёт всегда; optional —
  // защита от ответов старой версии сервера.
  eventCount?: number;
}

// Форма ChatRef (бэк Models/ChatRef.cs, шаг S2d-4b, К5): ссылка на чат, связанный с задачей.
// Видимый чат — id и название остаются в своих id-полях задачи (linkedSessionId и др.),
// этой формы нет. Чужой приватный (не виден зрителю) — id-поле обнулено (null), рядом
// стоит эта запись: sessionId нет, title — подпись вместо названия, private — признак.
export interface ChatRef {
  sessionId?: string | null;
  title?: string | null;
  private?: boolean;
  authorUserId?: string | null;   // чей чат: id автора, не название и не id чата
}
