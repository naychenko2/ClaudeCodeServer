// Чаты фичи (волна 3 «Обзор фич») — ТОЛЬКО в ответе `GET /api/tasks/features/{id}`
// (поле `chats`). Отличается от `FeatureChatLink` в `featureIndex.ts` (тот — запись
// индекса «чат × фича», бэк: `FeatureIndexChatEntry`): здесь — чат из снимка владельца,
// привязанный к фиче (бэк: `FeatureChat`), с id чата, архивом, исполнителем, моделью.
//
// kind: key | curator | orchestrator | executor — те же виды, что в индексе.
// taskRole/stepRef/lane — сырые поля задачи (подпись роли строит фронт, roleLabel).
// modelLabel/modelFull — короткая подпись и полное имя модели («Авто → {модель} (слот …)»).

// Вид связи чата с фичей: key > curator > orchestrator > executor (сила связи).
export type FeatureChatKind = 'key' | 'curator' | 'orchestrator' | 'executor';

// Чат фичи (GET features/{id}, chats[]): один чат из снимка владельца, привязанный к фиче.
export interface FeatureChatLink {
  // Id чата.
  id: string;
  // Вид связи: key | curator | orchestrator | executor.
  kind: FeatureChatKind;
  // Id задачи (Session.TaskId); null — нет.
  taskId: string | null;
  // true — чат в архиве.
  archived: boolean;
  // Исполнитель (PersonaId); null — нет.
  personaId: string | null;
  // Роль задачи как есть (step/rework/review/qa/final/merge/cut/plan/consult); null — нет.
  taskRole: string | null;
  // Код шага (у step/rework); null — нет.
  stepRef: string | null;
  // Линия как есть (back/front/…); null — нет.
  lane: string | null;
  // Короткая подпись модели; null — нет.
  modelLabel: string | null;
  // Полное имя модели; null — нет.
  modelFull: string | null;
}
