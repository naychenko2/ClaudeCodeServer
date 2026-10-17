// Модель фичи для режима «Исполнение фичи» в «Плане хода».
//
// Источник правды — БЭК: агрегат `FeatureRun` считает `FeatureProjector.Build`
// (backend/ClaudeHomeServer.Tasks/Projector/FeatureRun.cs) и приходит готовым из
// REST `GET /api/tasks/features?sessionId=…`. Фронт НЕ пересчитывает конвейер —
// только отображает. Ниже — зеркало полей бэкового агрегата (camelCase, как
// сериализует MVC); задачи внутри — подмножество `TaskItem`, которое несёт ответ.
//
// Что считать источником: бэк. `progress` — `double 0..1` (доля решённых витков),
// `verdicts.loops` — СЧЁТЧИК (`returned + acceptedWithRemarks`), не boolean:
// UI-условие «были правки» = `loops > 0`.

// Задача в агрегате фичи: подмножество `TaskItem` бэка (camelCase), которое
// использует карточка. Все поля аддитивны и nullable — UI обязан пережить
// отсутствие любого (старые задачи без разметки не несут feature-полей).
export interface FeatureTask {
  id: string;
  title: string;
  status?: 'todo' | 'inProgress' | 'done' | null;
  kind?: 'task' | 'defect' | null;
  // Роль в конвейере: feature · cut · plan · step · rework · review · qa · final · merge · consult.
  role?: string | null;
  featureId?: string | null;           // id эпика
  stepRef?: string | null;            // код шага («Ш1», «D1c»)
  lane?: string | null;               // линия («бэк», «фронт», «тест»)
  reviewOf?: string[] | null;         // id задач, которые оценивает это ревью/проверка/финал
  worktreeBranch?: string | null;
  personaId?: string | null;
  createdAt?: string | null;
  completedAt?: string | null;
  awaitingHumanSince?: string | null;
  awaitingHumanReason?: string | null;
  verification?: { verdict?: string | null; notes?: string | null } | null;
}

// Один виток ревью/проверки/финала для отображения («пружина»). `verdict='running'`
// — виток открыт, вердикта ещё нет. Строится в `FeatureRunView` из бэковых задач.
export interface FeatureRound {
  n: number;
  verdict: string;   // 'accepted' | 'accepted_with_remarks' | 'returned' | 'withdrawn' | 'running'
  who?: string | null;
  at?: string | null;
  reason?: string | null;
  ghost?: boolean;
}

// Счётчики исходов витков (бэк: `FeatureVerdicts`). `loops` — СЧЁТЧИК
// (`returned + acceptedWithRemarks`), не boolean: «были правки» = `loops > 0`.
export interface FeatureVerdicts {
  accepted: number;
  acceptedWithRemarks: number;
  returned: number;
  withdrawn: number;
  loops: number;
}

// Агрегат фичи — зеркало бэкового `FeatureRun`. `epic`/`steps`/`reviews`/`qa`/
// `final`/`merge` — задачи трекера, сгруппированные проектором по роли;
// `verdicts`/`progress`/`ready` вычисляет бэк, фронт только показывает.
export interface FeatureRun {
  featureId: string;
  epic: FeatureTask;
  steps: FeatureTask[];
  reviews: FeatureTask[];
  qa: FeatureTask[];
  final: FeatureTask | null;
  merge: FeatureTask | null;
  awaitingHumanSince?: string | null;
  awaitingHumanReason?: string | null;
  verdicts: FeatureVerdicts;
  progress: number;   // доля решённых витков, 0..1
  ready: boolean;     // финал закрыт с accepted/accepted_with_remarks
}
