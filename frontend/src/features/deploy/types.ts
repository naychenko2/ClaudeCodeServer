// Итог выкатки, как его пишет трей-раннер в deploy-status.json. Формат чужой — читаем как есть.
// result: running | ok | blocked | build-failed | rolled-back | failed | error.
// Времена — локальные строки «yyyy-MM-dd HH:mm:ss» без смещения.
export interface DeployStatusFile {
  startedAt: string | null;
  finishedAt: string | null;
  mode: string | null;
  branch: string | null;
  dirtyFiles: number;
  head: string | null;
  deployExitCode: number | null;
  result: string | null;
  productUp: boolean | null;
  note: string | null;
}

// Ответ GET /api/admin/deploy/status: доступность выкатки плюс последний известный итог
export interface DeployState {
  enabled: boolean;
  canLaunch: boolean;
  reason: string | null;
  status: DeployStatusFile | null;
  // Ход идущей выкатки (deploy-progress.json раннера): null — выкатка не идёт или раннер
  // хода не пишет. Пока продукт жив (идёт сборка), окно рисует по нему полосу.
  progress?: DeployProgressFile | null;
}

// Ход выкатки. stage: checking | building | publishing | restarting | done; percent — 0..100
// по весам шагов скрипта деплоя, step — подпись текущего шага, line — последняя строка вывода
export interface DeployProgressFile {
  startedAt: string | null;
  stage: string | null;
  percent: number;
  step: string | null;
  line: string | null;
  updatedAt: string | null;
}

// Журнал выкатки ИЗ ЧАТА (ADR-010) — другая механика, чем трей-раннер выше: заявку
// исполняет внешний агент планировщика, а журнал deploy-state.json пишет он же.
// Формат чужой и версионируется отдельно от сервера: незнакомые поля игнорируем,
// отсутствующие переживаем (см. Services/Deploy/DeployState.cs).
export interface DeployJournalStep {
  name: string;
  status?: string | null;   // ok | fail | running… — словарь агента, читаем как есть
  ms: number;
}

export interface DeployJournalResult {
  ok: boolean;
  // succeeded | rolled_back | failed — дублирует финальную фазу
  status: string;
  message?: string | null;
  releaseId?: string | null;
  finishedAt?: string | null;
}

export interface DeployJournalRecord {
  id: string;
  kind?: string | null;     // deploy | rollback
  // queued → building → switching → verifying → succeeded | rolled_back | failed
  phase: string;
  ref?: string | null;
  sha?: string | null;
  dirty?: boolean;
  dirtyFiles?: string[];
  initiatedBy?: { userId?: string | null; sessionId?: string | null } | null;
  steps?: DeployJournalStep[];
  result?: DeployJournalResult | null;
  startedAt?: string | null;
}

export interface DeployJournalRelease {
  id: string;
  sha?: string | null;
  path?: string | null;
  createdAt?: string | null;
}

// Ответ GET /api/deploy/status
export interface DeployJournal {
  enabled: boolean;
  current: DeployJournalRecord | null;
  history: DeployJournalRecord[];
  releases: DeployJournalRelease[];
}
