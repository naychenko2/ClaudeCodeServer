// Типы домена «Аналитика расхода токенов» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ===== Аналитика расхода токенов (Spend Analytics v2, /api/spend/*) =====

// Токены везде объектом — форма ответа бэкенда (docs/architecture/spend-analytics-api.md)
export interface SpendTokens {
  input: number;
  output: number;
  cacheRead: number;
  cacheCreation: number;
  total: number;
}

// Токены дня по ценовым классам моделей. Класс определяет бэк (S1): фронт
// красит по ключу и НЕ знает, какая модель какого класса.
export interface SpendTierCounts {
  free: number;
  cheap: number;
  expensive: number;
}

// Точка дневного ряда обзора/виджета; aggregated — день свёрнут (за детальным окном)
export interface SpendDayPoint {
  date: string;
  aggregated: boolean;
  total: number;
  bySource: Record<string, number>;
  // Раскладка дня по ценовым классам (бэк кладёт не везде — старые ответы без поля)
  byTier?: SpendTierCounts;
  falGenerations: number;
}

// Строка карточки-топа обзора и узел pivot-дерева (одна форма у бэкенда)
export interface SpendCardRow {
  key: string;               // id значения; "" — узел «без значения»
  name: string | null;       // null — сущность удалена
  meta: string | null;       // у чатов: "chat" | "task"
  tokens: SpendTokens;
  turns: number;
  falGenerations: number;
  // Ценовой класс модели строки (free|cheap|expensive). Заполнен для разрезов
  // «модель» и «провайдер», у прочих — null: класса у них нет
  tier?: string | null;
}

export interface SpendOverviewResponse {
  from: string;
  to: string;
  detailDays: number;
  windowStart: string;
  allUsers: boolean;
  totals: SpendTokens;
  turns: number;
  falGenerations: number;
  // Оценка объёма по тарифам API за период (сумма SpendRecord.CostUsd), $.
  // «Модели и расход» → плитка «По тарифам API»: бэк отдаёт не всегда —
  // без поля плитка просто не рисуется.
  costUsd?: number | null;
  // Расход на сервисы Яндекса за период (озвучка SpeechKit): рубли и число оплаченных
  // запросов. null — таких трат не было; с долларами и токенами НЕ складывается — другая валюта
  rub?: { total: number; requests: number } | null;
  byDay: SpendDayPoint[];
  cards: {
    // Разрез по пользователям бэк кладёт только в ответ scope=all — в scope=mine поля нет вовсе
    users?: SpendCardRow[];
    projects: SpendCardRow[];
    models: SpendCardRow[];
    chats: SpendCardRow[];
    personas: SpendCardRow[];
    sources: SpendCardRow[];
    providers: SpendCardRow[];
  };
  topTurns: SpendTurnDto[];
}

export interface SpendPivotNode extends SpendCardRow {
  hasDetail: boolean;        // false — узел целиком из дневных агрегатов (🔒 ходы недоступны)
}

export interface SpendPivotResponse {
  nodes: SpendPivotNode[];
}

// Замер размера постановки задачи по секциям (один запуск исполнителя).
// Только размеры в символах — содержимого постановки и заметок здесь нет by design.
export interface SpendTaskPromptRun {
  at: string;
  taskId: string;
  ownerId: string;
  projectId: string | null;
  sessionId: string | null;
  personaId: string | null;
  totalChars: number;
  totalTokensEst: number;
  task: number;
  expected: number;
  tools: number;
  rules: number;
  restrictions: number;
  delegation: number;
  omo: number;
  context: number;
  notes: number;
}

export interface SpendTaskPromptResponse {
  runs: SpendTaskPromptRun[];
}

export interface SpendTurnDto {
  id: string;
  timestamp: string;
  ownerId: string;
  userName: string | null;
  sessionId: string | null;
  chatName: string | null;
  projectId: string | null;
  projectName: string | null;
  taskId: string | null;
  taskTitle: string | null;
  personaId: string | null;
  personaName: string | null;
  provider: string | null;
  model: string | null;
  source: string;            // chat-turn | one-shot | fal | free
  label: string | null;      // подпись one-shot действия или endpoint fal
  tier?: string | null;      // ценовой класс модели хода (free|cheap|expensive)
  tokens: SpendTokens;
  generations: number;
  durationMs: number | null;
  own: boolean;              // ход текущего пользователя → можно «Открыть чат»
}

export interface SpendTurnsResponse {
  total: number;
  windowClamped: boolean;    // в периоде были свёрнутые дни — их ходы недоступны
  items: SpendTurnDto[];
}

export interface SpendTurnDetailResponse {
  turn: SpendTurnDto;
  neighbors: { id: string; timestamp: string; total: number }[];
}

export interface SpendWidgetResponse {
  today: SpendTokens;
  week: SpendTokens;
  todayTurns: number;
  weekTurns: number;
  weekFalGenerations: number;
  byDay: SpendDayPoint[];
}

export interface SpendBadgeResponse {
  sessionId: string;
  total: SpendTokens;
  turns: number;
  lastTurn: SpendTurnDto | null;
}

