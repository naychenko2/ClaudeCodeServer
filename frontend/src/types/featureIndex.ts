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

import type { FeatureRun, FeatureState } from './featureRun';
import type { FeatureStats } from './featureStats';
import type { FeaturePlan, FeatureMockup } from './featurePlans';
import type { PlanState } from '../lib/featureProgress';
// Чат фичи (GET features/{id}, chats[]) — из featureChats; алиас, чтобы не путать с
// FeatureChatLink этого файла (запись индекса «чат × фича», бэк: FeatureIndexChatEntry).
import type { FeatureChatLink as FeaturePageChatLink } from './featureChats';
import type { FeatureBrief } from '../lib/featurePage';

// Группа фичи (контракт §1): attention — требует внимания; running — идёт;
// idle — стоит; closed — закрыта. Считается той же функцией, что и порядок.
export type FeatureBucket = 'attention' | 'running' | 'merged' | 'idle' | 'closed';

// Состояние стадии рельсы — словарь бэка `FeatureStage.State` один в один
// (контракт: `pending`, а не `ahead` — переименование дало бы вторую точку правды).
// `skipped` — необязательная «design» без задач (W4-DES-B): остаётся skipped и у
// закрытой фичи (это не «пройден»).
export type FeatureRailState = 'done' | 'active' | 'pending' | 'absent' | 'skipped';
// Тон активной стадии (контракт: у закрытой фичи tone = null).
export type FeatureRailTone = 'run' | 'loop' | 'wait' | 'stopped';
// Восемь стадий конвейера в порядке жизненного цикла (контракт: «ровно 8»); design
// необязательна — без задач её сегмент skipped (W4-DES-B).
export type FeatureRailKey = 'cut' | 'design' | 'plan' | 'prod' | 'review' | 'qa' | 'final' | 'merge';

// Сегмент рельсы. У закрытой фичи `active` нет: стадия — `done` (задачи есть,
// пройдены) или `absent` (задач нет), tone = null (не `pending` — всё завершено).
export interface FeatureRailSegment {
  key: FeatureRailKey;
  state: FeatureRailState;
  tone: FeatureRailTone | null;
}

// Чипы-фильтры (контракт §1, «chips принят»): фронт фильтрует по chips, а не по attention.
// open = bucket ≠ closed; waiting/stopped/merge = attention.kind; closed = bucket = closed;
// running = bucket = running (идёт, без тревоги).
export type FeatureChip = 'open' | 'waiting' | 'stopped' | 'merge' | 'closed' | 'running' | 'merged';

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
  // Id задачи тревоги (Б7: FeatureAttention.TaskId); null — нет.
  taskId: string | null;
}

// «Сейчас» фичи: одна InProgress-задача (контракт: в now0 добавлен stepRef —
// «сейчас: Ш2» без второго запроса).
export interface FeatureNow {
  personaId: string | null;
  // Стадия конвейера: cut | plan | prod | review | qa | final | merge.
  stage: string;
  stepRef: string | null;
}

// Итог смерженной фичи: есть при bucket=closed и merged (смержена, эпик открыт), иначе null.
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
  // Состояние фичи (FeatureRun.State, 9 значений; всегда отдаётся). Единая точка
  // правды: bucket = f(state, эпик Done) (merged при эпике Done → closed).
  state: FeatureState;
  // null — тревоги нет.
  attention: FeatureAttention | null;
  // Ровно 8 стадий в порядке жизненного цикла (design необязательна, без задач — skipped).
  rail: FeatureRailSegment[];
  chips: FeatureChip[];
  // Счётчики шагов: принятые (Done) / все.
  stepsAccepted: number;
  stepsTotal: number;
  // Честный прогресс (волна 3, Ф-pr): на ревью / в работе / не заведено (план есть,
  // задач нет), состояние плана и — вне знаменателя — снятые шаги и доработки без
  // привязки (индекс отдаёт те же числа, что страница фичи; бэк — коммит defd6cd19).
  // Аддитивные: старые ответы их не несут.
  stepsReview?: number;
  stepsWork?: number;
  stepsNotCreated?: number;
  planState?: PlanState;
  stepsWithdrawn?: number;
  unlinkedReworks?: number;
  // Число возвратов (Loops).
  loops: number;
  // null — фича не идёт.
  now0: FeatureNow | null;
  lastActivityAt: string | null;
  // При bucket=closed и merged (итог есть и у смерженной с открытым эпиком).
  summary: FeatureSummary | null;
  // Ключевой чат фичи (цепочка из контракта §1); null — не найден.
  keySessionId: string | null;
  // Ключевой чат в архиве (остаётся ключевым, помечается).
  keyArchived: boolean;
  // Проект ключевого чата; null — чат вне проектов.
  keyProjectId: string | null;
  // Оркестратор фичи (W4-1B): персона ключевого чата из снимка владельца; иначе —
  // запустившая исполнителя эпика; иначе — создавшая эпик; null — никого нет.
  // Аддитивное: старые ответы его не несут — UI обязан пережить отсутствие.
  orchestratorPersonaId?: string | null;
  // Чатов фичи в этом ответе (архивные включены).
  chatCount: number;
  // Аддитивные (волна 2, без хранения): старые ответы их не несут — UI обязан пережить
  // отсутствие (все четыре опциональны).
  // Ветка worktree эпика; null — эпик без worktree.
  branch?: string | null;
  // Момент создания эпика (ISO-строка).
  createdAt?: string | null;
  // Число задач фичи: эпик + дочерние.
  taskCount?: number;
  // «Сейчас»: уникальные PersonaId идущих задач, по убыванию старта; [] — фича не идёт.
  nowPersonaIds?: string[];
}

