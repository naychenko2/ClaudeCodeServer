// Индекс фич — зеркало бэкового `FeatureIndex` (контракт утверждён в
// «Контракт индекса фич — утверждён (Александр, 18.10)»): ответ
// REST `GET /api/tasks/features/index?projectId=…`.
//
// Источник правды — БЭК: проектор считает bucket, порядок (OrderRuns), членство
// и ключевой чат. Фронт НЕ пересортировывает и НЕ пересчитывает — только
// показывает то, что отдал бэк.
//
// Все поля аддитивны и nullable-терпимы: UI обязан пережить отсутствие любого
// (старые ответы не несут новых полей).

// Группа фичи (контракт §1): attention — требует внимания; running — идёт;
// idle — стоит; closed — закрыта. Считается той же функцией, что и порядок.
export type FeatureBucket = 'attention' | 'running' | 'idle' | 'closed';

// Состояние стадии рельсы — словарь бэка `FeatureStage.State` один в один
// (контракт: `pending`, а не `ahead` — переименование дало бы вторую точку правды).
export type FeatureRailState = 'done' | 'active' | 'pending' | 'absent';
// Тон активной стадии (контракт: у закрытой фичи tone = null).
export type FeatureRailTone = 'run' | 'loop' | 'wait' | 'stopped';
// Семь стадий конвейера в порядке жизненного цикла (контракт: «ровно 7»).
export type FeatureRailKey = 'cut' | 'plan' | 'prod' | 'review' | 'qa' | 'final' | 'merge';

// Сегмент рельсы. У закрытой фичи `active` нет: стадия — `absent` (задач нет)
// или `pending` (задачи есть), tone = null.
export interface FeatureRailSegment {
  key: FeatureRailKey;
  state: FeatureRailState;
  tone: FeatureRailTone | null;
}

// Чипы-фильтры (контракт §1, «chips принят»): фронт фильтрует по chips, а не по attention.
// open = bucket ≠ closed; waiting/stopped/merge = attention.kind; closed = bucket = closed.
export type FeatureChip = 'open' | 'waiting' | 'stopped' | 'merge' | 'closed';

// Тревога фичи (контракт: `attention.kind` расширен значением `merge` —
// маппинг сырой строки «мерж» делается на бэке, на фронт уходит только kind).
export type FeatureAttentionKind = 'stopped' | 'wait' | 'merge';

export interface FeatureAttention {
  kind: FeatureAttentionKind;
  // С какого момента: ExecutorStoppedAt / AwaitingHumanSince / CompletedAt финала.
  // Бывает null (контракт §4.7) — фронт не должен падать.
  since: string | null;
  // Причина: ExecutorStopReason / AwaitingHumanReason; у merge всегда null.
  reason: string | null;
  // Исполнитель (PersonaId задачи).
  personaId: string | null;
  // Связанный чат исполнителя (LinkedSessionId задачи, прошёл фильтр владельца).
  sessionId: string | null;
}

// «Сейчас» фичи: одна InProgress-задача (контракт: в now0 добавлен stepRef —
// «сейчас: Ш2» без второго запроса).
export interface FeatureNow {
  personaId: string | null;
  // Стадия конвейера: cut | plan | prod | review | qa | final | merge.
  stage: string;
  stepRef: string | null;
}

// Итог смерженной фичи: только при bucket=closed, иначе null.
export interface FeatureSummary {
  // Момент мержа: CompletedAt merge-задачи; null — мержа нет.
  mergedAt: string | null;
  durationMinutes: number;
}

// Строка фичи в индексе.
export interface FeatureIndexFeature {
  featureId: string;
  title: string;
  // Проект ЭПИКА; null — эпик вне проектов.
  projectId: string | null;
  bucket: FeatureBucket;
  // null — тревоги нет.
  attention: FeatureAttention | null;
  // Ровно 7 стадий в порядке жизненного цикла.
  rail: FeatureRailSegment[];
  chips: FeatureChip[];
  // Счётчики шагов: принятые (Done) / все.
  stepsAccepted: number;
  stepsTotal: number;
  // Число возвратов (Loops).
  loops: number;
  // null — фича не идёт.
  now0: FeatureNow | null;
  lastActivityAt: string | null;
  // Только при bucket=closed.
  summary: FeatureSummary | null;
  // Ключевой чат фичи (цепочка из контракта §1); null — не найден.
  keySessionId: string | null;
  // Ключевой чат в архиве (остаётся ключевым, помечается).
  keyArchived: boolean;
  // Проект ключевого чата; null — чат вне проектов.
  keyProjectId: string | null;
  // Чатов фичи в этом ответе (архивные включены).
  chatCount: number;
}

// Роль чата в фиче (контракт: «Роль чата принята и дополнена»). Сила связи:
// key > curator > orchestrator > executor; на паре «чат × фича» одна запись
// с сильнейшим видом.
export type FeatureChatKind = 'key' | 'curator' | 'orchestrator' | 'executor';

// Связь «чат × фича» (запись в chats).
export interface FeatureChatLink {
  featureId: string;
  kind: FeatureChatKind;
  // Только у executor: Role задачи Session.TaskId.
  taskRole: string | null;
  stepRef: string | null;
  lane: string | null;
}

// Ответ `GET /api/tasks/features/index`.
//   features[] — в порядке OrderRuns (фронт не пересортировывает);
//   chats      — по сессиям области ответа; chats[id][] упорядочен по силе
//                (key > curator > orchestrator > executor), при равенстве —
//                по порядку фичи в features[].
export interface FeaturesIndex {
  features: FeatureIndexFeature[];
  chats: Record<string, FeatureChatLink[]>;
}
