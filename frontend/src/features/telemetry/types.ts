// Типы домена «Инциденты телеметрии» (переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// === Инциденты телеметрии (вкладка «Инциденты» раздела «Телеметрия», только админ) ===

// Состояние сбора: ok — данные собраны; notConfigured — телеметрия выключена
// (это НЕ «всё тихо»); unavailable — SigNoz не ответил.
export type IncidentStatus = 'ok' | 'notConfigured' | 'unavailable';

export interface IncidentSummary {
  fingerprint: string;
  title: string;
  description?: string | null;
  severity?: string | null;
  environment?: string | null;
  startedAt?: string | null;
  resolvedAt?: string | null;
  isFiring: boolean;
  // Заглушён человеком: виден в списке, но не идёт в счётчик и не будит push
  isMuted?: boolean;
}

export interface IncidentBreakdownRow {
  label: string;
  count: number;
}

export interface IncidentTurn {
  traceId: string;
  chatId?: string | null;
  at?: string | null;
  model?: string | null;
  provider?: string | null;
  errorType?: string | null;
  durationMs: number;
}

export interface IncidentLogLine {
  at?: string | null;
  severity: string;
  message: string;
}

export interface IncidentChat {
  chatId: string;
  projectId?: string | null;
  title?: string | null;
  failures: number;
  totalTokens: number;
  mcpFailures: string[];
  // Чат назвал сам алерт (правила с разрезом по chat_id): попадает в список даже
  // без упавших ходов — они там успешные, просто долгие.
  fromAlert?: boolean;
}

export interface IncidentDossier {
  incident: IncidentSummary;
  status: IncidentStatus;
  from: string;
  to: string;
  // Алерт другого контура: локальных чатов по нему нет и быть не может
  isForeignEnvironment: boolean;
  breakdownTag: string;
  breakdown: IncidentBreakdownRow[];
  turns: IncidentTurn[];
  turnsTotal: number;
  logs: IncidentLogLine[];
  logsTotal: number;
  chats: IncidentChat[];
  // Путь правила ВНУТРИ SigNoz (/alerts/overview?ruleId=…). Относительный:
  // базой служит проброс /telemetry-proxy, а не localhost бэкенда.
  rulePath?: string | null;
}

export interface IncidentListResponse {
  status: IncidentStatus;
  items: IncidentSummary[];
}