// Роль чата в фиче (контракт: «Роль чата принята и дополнена»). Сила связи:
// key > curator > orchestrator > executor; на паре «чат × фича» одна запись
// с сильнейшим видом.
// `key` — зарезервирован: бэк его в chats НЕ отдаёт (ADR-031 §1) — ключевой чат
// берётся из строки feature.keySessionId, а в chats он приходит как curator/orchestrator.
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

// ── Страница фичи (волна 2 «Обзор фич», разрез §2) ─────────────────────────────
//
// Событие ленты фичи: зеркало бэкового `FeatureEvent`
// (backend/ClaudeHomeServer.Tasks/Projector/FeatureEvents.cs). Один факт из жизни
// фичи, выведенный из полей TaskItem/витков без хранилища. Контракт:
// { at, kind, taskId, role?, stepRef?, personaId?, sessionId?, round?, reason? }.
// Все поля nullable-терпимы: UI обязан пережить отсутствие любого.
export interface FeatureEvent {
  // Момент события (UTC, ISO-строка): CreatedAt / ClaudeStartedAt /
  // ExecutorStoppedAt / AwaitingHumanSince / DroppedByHumanAt / CompletedAt.
  at: string;
  // Вид: created | started | stopped | waiting | accepted | accepted_with_remarks |
  // returned | withdrawn | completed | dropped | merged | closed.
  kind: string;
  // Id задачи, к которой относится событие.
  taskId: string;
  // Роль задачи: feature · cut · plan · step · rework · review · qa · final · merge · consult.
  role?: string | null;
  // Код шага («Ш1», «D1c»); у step/rework, иначе null.
  stepRef?: string | null;
  // Исполнитель (PersonaId задачи); null — без персоны.
  personaId?: string | null;
  // Чат исполнителя (task.LinkedSessionId); наружу только если в снимке сессий
  // владельца, иначе null (fail-closed).
  sessionId?: string | null;
  // Номер витка (1-based); только у вердиктов, у остальных null.
  round?: number | null;
  // Причина (≤200): ExecutorStopReason / AwaitingHumanReason / Verification.Notes; иначе null.
  reason?: string | null;
}

// Ответ страницы фичи `GET /api/tasks/features/{featureId}` (волна 2 «Обзор фич»):
// агрегат `FeatureRun` одной фичи + лента событий.
//   run        — полный агрегат (Project, без новой арифметики);
//   events     — лента событий, обрезанная по `eventsLimit` (по умолчанию 100);
//   eventsTotal — общее число событий ДО обрезки (лента может быть короче).
// Новые поля (волна 3) ОПЦИОНАЛЬНЫ: бэк подтягивается позже, старые ответы их не
// несут — UI обязан пережить отсутствие (tsc не ломается на моках).
export interface FeaturePage {
  run: FeatureRun;
  events: FeatureEvent[];
  eventsTotal: number;
  // Чаты фичи (Б4): id/вид/задача/архив/исполнитель/модель; null — бэк ещё не отдаёт.
  chats?: FeaturePageChatLink[];
  // Статистика (таймлайн/разбивка/«дольше всех»/расход); null — не считалась.
  stats?: FeatureStats;
  // Планы (role=plan/cut/consult); null — нет.
  plans?: FeaturePlan[];
  // Макеты; null — нет.
  mockups?: FeatureMockup[];
  // 'device' — макеты недоступны (нет устройства); null — доступны.
  mockupsUnavailable?: 'device';
  // Сводка состояния фичи (волна 6, разрез п. 6). Аддитивное: старые ответы его
  // не несут — UI обязан пережить отсутствие (блок «Сводка» тогда не показывается).
  brief?: FeatureBrief;
}
