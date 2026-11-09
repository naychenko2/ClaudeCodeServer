// Ответ GET /api/admin/power/status: доступно ли управление питанием машины и не
// запланировано ли уже действие (его видят все окна, а не только то, из которого нажали)
export interface PowerPending {
  action: PowerActionKind;
  runAt: string;
  secondsLeft: number;
  requestedBy: string;
}

export type PowerActionKind = 'shutdown' | 'restart' | 'sleep';

export interface PowerState {
  enabled: boolean;
  available: boolean;
  delaySeconds: number;
  pending: PowerPending | null;
}

// Пульт удалённых команд (docs/research/remote-commands-plan.md §7). Список действий
// объявлен в конфиге сервера на диске; по HTTP ездит только `key`, тексты команд наружу
// не отдаются никогда.
export type RemoteCommandMode = 'oneshot' | 'daemon';

// `unknown` — «проверить не удалось» (команду не запустить, таймаут, опечатка в конфиге),
// а НЕ «остановлен»: у пульта это два разных факта, и путать их нельзя.
export type RemoteCommandState = 'running' | 'stopped' | 'unknown';

export interface RemoteCommandAction {
  key: string;
  title: string;
  mode: RemoteCommandMode;
  state: RemoteCommandState;
  busy: boolean;
  // null — до первой проверки: после рестарта сервера кэш статуса пуст, и это честно видно
  checkedAt: string | null;
  lastExitCode: number | null;
  // Ссылка «Открыть» из конфига действия (http/https) — например, vscode.dev/tunnel/имя
  // для туннеля VS Code. Null — кнопки нет.
  url: string | null;
}

export interface RemoteCommandsState {
  enabled: boolean;
  actions: RemoteCommandAction[];
}

// Ответ start/stop/refresh. `busy` приходит от refresh при чужой идущей операции (он не
// берёт gate и отдаёт кэш), `detail` — короткая причина и хвост вывода при провале.
export interface RemoteCommandResult {
  ok: boolean;
  state: RemoteCommandState;
  busy?: boolean;
  detail?: string | null;
  checkedAt?: string | null;
  lastExitCode?: number | null;
}

// --- Бэкапы (админский раздел) (переехали из types/index.ts, S5-P4) ---

export interface BackupSummary {
  chats: number;
  personas: number;
  tasks: number;
  notes: number;
  projects: number;
  users: number;
  totalBytes: number;
}

export interface BackupEntry {
  fileName: string;
  createdAt: string;
  size: number;
  summary: BackupSummary;
}

export interface BackupStatus {
  // Всё из секции Backup конфига — UI их не меняет, только показывает
  enabled: boolean;
  // Куда архивы ложатся фактически (при пустом Backup:Path — папка по умолчанию)
  effectivePath: string;
  secretsPath: string;
  intervalHours: number;
  lastSuccessAt: string | null;
  lastError: string | null;
  lastAttemptAt: string | null;
  recent: BackupEntry[];
}

