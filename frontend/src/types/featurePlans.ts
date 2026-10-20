// Планы и макеты фичи (волна 3 «Обзор фич») — ТОЛЬКО в ответе
// `GET /api/tasks/features/{id}` (поля `plans` и `mockups`). Источник правды — БЭК:
// проектор собирает планы (role=plan/cut/consult) и макеты (из шапки/linkedFiles);
// фронт только показывает. Все поля nullable-терпимы: UI обязан пережить отсутствие
// любого (старые ответы не несут plans/mockups).

// Роль плановой задачи: plan — план; cut — разрез; consult — консультация.
export type FeaturePlanRole = 'plan' | 'cut' | 'consult';

// План фичи: задача-план/разрез/консультация с её метаданными.
export interface FeaturePlan {
  // Id задачи-плана.
  taskId: string;
  // Роль: plan | cut | consult.
  role: FeaturePlanRole;
  // Название плана.
  title: string;
  // Автор (PersonaId); null — без автора.
  authorPersonaId: string | null;
  // Момент создания (ISO-строка).
  createdAt: string;
  // Момент завершения (ISO-строка); null — не завершён.
  completedAt: string | null;
  // true — у плана есть текст (тело).
  hasText: boolean;
  // Пути макетов, связанных с планом.
  mockupPaths: string[];
}

// Статус макета: draft — черновик; approved — принят; rejected — отклонён.
export type FeatureMockupStatus = 'draft' | 'approved' | 'rejected';

// Файл макета: путь + существует ли в дереве проекта.
export interface FeatureMockupFile {
  // Путь файла (от корня проекта).
  path: string;
  // true — файл существует в дереве проекта.
  exists: boolean;
}

// Макет фичи: путь, название, статус, файлы, источник и автор.
export interface FeatureMockup {
  // Путь макета (от корня проекта).
  path: string;
  // Название макета.
  title: string;
  // Статус; null — не задан.
  status: FeatureMockupStatus | null;
  // Файлы макета.
  files: FeatureMockupFile[];
  // Источник: header — из шапки; linkedFiles — из linkedFiles задачи.
  source: 'header' | 'linkedFiles';
  // Id задачи, к которой относится макет; null — не привязан.
  taskId: string | null;
  // Автор (PersonaId); null — без автора.
  authorPersonaId: string | null;
  // Момент изменения (ISO-строка); null — неизвестен.
  modifiedAt: string | null;
}
