// Модель фичи для режима «Исполнение фичи» в «Плане хода».
//
// Фича — это задача-эпик (role=feature) плюс её дочерние задачи в том же трекере.
// Агрегат `FeatureRun` считается ОДНОЙ чистой функцией (projector) из сырых задач:
// на бэке её считает `FeatureProjector.Build`, а фронт в демо-фазе (пока REST
// `GET /api/tasks/features` не готов) считает из фикстуры. Правила подсчёта держим
// в одном месте, чтобы «План хода» и «Пульт куратора» не разъехались.

// Роль задачи в конвейере фичи. Значения — строки-константы (без enum): бэк
// добавляет их аддитивно, старый код при чтении игнорирует незнакомые.
export type FeatureRole =
  | 'feature'   // эпик — сама фича
  | 'cut'       // разрез фичи
  | 'plan'       // план (макет/план шагов)
  | 'step'       // производственный шаг
  | 'rework'     // доработка шага после ревью/проверки
  | 'review'     // ревью (пачкой — reviewOf на несколько задач)
  | 'qa'          // проверка (Вера)
  | 'final'      // финальное ревью
  | 'merge'      // мерж
  | 'consult';   // архитектурная консультация

// Исход витка. `null` = старая семантика «проверено» (без вердикта).
export type Verdict = 'accepted' | 'accepted_with_remarks' | 'returned' | 'withdrawn';

// Сырая задача трекера в форме, в которой её несёт фикстура/REST. Всё, кроме
// id/title, — аддитивно и nullable: UI обязан пережить отсутствие любого поля.
export interface FeatureTask {
  id: string;
  title: string;
  persona?: string | null;
  status?: string | null;              // 'todo' | 'inProgress' | 'done'
  created?: string | null;             // 'YYYY-MM-DDTHH:MM'
  completed?: string | null;
  kind?: string | null;
  role?: FeatureRole | null;
  featureId?: string | null;           // id эпика
  worktreeBranch?: string | null;
  stepRef?: string | null;             // код шага («Ш1», «D1c»)
  lane?: string | null;                // линия («фронт», «бэк», «тест»)
  reviewOf?: string[] | null;          // id задач, которые оценивает ревью
  verification?: { verdict?: Verdict | null; notes?: string | null } | null;
  awaitingHumanSince?: string | null;
  awaitingHumanReason?: string | null;
}

// Один виток ревью/проверки/финала. `verdict='running'` — виток открыт, вердикта ещё нет.
export interface FeatureRound {
  n: number;
  verdict: Verdict | 'running';
  who?: string | null;
  at?: string | null;
  reason?: string | null;
  ghost?: boolean;                      // ревью вне трекера (виден только по доработке)
}

// Производственный шаг (role=step/rework) со своими витками.
export interface FeatureStep {
  id: string;                          // stepRef
  lane?: string | null;
  name: string;
  status: 'todo' | 'run' | 'await' | 'done';
  rounds: FeatureRound[];
  ret: number;                         // число витков «вернул/с правками»
  closed?: string | null;              // «снят с выкладки»
}

// Состояние фичи: идёт / ждёт человека / смержена / закрыта (спайк).
export type FeatureState = 'run' | 'wait' | 'done' | 'closed';

// Статус одной стадии конвейера.
//  none  — задач этой роли в трекере нет
//  todo  — стадия ещё не начата (нет задач, но ожидается)
//  run   — идёт
//  done  — завершена
//  wait  — ждёт человека (мерж)
//  loop  — вернула, но не закрыта (финал)
export type FeatureStageStatus = 'none' | 'todo' | 'run' | 'done' | 'wait' | 'loop';

// Стадия конвейера: разрез → план → производство → ревью → проверка → финал → мерж.
export interface FeatureStage {
  key: 'cut' | 'plan' | 'prod' | 'review' | 'check' | 'final' | 'merge';
  name: string;
  status: FeatureStageStatus;
  meta?: string | null;
  rounds?: FeatureRound[];   // витки стадии (ревью/проверка/финал)
}

// Агрегат фичи — то, что рисует карточка.
export interface FeatureRun {
  id: string;                          // id эпика
  name: string;                        // заголовок эпика
  branch?: string | null;
  kind?: string | null;
  lanes: { key: string; label: string }[];
  state: FeatureState;
  progress: { done: number; total: number };   // принято шагов / всего
  verdicts: { accepted: number; acceptedWithRemarks: number; returned: number; withdrawn: number };
  ret: number;                         // суммарные возвраты (уникальные)
  ready: boolean;                      // final.verdict ∈ {accepted, accepted_with_remarks}
  awaitingHumanSince?: string | null;
  awaitingHumanReason?: string | null;
  merged?: string | null;
  running: { who: string; title: string; stepId?: string }[];   // «Сейчас»
  steps: FeatureStep[];
  stages: FeatureStage[];
}
