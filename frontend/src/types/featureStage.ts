// Типы экрана этапа (D1c): зеркало серверных StageScreenService.cs (GET /api/tasks/{id}/stage и
// /stage/commits/{sha}/diff). Поля не досчитываются на фронте — всё приходит с бэка как есть.

import type { StageMetrics } from './featureStats';

// Слепок этапа на задаче (StageSnapshot.cs): промпт этапа отдельно, модель, ограничения, таймаут.
export interface StageSnapshot {
  templateId: string;
  templateVersion: number;
  role: string;
  stageId: string;
  lane?: string | null;
  after: string[];
  specialty?: string | null;
  personaId?: string | null;
  prompt?: string | null;
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
  defaultTier?: string | null;
  disallowedTools?: string[] | null;
  mcpAllow?: string[] | null;
  stallMinutes?: number | null;
  stallPolicy?: string | null;
}

export interface StageCommit {
  sha: string;
  shortSha: string;
  subject: string;
  at: string | null;
  files: string[];
  taskId: string;
  role: string | null;
  // Номер версии артефакта в цепочке шага и доработок (с 1)
  version: number;
}

export interface StageArtifacts {
  commits: StageCommit[];
  // false — дерево задачи недоступно или git не ответил: пустой список не значит «коммитов нет»
  commitsAvailable: boolean;
  linkedFiles: string[];
  resultMarkdown: string | null;
}

export interface StageToolCall {
  name: string;
  summary: string | null;
  at: string | null;
  isError: boolean;
}

export interface StageTranscript {
  prompt: string | null;
  filesRead: string[];
  toolCalls: StageToolCall[];
  toolCallsTotal: number;
  truncated: boolean;
}

export interface StageInput {
  stage: StageSnapshot | null;
  sessionId: string | null;
  transcriptAvailable: boolean;
  transcript: StageTranscript | null;
}

export interface StageReviewItem {
  taskId: string;
  title: string;
  role: string | null;
  status: string;
  verdict: string | null;
  returnReason: string | null;
  notes: string | null;
  blocker: boolean;
  verifiedAt: string | null;
}

export interface StageAwaitingHuman {
  since: string;
  reason: string | null;
}

export interface StageReview {
  own: StageReviewItem | null;
  reviews: StageReviewItem[];
  awaitingHuman: StageAwaitingHuman | null;
}

export interface StageScreen {
  taskId: string;
  featureId: string | null;
  role: string | null;
  stepRef: string | null;
  title: string;
  status: string;
  stage: StageSnapshot | null;
  artifacts: StageArtifacts;
  input: StageInput;
  review: StageReview;
  // null — у задачи нет фичи с эпиком: блок метрик не рисуется
  metrics: StageMetrics | null;
  // Вопросы человеку: сначала открытые, затем история (новые сверху); пусто — вопросов не было
  questions: StageQuestion[];
  // Сводка шапки этапа (B07): нужен только чат исполнителя; null/нет поля — кнопки «Чат исполнителя» нет
  stageSummary?: { executorSessionId: string | null } | null;
}

export interface StageAnswer {
  text: string;
  by: string | null;
  at: string;
}

export interface StageQuestion {
  id: string;
  status: 'open' | 'answered';
  askedBy: { personaId: string | null; name: string };
  askedAt: string;
  text: string;
  // Открытый — сколько ждёт до сих пор, отвеченный — сколько ждал до ответа
  waitedMinutes: number | null;
  answer: StageAnswer | null;
}

export interface StageCommitDiff {
  sha: string;
  subject: string;
  files: string[];
  diff: string;
  truncated: boolean;
  maxChars: number;
}
