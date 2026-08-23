import type { Me, Project, ProjectGroup, ProjectTag, Session, FileEntry, SyncMark, WorkflowAgentInfo, WorkflowAgentBlock, AppSettings, UserProfile, SkillsData, SkillInfo, RegistrySkill, SkillSuggestion, GeneratedSkill, PermissionRule, UsageResponse, FalAccountResponse, GlifAccountResponse, ImageGenerationSettings, ImageGenerationPatch, ImagePlacePatch, ProviderBalanceInfo, FeatureFlagDefinition, SystemPromptPart, Task, CreateTaskDto, UpdateTaskDto, BoardColumn, BoardItem, HomeSummaryResponse, ChangelogDay, DaySummaryStub, ChangelogStatus, NoteSummary, NoteDetail, NoteBacklink, NoteGraph, DocAnnotation, NoteReply, NoteSource, NoteFolder, NoteTemplate, NoteSemanticHit, CreateNoteDto, UpdateNoteDto, NoteTask, ExtractTasksResponse, SearchHit, Persona, CreatePersonaDto, UpdatePersonaDto, PersonaScope, PersonaMemoryType, PersonaMemoryEntry, PersonaMemoryHit, PersonaContract, PersonaWorkingFocus, PantheonTemplate, PersonaBinding, PersonaBindingDto, PersonaBindingType, BindingTarget, KnowledgeBaseDetail, KnowledgeSearchHit, CreateKnowledgeBaseDto, KnowledgeListResponse, KnowledgeDocumentContent, TeamMemoryEntry, TeamMemoryType, TeamMemberDraft, PersonaAutomationRule, AutomationRuleDto, ProjectService, LaunchConfigEntry, GitStatus, GitBranchInfo, GitLogEntry, GitCommitDetail, GitStashEntry, GitFileChange, GitBlameLine, GitRemoteInfo, GitCommitPromptInfo, SpendOverviewResponse, SpendPivotResponse, SpendTurnsResponse, SpendTurnDetailResponse, SpendWidgetResponse, SpendBadgeResponse, SpendTaskPromptResponse, BackupStatus, BackupSummary, CodeGraph, DocEntry, DocDetail, DocSearchHit, DocsScope, DocsScopeInfo, DocProperty, DocTypeSchema, PromptSnapshot, PromptSection, ReaderPage, ReaderErrorCode, SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtySettingsResponse, ResetResult, ModelPreviewResponse, PresetUsageResponse, PlacePresetRef, McpServer, McpBuiltinServer, McpServerUpsert, McpProbeResult, McpCallsResponse, McpOAuthStartResult, McpOAuthCompleteResult, DossierEntry, DesktopDevice, DesktopPairingCode, DesktopHandsChatStatus, BackgroundResult, ChangedBySession, IncidentListResponse, IncidentDossier } from '../types';
import { request } from './offline';

// Личные/админские слоты моделей: сильная/средняя/слабая.
// null = наследовать глобальный слот, string = override, "" = сброс к наследованию.
export interface ModelTiers {
  strong: string | null;
  medium: string | null;
  weak: string | null;
}

// Кандидат значка проекта (ADR-009 §2.2): name — имя lucide-иконки из белого списка.
// Сервер валидирует на входе; на фронте достаточно хранить как есть.
export interface GlyphCandidate {
  name?: string | null;
}


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

export type { WorkflowAgentInfo, WorkflowAgentBlock };

// Метаданные внешнего модуля из GET /api/modules (контракт §2/§7)
export interface ModuleInfo {
  id: string;
  displayName: string;
  description?: string | null;
  version: string;
  schemaVersion: string;
  apiBase: string;                 // "/api/modules/{id}"
  tab?: { label: string; icon?: string | null; order: number } | null;
  remoteEntry?: string | null;     // с ?v={version}
  exposedModule?: string | null;   // "./Tab"
}

export interface DifyDocument {
  id: string;
  name: string;
  indexingStatus: string;
  error?: string | null;   // текст ошибки индексации (только у indexingStatus === 'error')
  tags?: string[];
}

// Контекст worktree-чата для git-запросов: пока активен чат в отдельном git worktree,
// ВСЕ git-вызовы его проекта несут ?sessionId= — бэкенд переводит операции в дерево чата.
// Без контекста (или для другого проекта) запросы идут в корень проекта, как раньше.
// Выставляет ChatPanel по активной сессии; частичная передача сломала бы инвариант
// «коммит/дискард — в том же дереве, что и статус».
let gitSessionCtx: { projectId: string; sessionId: string } | null = null;
export function setGitSessionContext(projectId: string, sessionId: string | null) {
  gitSessionCtx = sessionId ? { projectId, sessionId } : null;
}
export function getGitSessionContext(): { projectId: string; sessionId: string } | null {
  return gitSessionCtx;
}
// Суффикс query для git-URL; sep — '?' для URL без параметров, '&' для URL с ними
function gq(projectId: string, sep: '?' | '&' = '?'): string {
  return gitSessionCtx?.projectId === projectId ? `${sep}sessionId=${gitSessionCtx.sessionId}` : '';
}

// Перезапись cc_token после смены пароля: старый токен сервер отозвал, а хранилище
// выбирает не вызывающий — оно задано галкой «запомнить меня» на входе (localStorage
// против sessionStorage). Пишем туда, где токен уже лежит, режим входа не меняем.
export function setStoredToken(token: string) {
  if (typeof window === 'undefined') return;
  if (localStorage.getItem('cc_token') !== null) localStorage.setItem('cc_token', token);
  else sessionStorage.setItem('cc_token', token);
}

// Projects
export const api = {
  auth: {
    login: (username: string, password: string) =>
      request<{ token: string; expiresAt: string; username: string; displayName?: string | null }>('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ username, password }),
      }),
    me: (opts?: { timeoutMs?: number }) => request<Me>('/auth/me', opts),
    // Возвращает свежий токен: смена пароля отзывает все прежние, включая текущий
    changePassword: (currentPassword: string, newPassword: string) =>
      request<{ token: string; expiresAt: string }>('/auth/password', {
        method: 'PUT',
        body: JSON.stringify({ currentPassword, newPassword }),
      }),
    // Пороги индикатора контекста (per-user); пустой body → сброс к дефолтам
    setContextThresholds: (t: { warnPct?: number; dangerPct?: number }) =>
      request<{ contextThresholds: { warnPct: number; dangerPct: number } | null }>('/auth/context-thresholds', {
        method: 'PUT',
        body: JSON.stringify(t),
      }),
    // Таймзона устройства (IANA) — серверу для расчёта напоминаний по локальным срокам
    setTimeZone: (timeZone: string) =>
      request<void>('/auth/timezone', {
        method: 'PUT',
        body: JSON.stringify({ timeZone }),
      }),
  },

  push: {
    vapidPublicKey: () => request<{ publicKey: string }>('/push/vapid-public-key'),
    subscribe: (sub: { endpoint: string; p256dh: string; auth: string }) =>
      request<void>('/push/subscribe', { method: 'POST', body: JSON.stringify(sub) }),
    unsubscribe: (endpoint: string) =>
      request<void>('/push/unsubscribe', { method: 'POST', body: JSON.stringify({ endpoint }) }),
  },

  users: {
    list: () => request<UserProfile[]>('/users'),
    create: (data: { username: string; password: string; role: string; executionEnvironment?: string }) =>
      request<UserProfile>('/users', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: string, data: { username?: string; role?: string; executionEnvironment?: string }) =>
      request<UserProfile>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/users/${id}`, { method: 'DELETE' }),
    resetPassword: (id: string, newPassword: string) =>
      request<void>(`/users/${id}/password`, { method: 'PUT', body: JSON.stringify({ newPassword }) }),
  },

  settings: {
    get: () => request<AppSettings>('/settings'),
    // Патч: присылаем только изменённые поля, остальные сервер оставляет как есть
    save: (s: Partial<AppSettings>) => request<AppSettings>('/settings', { method: 'PUT', body: JSON.stringify(s) }),
  },

  // Личные слоты моделей текущего пользователя (GET /api/me/model-tiers).
  // PATCH: null = не трогать, "" = очистить к наследованию, string = override.
  meModelTiers: {
    get: () => request<ModelTiers>('/me/model-tiers'),
    save: (patch: Partial<ModelTiers>) => request<ModelTiers>('/me/model-tiers', { method: 'PUT', body: JSON.stringify(patch) }),
  },

  // Админские слоты моделей любого пользователя (GET /api/admin/users/{id}/model-tiers).
  adminUserModelTiers: {
    get: (userId: string) => request<ModelTiers>(`/admin/users/${encodeURIComponent(userId)}/model-tiers`),
    save: (userId: string, patch: Partial<ModelTiers>) => request<ModelTiers>(`/admin/users/${encodeURIComponent(userId)}/model-tiers`, { method: 'PUT', body: JSON.stringify(patch) }),
  },

  usage: {
    get: () => request<UsageResponse>('/usage'),
  },

  // Аналитика расхода токенов (Spend Analytics v2). query — готовая строка
  // из spendQuery() (период/скоуп/фильтры), чтобы не дублировать сборку параметров.
  spend: {
    overview: (query: string) => request<SpendOverviewResponse>(`/spend/overview${query}`),
    pivot: (query: string) => request<SpendPivotResponse>(`/spend/pivot${query}`),
    turns: (query: string) => request<SpendTurnsResponse>(`/spend/turns${query}`),
    turn: (id: string) => request<SpendTurnDetailResponse>(`/spend/turns/${encodeURIComponent(id)}`),
    taskPrompt: (taskId: string) =>
      request<SpendTaskPromptResponse>(`/spend/tasks/${encodeURIComponent(taskId)}/prompt`),
    widget: () => request<SpendWidgetResponse>('/spend/widget'),
    badge: (sessionId: string) => request<SpendBadgeResponse>(`/spend/sessions/${encodeURIComponent(sessionId)}/badge`),
  },

  // Исполнитель фоновых действий — правит только админ (настройка серверная, общая для всех).
  // route: 'local' | 'claude' | id модели провайдера. Текущее состояние приходит в блоке
  // ollama ответа /usage.
  localActions: {
    setRoute: (key: string, route: string) =>
      request<{ key: string; route: string; source: string; preset?: PlacePresetRef | null }>(`/admin/local-actions/${key}`,
        { method: 'PUT', body: JSON.stringify({ route }) }),
    reset: (key: string) =>
      request<{ key: string; route: string; source: string; preset?: PlacePresetRef | null }>(`/admin/local-actions/${key}`,
        { method: 'DELETE' }),
    // Массовый автоподбор исполнителя всем действиям по пресету; актуальные маршруты фронт
    // затем перечитывает из /usage. preset: 'tiers' | 'tiers-local'.
    applyPreset: (preset: 'tiers' | 'tiers-local') =>
      request<{ preset: string; count: number }>(`/admin/local-actions/preset`,
        { method: 'POST', body: JSON.stringify({ preset }) }),
  },

  // Бэкапы — только админ (инстансная штука). Настройки правятся руками в секции Backup
  // конфига, отсюда только чтение статуса и ручной снимок. Восстановления тут нет: оно
  // требует остановленного сервера и живёт в CLI (exe --restore) и меню трея.
  backup: {
    get: () => request<BackupStatus>('/admin/backup'),
    run: () => request<{ file: string; createdAt: string; summary: BackupSummary }>(
      '/admin/backup/run', { method: 'POST' }),
  },

  fal: {
    account: (days = 7) => request<FalAccountResponse>(`/fal/account?days=${days}`),
  },

  glif: {
    // Агрегаты расхода приходят сразу за три окна (24ч/7д/30д) — параметра периода нет
    account: () => request<GlifAccountResponse>('/glif/account'),
  },

  // Генератор картинок инстанса ПО МЕСТАМ (иконка проекта, аватар персоны): у каждого
  // места свой режим auto|fal|glif и своя модель. Чтение открыто всем (диалоги
  // подписывают, кто и чем рисует), запись — админам. Патч места: поле не прислали —
  // оставить, "" — сброс к конфигу; место вне places не трогается.
  imageGeneration: {
    get: () => request<ImageGenerationSettings>('/image-generation'),
    save: (patch: ImageGenerationPatch) =>
      request<ImageGenerationSettings>('/image-generation', { method: 'PUT', body: JSON.stringify(patch) }),
    savePlace: (place: string, patch: ImagePlacePatch) =>
      request<ImageGenerationSettings>('/image-generation',
        { method: 'PUT', body: JSON.stringify({ places: { [place]: patch } }) }),
  },

  // Внешние модули (платформа): список включённых у юзера модулей для оболочки (R6).
  // Данные к самим модулям идут мимо этого API — через gateway /api/modules/{id}/** (YARP).
  modules: {
    list: () => request<{ items: ModuleInfo[] }>('/modules'),
  },

  // Личный реестр MCP-серверов владельца (фича mcp-registry). Секретные значения наружу
  // не выходят: в McpValue у секрета value = null, а в форме пустое значение секрета
  // означает «оставить как было».
  mcp: {
    list: () => request<McpServer[]>('/mcp/servers'),
    get: (id: string) => request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}`),
    // Встроенные серверы продукта — только наблюдение статуса, записи в реестре нет
    builtin: () => request<McpBuiltinServer[]>('/mcp/servers/builtin'),
    create: (data: McpServerUpsert) =>
      request<McpServer>('/mcp/servers', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: string, data: McpServerUpsert) =>
      request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(data) }),
    setEnabled: (id: string, enabled: boolean) =>
      request<McpServer>(`/mcp/servers/${encodeURIComponent(id)}/enable`, {
        method: 'POST', body: JSON.stringify({ enabled }),
      }),
    delete: (id: string) => request<void>(`/mcp/servers/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Разовая проверка «по кнопке»: рукопожатие + tools/list, результат едет и в стор статусов
    probe: (id: string) =>
      request<McpProbeResult>(`/mcp/servers/${encodeURIComponent(id)}/probe`, { method: 'POST' }),
    // Импорт фрагмента {"mcpServers": {...}} — записи заводятся выключенными
    import: (fragment: unknown) =>
      request<{ created: McpServer[]; skipped: { key: string; reason: string }[] }>('/mcp/servers/import', {
        method: 'POST', body: JSON.stringify(fragment),
      }),
    // Диагностика вызовов инструментов — только админ (данные всех владельцев)
    calls: (failures = 50) => request<McpCallsResponse>(`/mcp/calls?failures=${failures}`),
    // Вход по OAuth (волна 7): start отдаёт адрес окна провайдера, complete — запасной
    // путь с кодом, вставленным вручную (сервер принимает только loopback-адрес возврата)
    oauthStart: (id: string, clientId?: string) =>
      request<McpOAuthStartResult>(`/mcp/servers/${encodeURIComponent(id)}/oauth/start`, {
        method: 'POST', body: JSON.stringify(clientId ? { clientId } : {}),
      }),
    oauthComplete: (id: string, state: string, code: string) =>
      request<McpOAuthCompleteResult>(`/mcp/servers/${encodeURIComponent(id)}/oauth/complete`, {
        method: 'POST', body: JSON.stringify({ state, code }),
      }),
  },

  // Десктопный агент (ADR-008): устройства владельца, сопряжение и веб-половина сеанса рук.
  // Начать сеанс отсюда нельзя ни при каких условиях — эта дверь на самом устройстве,
  // веб-морда может только попросить (request) и остановить (handsStop).
  devices: {
    list: () => request<DesktopDevice[]>('/devices'),
    // Код сопряжения: 8 символов, живёт 5 минут, принадлежит ЭТОЙ веб-сессии
    startPairing: () => request<DesktopPairingCode>('/devices/pairing', { method: 'POST' }),
    pairingStatus: () => request<DesktopPairingCode>('/devices/pairing'),
    cancelPairing: () => request<void>('/devices/pairing', { method: 'DELETE' }),
    rename: (id: string, name: string) =>
      request<DesktopDevice>(`/devices/${encodeURIComponent(id)}`, {
        method: 'PATCH', body: JSON.stringify({ name }),
      }),
    // Отзыв: запись остаётся надгробием, токен устройства умирает немедленно
    revoke: (id: string) => request<void>(`/devices/${encodeURIComponent(id)}`, { method: 'DELETE' }),

    // Статус сеанса для бейджа «руки на …». Отдельный запрос, а не только событие ленты:
    // событие эфемерное, и после перезагрузки страницы бейдж погас бы при живых руках
    handsChat: (chatSessionId: string) =>
      request<DesktopHandsChatStatus>(`/devices/hands/chat/${encodeURIComponent(chatSessionId)}`),
    handsRequest: (chatSessionId: string) =>
      request<{ requested: boolean; active: boolean; requestedAt?: string }>(
        `/devices/hands/chat/${encodeURIComponent(chatSessionId)}/request`, { method: 'POST' }),
    handsStop: (chatSessionId: string) =>
      request<{ stopped: boolean }>(
        `/devices/hands/chat/${encodeURIComponent(chatSessionId)}/stop`, { method: 'POST' }),
  },

  providers: {
    balance: (key: string) => request<ProviderBalanceInfo>(`/providers/${key}/balance`),
    usage: (key: string) =>
      request<{
        balance: ProviderBalanceInfo | null;
        snapshots: { timestamp: string; balance: number; currency: string }[];
      }>(`/providers/${key}/usage`),
  },

  models: {
    list: () =>
      request<{
        models: { value: string; displayName: string; description?: string | null; provider?: string | null; contextWindow?: number | null; isCurated?: boolean }[];
        providers?: Record<string, import('./models').ProviderCapabilities>;
        // Резолвнутые модели агентных мест (ключ каталога → модель или null): по ним
        // пикеры подписывают пункт «По умолчанию (<модель>)»
        assignments?: Record<string, string | null>;
      }>('/models'),
    // Эффективный резолв для строки «Сейчас пойдёт» (считается той же кодовой дорогой,
    // что запуск хода — второй точки истины нет). sessionId вместе с personaId добавляет
    // в ответ subagentChip — чип модели на карточке персоны-сабагента.
    preview: (q: { place?: string; personaId?: string; specialty?: string; tier?: string; sessionId?: string }) => {
      const qs = new URLSearchParams();
      if (q.place) qs.set('place', q.place);
      if (q.personaId) qs.set('personaId', q.personaId);
      if (q.specialty) qs.set('specialty', q.specialty);
      if (q.tier) qs.set('tier', q.tier);
      if (q.sessionId) qs.set('sessionId', q.sessionId);
      const s = qs.toString();
      return request<ModelPreviewResponse>(`/models/preview${s ? `?${s}` : ''}`);
    },
    // Места, где выбран пресет (диалог удаления)
    presetUsage: (id: string) =>
      request<PresetUsageResponse>(`/models/presets/${encodeURIComponent(id)}/usage`),
  },

  // Выкатка боевого продукта трей-раннером (только админам и только при Deploy:Enabled).
  // status отвечает всегда, в том числе при выключенной фиче (enabled: false) — по нему
  // шапка решает, показывать ли пункт меню, и 404 здесь шумел бы в консоли у всех.
  deploy: {
    // live: true — НЕ подставлять ответ из офлайн-кэша (IndexedDB), даже когда сервер не
    // отвечает. Для этого запроса важно не только содержимое, но и сам факт ответа: продукт на
    // время публикации гаснет, и подставленный прошлый ответ выдавал бы «сервер отвечает,
    // ничего не происходит» — окно выкатки объявляло «трей команду не принял» поверх успешной
    // публикации. cache: 'no-store' закрывает то же самое со стороны браузера.
    status: () => request<DeployState>('/admin/deploy/status', { cache: 'no-store', live: true }),
    // 202: команда ушла трею. previousStartedAt — начало ПРОШЛОЙ выкатки: только по смене
    // этого времени видно, что трей команду принял и начал новую (см. DeployModal).
    launch: () => request<{ previousStartedAt: string | null }>('/admin/deploy', { method: 'POST' }),
  },

  // Журнал выкатки из чата (ADR-010) — за ним следит карточка хода выкатки в ленте.
  // live: true + no-store по той же причине, что и у трей-выкатки выше, и она здесь
  // ещё важнее: сервер во время переключения ГАСНЕТ намеренно, и подставленный из
  // офлайн-кэша прошлый ответ означал бы «прод отвечает, шаги не двигаются» — карточка
  // рапортовала бы о живом сервере ровно тогда, когда его нет.
  deployJournal: {
    status: () => request<DeployJournal>('/deploy/status', { cache: 'no-store', live: true }),
  },

  featureFlags: {
    get: () => request<{ definitions: FeatureFlagDefinition[]; values: Record<string, boolean> }>('/feature-flags'),
    set: (key: string, enabled: boolean) =>
      request<{ values: Record<string, boolean> }>(`/feature-flags/${key}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
  },

  // Специальности персон и настройки к ним. Каталог отдаёт подписи и эффективные
  // шаблоны прав; настройки — глобальный слой (пишет только админ), личный слой
  // вызывающего и user-слой (только для admin, конкретный пользователь).
  specialties: {
    list: () => request<SpecialtyCatalogEntry[]>('/specialties'),
    getSettings: () => request<SpecialtySettingsResponse>('/specialties/settings'),
    saveOwnerLayer: (layer: SpecialtySettingsLayer) =>
      request<{ owner: SpecialtySettingsLayer }>('/specialties/settings', {
        method: 'PUT', body: JSON.stringify(layer),
      }),
    saveGlobalLayer: (layer: SpecialtySettingsLayer) =>
      request<{ global: SpecialtySettingsLayer }>('/specialties/settings/global', {
        method: 'PUT', body: JSON.stringify(layer),
      }),
    // User-слой конкретного пользователя (только для admin). Подтягивается отдельно
    // от getSettings — основной ответ остаётся лёгким, admin в админке догружает
    // слой по выбранному пользователю.
    getUserLayer: (userId: string) =>
      request<{ user: SpecialtySettingsLayer; userId: string }>(`/specialties/settings/user/${encodeURIComponent(userId)}`),
    saveUserLayer: (userId: string, layer: SpecialtySettingsLayer) =>
      request<{ user: SpecialtySettingsLayer }>(`/specialties/settings/user/${encodeURIComponent(userId)}`, {
        method: 'PUT', body: JSON.stringify(layer),
      }),
    // Сброс исключений к наследованию (возврат = удаление записи слоя, а не обнуление
    // ячеек): preview — числа/имена ДО подтверждения, reset — фактическая запись.
    // key — точечный жест (одна специальность), без него — весь слой.
    // scope='user' — только для admin, требует userId.
    resetPreview: (scope: 'owner' | 'global' | 'user', key?: string, userId?: string) => {
      const qs = new URLSearchParams();
      if (key) qs.set('key', key);
      if (scope === 'user' && userId) qs.set('userId', userId);
      const s = qs.toString();
      return request<ResetResult>(`/specialties/settings/reset/${scope}/preview${s ? `?${s}` : ''}`);
    },
    reset: (scope: 'owner' | 'global' | 'user', key?: string, userId?: string) => {
      const body: { key?: string; userId?: string } = {};
      if (key) body.key = key;
      if (scope === 'user' && userId) body.userId = userId;
      return request<ResetResult>(`/specialties/settings/reset/${scope}`, {
        method: 'POST', body: JSON.stringify(body),
      });
    },
    // Лимит подмен за ход (фолбэк): `null` = снять настройку слоя (наследование).
    // Управляется через ту же дорогу, что сброс — отдельных типов в index.ts нет,
    // описаны здесь, чтобы не разъезжаться с бэком. scope='user' требует userId.
    setMaxSubstitutions: (scope: 'owner' | 'global' | 'user', value: number | null, userId?: string) => {
      const body: { maxSubstitutions: number | null; userId?: string } = { maxSubstitutions: value };
      if (scope === 'user' && userId) body.userId = userId;
      return request<{ maxSubstitutions: number }>(`/specialties/settings/fallback/${scope}`, {
        method: 'PUT', body: JSON.stringify(body),
      });
    },
  },

  projects: {
    list: () => request<Project[]>('/projects'),
    events: (id: string, opts?: { since?: string; type?: string; actor?: string; limit?: number }) => {
      const qs = new URLSearchParams();
      if (opts?.since) qs.set('since', opts.since);
      if (opts?.type) qs.set('type', opts.type);
      if (opts?.actor) qs.set('actor', opts.actor);
      if (opts?.limit) qs.set('limit', String(opts.limit));
      return request<unknown[]>(`/projects/${encodeURIComponent(id)}/events${qs.toString() ? `?${qs}` : ''}`);
    },
    // Память команды проекта (③-3.4)
    teamMemory: (id: string) => request<TeamMemoryEntry[]>(`/projects/${encodeURIComponent(id)}/team-memory`),
    addTeamMemory: (id: string, text: string, type?: TeamMemoryType) =>
      request<TeamMemoryEntry>(`/projects/${encodeURIComponent(id)}/team-memory`, {
        method: 'POST', body: JSON.stringify({ text, type }),
      }),
    updateTeamMemory: (id: string, entryId: string, text: string) =>
      request<TeamMemoryEntry>(`/projects/${encodeURIComponent(id)}/team-memory/${encodeURIComponent(entryId)}`, {
        method: 'PUT', body: JSON.stringify({ text }),
      }),
    removeTeamMemory: (id: string, entryId: string) =>
      request<void>(`/projects/${encodeURIComponent(id)}/team-memory/${encodeURIComponent(entryId)}`, { method: 'DELETE' }),
    create: (name: string, rootPath: string | null, createDirectory = false, groupId?: string | null,
      git?: { enableGit?: boolean; gitAutoCommit?: boolean; gitAutoPush?: boolean }, color?: string | null) =>
      request<Project>('/projects', { method: 'POST', body: JSON.stringify({ name, rootPath, createDirectory, groupId, ...git, color }) }),
    update: (id: string, data: { name?: string; rootPath?: string; systemPrompt?: string; showHiddenFiles?: boolean; permissionRules?: PermissionRule[]; groupId?: string | null; color?: string | null; mcpServersOn?: string[]; autoImportDossiers?: boolean }) =>
      request<Project>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    // Тумблер грани десктопного агента в проекте (ADR-008). Отдельная ручка, а не поле
    // update: выключение — рубильник, сервер гасит живые сеансы рук проекта и отвечает,
    // сколько погасил (состав инструментов зафиксирован на запуске CLI, и запущенный ход
    // иначе доработал бы с гранью в руках)
    setDesktopAgent: (id: string, enabled: boolean) =>
      request<{ project: Project; handsStopped: number }>(
        `/projects/${encodeURIComponent(id)}/desktop-agent`,
        { method: 'PUT', body: JSON.stringify({ enabled }) }),
    // Реестр общих тегов проекта: перезапись целиком (бэк нормализует order по позиции
    // массива и валидирует уникальность имён без учёта регистра)
    updateTags: (id: string, registry: ProjectTag[]) =>
      request<Project>(`/projects/${encodeURIComponent(id)}/tags`, {
        method: 'PUT', body: JSON.stringify(registry),
      }),
    delete: (id: string) => request<void>(`/projects/${id}`, { method: 'DELETE' }),

    // --- Значок проекта (ADR-009: SVG, белый список lucide, модельный подбор) ---
    // Кандидаты двух видов: name (из белого списка) или paths (нарисованные моделью).
    // Бэк валидирует имя по членству в LucideGlyphs.All и пути по алфавиту/лимитам.
    // Стор не меняется: возвращаются до 4 кандидатов, фронт сам выбирает и зовёт select.
    // Подбор значка — серверный бюджет генерации места project-icon доходит до 180 с,
    // дефолтный клиентский таймаут 30 с обрывал бы запрос раньше ответа. Таймаут расширен
    // явно — общий FETCH_TIMEOUT_MS в offline.ts не трогаем (растровые вызовы тоже
    // задают свой timeoutMs по тому же принципу).
    suggestIcon: (id: string, opts?: { prompt?: string }) =>
      request<{ candidates: GlyphCandidate[]; failReason?: string | null }>(
        `/projects/${encodeURIComponent(id)}/icon/suggest`,
        {
          method: 'POST',
          body: JSON.stringify({ prompt: opts?.prompt?.trim() || undefined }),
          timeoutMs: 180_000,
        },
      ),
    // Кандидаты ДО создания проекта (диалог «Добавить проект»): серверная сторона не
    // сохраняется, имя берётся из черновика названия. Тот же серверный лимит 180 с —
    // клиентский таймаут расширен явно (см. комментарий выше).
    suggestIconPreview: (opts?: { name?: string; prompt?: string }) =>
      request<{ candidates: GlyphCandidate[]; failReason?: string | null }>(
        '/projects/icon/suggest-preview',
        {
          method: 'POST',
          body: JSON.stringify({
            name: opts?.name?.trim() || undefined,
            prompt: opts?.prompt?.trim() || undefined,
          }),
          timeoutMs: 180_000,
        },
      ),
    // Принять кандидата: сервер валидирует тело целиком (источник не доверен, ADR-009 §8),
    // проставляет Kind=Glyph и Glyph, возвращает обновлённый проект.
    selectIcon: (id: string, candidate: { name?: string | null }) =>
      request<Project>(`/projects/${encodeURIComponent(id)}/icon/select`, {
        method: 'POST', body: JSON.stringify(candidate),
      }),
    // Переключение режима отображения значка: буквы ↔ глиф. Файлов больше нет,
    // Glyph не стирается — «Вернуть значок» показывает его снова на той же плитке.
    setIconMode: (id: string, kind: 'initials' | 'glyph') =>
      request<Project>(`/projects/${encodeURIComponent(id)}/icon/mode`, {
        method: 'POST', body: JSON.stringify({ kind }),
      }),
    getBuiltinPrompt: () => request<{ content: string }>('/projects/builtin-prompt'),
    // --- Фон рабочего пространства (ADR-008 §7) ---
    // Сгенерировать / перегенерировать фон. Гейтится владением на бэке (404).
    // suggestedColorKey + !colorApplied — сервер цвет не трогал (выбран руками), фронт
    // показывает диалог подтверждения; при согласии цвет меняет существующий update({color}).
    generateBackground: (id: string) =>
      request<BackgroundResult>(`/projects/${encodeURIComponent(id)}/background/generate`, {
        method: 'POST', timeoutMs: 120_000,
      }),
    // «Вернуть стандартный»: Kind=Standard, файл удаляется. Тот же ответ, что у generate.
    resetBackground: (id: string) =>
      request<BackgroundResult>(`/projects/${encodeURIComponent(id)}/background/reset`, {
        method: 'POST',
      }),
    // URL тайла-маски для CSS mask-image превью: токен через ?access_token= (запрос идёт
    // из CSS, заголовок браузер не поставит), v — cache-buster по имени файла. null —
    // фон не сгенерирован, превью рисует стандартный паттерн.
    backgroundTileUrl: (project: Project): string | null => {
      if (project.background?.kind !== 'generated' || !project.background.tileVersion) return null;
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', project.background.tileVersion);
      return `/api/projects/${encodeURIComponent(project.id)}/background/tile.svg?${params}`;
    },
    getEffectivePrompt: (id: string) => request<{ parts: SystemPromptPart[] }>(`/projects/${id}/effective-prompt`),
    // Кастомные колонки Kanban-доски проекта (пустой массив → дефолтные 3)
    updateBoardColumns: (id: string, columns: BoardColumn[]) =>
      request<Project>(`/projects/${id}/board-columns`, { method: 'PUT', body: JSON.stringify({ columns }) }),
    // Применить пресет каркаса (знакомство v2, п.4): "docs" / "dev" / "personal" — создать;
    // "none" — зафиксировать отказ. 409 → "Каркас уже применён"; 400 → неверный ключ.
    // 404 → чужой проект. Ответ — отчёт { created, skipped } (на "none" —
    // пустые массивы). На ошибке `err.status` покажет код, `err.body.error` — текст с бэка.
    applyPreset: (id: string, presetKey: string) =>
      request<{ created: string[]; skipped: { path: string; reason: string }[] }>(
        `/projects/${encodeURIComponent(id)}/preset`,
        { method: 'POST', body: JSON.stringify({ presetKey }) },
      ),
    // Code Graph: карта типов и связей проекта. 404 (граф не построен) и 403
    // (чужой проект) уходят в статус-коде ошибки — потребитель (lib/codeGraph.ts)
    // отличает их от сетевого сбоя по err.status (см. request в offline.ts).
    // При 404 бэкенд может прислать заголовок X-CodeGraph-Building: true — значит,
    // сборка уже идёт в фоне (build-on-first-GET), клиенту остаётся ждать.
    codeGraph: (id: string) => request<CodeGraph>(`/projects/${encodeURIComponent(id)}/code-graph`),
    // Явное построение графа (кнопка «Построить граф»/«Перестроить»): 202 — построен.
    // Rebuild на бэке синхронный и на большом проекте идёт десятки секунд —
    // поэтому таймаут запроса поднят до 3 минут (дефолтный 30с перехватил бы сборку).
    codeGraphBuild: (id: string) =>
      request<void>(`/projects/${encodeURIComponent(id)}/code-graph/build`, { method: 'POST', timeoutMs: 180_000 }),
    // Preview: сервисы проекта (инференс из манифестов + сохранённые в .claude/launch.json)
    services: (id: string) =>
      request<{ services: ProjectService[]; activeServiceId: string | null }>(`/projects/${id}/services`),
    previewStart: (id: string, svc: {
      serviceId: string; name: string; command: string; args: string[];
      cwd?: string; port?: number; autoPort?: boolean; env?: Record<string, string>;
    }) =>
      request<{ status: string; port?: number; error?: string; serviceId: string }>(`/projects/${id}/preview/start`, {
        method: 'POST', body: JSON.stringify(svc),
      }),
    previewStop: (id: string, serviceId: string) =>
      request<{ status: string }>(`/projects/${id}/preview/stop`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    previewStatus: (id: string) =>
      request<{ running: { serviceId: string; name: string; port: number | null; status: string; error: string | null }[]; activeServiceId: string | null }>(`/projects/${id}/preview/status`),
    previewActive: (id: string, serviceId: string) =>
      request<{ activeServiceId: string }>(`/projects/${id}/preview/active`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    // Сервис поднят вне продукта (Rider, терминал) — показать его в превью.
    // Порт выбирает сервер по конфигурации сервиса, клиент его не передаёт.
    previewActiveExternal: (id: string, serviceId: string) =>
      request<{ activeServiceId: string; port: number }>(`/projects/${id}/preview/active-external`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    getLaunchConfig: (id: string) =>
      request<{ configurations: LaunchConfigEntry[] }>(`/projects/${id}/launch-config`),
    putLaunchConfig: (id: string, configurations: LaunchConfigEntry[]) =>
      request<{ configurations: LaunchConfigEntry[] }>(`/projects/${id}/launch-config`, {
        method: 'PUT', body: JSON.stringify({ configurations }),
      }),
  },

  // Доска агентов (диспетчерская)
  // Сводка дашборда «Домой»: активные + недавние сессии по всем проектам и чатам
  home: {
    summary: (recent = 10) => request<HomeSummaryResponse>(`/home/summary?recent=${recent}`),
  },
  board: {
    agents: () => request<{ items: BoardItem[] }>('/board/agents'),
    interrupt: (sessionId: string) =>
      request<void>(`/board/agents/${sessionId}/interrupt`, { method: 'POST' }),
    allowPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/allow`, { method: 'POST' }),
    denyPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/deny`, { method: 'POST' }),
  },

  // Группы проектов
  projectGroups: {
    list: () => request<ProjectGroup[]>('/project-groups'),
    create: (name: string, color: string) =>
      request<ProjectGroup>('/project-groups', { method: 'POST', body: JSON.stringify({ name, color }) }),
    update: (id: string, data: { name?: string; color?: string }) =>
      request<ProjectGroup>(`/project-groups/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    reorder: (orderedIds: string[]) =>
      request<ProjectGroup[]>('/project-groups/reorder', { method: 'POST', body: JSON.stringify({ orderedIds }) }),
    delete: (id: string) => request<void>(`/project-groups/${id}`, { method: 'DELETE' }),
  },

  tasks: {
    // Все задачи пользователя (для календаря)
    listAll: (from?: string, to?: string) => {
      const qs = new URLSearchParams();
      if (from) qs.set('from', from);
      if (to) qs.set('to', to);
      const q = qs.toString();
      return request<Task[]>(`/tasks${q ? `?${q}` : ''}`);
    },
    listByProject: (projectId: string) => request<Task[]>(`/projects/${projectId}/tasks`),
    // Задачи, порученные персоне-исполнителю (assignee=claude + personaId)
    listByPersona: (personaId: string) =>
      request<Task[]>(`/tasks?personaId=${encodeURIComponent(personaId)}`),
    // projectId === null → личная задача (вне проекта)
    create: (projectId: string | null, dto: CreateTaskDto) =>
      request<Task>(projectId ? `/projects/${projectId}/tasks` : '/tasks', { method: 'POST', body: JSON.stringify(dto) }),
    get: (taskId: string) => request<Task>(`/tasks/${taskId}`),
    update: (taskId: string, dto: UpdateTaskDto) =>
      request<Task>(`/tasks/${taskId}`, { method: 'PUT', body: JSON.stringify(dto) }),
    delete: (taskId: string) => request<void>(`/tasks/${taskId}`, { method: 'DELETE' }),
    // Запустить выполнение задачи Claude-ом (отдельная сессия)
    execute: (taskId: string) => request<Task>(`/tasks/${taskId}/execute`, { method: 'POST' }),
    // Генерация Claude: описание по названию (+контекст проекта), подзадачи по описанию
    aiDescription: (title: string, projectId?: string | null) =>
      request<{ description: string }>('/tasks/ai/description', {
        method: 'POST', body: JSON.stringify({ title, projectId: projectId ?? null }),
      }),
    aiSubtasks: (title: string, description: string, projectId?: string | null) =>
      request<{ subtasks: string[] }>('/tasks/ai/subtasks', {
        method: 'POST', body: JSON.stringify({ title, description, projectId: projectId ?? null }),
      }),
    // Локальная модель (если настроена, иначе Claude): приоритет+метки, нормализация заголовка, дедуп
    aiClassify: (title: string, description?: string | null, projectId?: string | null) =>
      request<{ priority: string | null; labels: string[] }>('/tasks/ai/classify', {
        method: 'POST', body: JSON.stringify({ title, description: description ?? null, projectId: projectId ?? null }),
      }),
    aiNormalizeTitle: (title: string) =>
      request<{ title: string; dueHint: string | null }>('/tasks/ai/normalize-title', {
        method: 'POST', body: JSON.stringify({ title }),
      }),
    aiFindDuplicate: (title: string, description?: string | null, projectId?: string | null) =>
      request<{ duplicateId: string | null; reason: string | null }>('/tasks/ai/find-duplicate', {
        method: 'POST', body: JSON.stringify({ title, description: description ?? null, projectId: projectId ?? null }),
      }),
  },

  // Заметки (Obsidian-совместимая база знаний): .md файлы в личном vault + notes/ проектов
  notes: {
    list: (source?: string, q?: string) => {
      const qs = new URLSearchParams();
      if (source) qs.set('source', source);
      if (q) qs.set('q', q);
      const s = qs.toString();
      return request<NoteSummary[]>(`/notes${s ? `?${s}` : ''}`);
    },
    sources: () => request<NoteSource[]>('/notes/sources'),
    graph: (annotations?: boolean) =>
      request<NoteGraph>(`/notes/graph${annotations ? '?annotations=true' : ''}`),
    templates: () => request<NoteTemplate[]>('/notes/templates'),
    // Резолв по имени вики-ссылки (+ фрагмент по якорю) — hover-preview и embeds
    resolve: (name: string, anchor?: string) => {
      const qs = new URLSearchParams({ name });
      if (anchor) qs.set('anchor', anchor);
      return request<{ note: NoteDetail; fragment: string | null }>(`/notes/resolve?${qs}`);
    },
    // Дневниковая заметка: date — локальная дата клиента YYYY-MM-DD
    daily: (date: string) =>
      request<NoteDetail>('/notes/daily', { method: 'POST', body: JSON.stringify({ date }) }),
    caps: () => request<{ semantic: boolean }>('/notes/caps'),
    // Комментарии к документам (флаг doc-annotations): создание с verify-guard (409 —
    // документ изменился), список с резолвом привязки, смена статуса open/resolved
    annotate: (dto: {
      doc: { scope: string; path: string };
      selection: { start: number; end: number; text: string };
      comment?: string; tags?: string[]; title?: string;
    }) => request<NoteDetail>('/notes/annotate', { method: 'POST', body: JSON.stringify(dto) }),
    annotations: (scope: string, path: string) =>
      request<DocAnnotation[]>(
        `/notes/annotations?scope=${encodeURIComponent(scope)}&path=${encodeURIComponent(path)}`),
    setStatus: (id: string, status: 'open' | 'resolved') =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/status`, {
        method: 'POST', body: JSON.stringify({ status }),
      }),
    repin: (id: string, selection: { start: number; end: number; text: string }) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/repin`, {
        method: 'POST', body: JSON.stringify(selection),
      }),
    reply: (id: string, comment: string, tags?: string[]) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/reply`, {
        method: 'POST', body: JSON.stringify({ comment, tags }),
      }),
    replies: (id: string) => request<NoteReply[]>(`/notes/${encodeURIComponent(id)}/replies`),
    semantic: (q: string, topK = 8) =>
      request<{ available: boolean; results: NoteSemanticHit[] }>(
        `/notes/semantic?q=${encodeURIComponent(q)}&topK=${topK}`),
    reindex: () => request<{ changed: number }>('/notes/reindex', { method: 'POST' }),
    // Переименование/перенос папки целиком (newPath — полный новый путь)
    moveFolder: (source: string, path: string, newPath: string) =>
      request<{ notes: { oldId: string; newId: string }[] }>('/notes/folder/move', {
        method: 'POST', body: JSON.stringify({ source, path, newPath }),
      }),
    // Физические папки (в т.ч. пустые) — для дерева и «куда создать»
    folders: () => request<NoteFolder[]>('/notes/folders'),
    createFolder: (source: string, path: string) =>
      request<NoteFolder>('/notes/folder', { method: 'POST', body: JSON.stringify({ source, path }) }),
    deleteFolder: (source: string, path: string) =>
      request<{ removed: number }>(
        `/notes/folder?source=${encodeURIComponent(source)}&path=${encodeURIComponent(path)}`,
        { method: 'DELETE' }),
    // Перенос: в папку и/или другой источник (личный vault ↔ notes/ проекта)
    move: (id: string, folder: string | null, targetSource?: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/move`, {
        method: 'POST', body: JSON.stringify({ folder, targetSource }),
      }),
    linkMention: (id: string, targetTitle: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/link-mention`, {
        method: 'POST', body: JSON.stringify({ targetTitle }),
      }),
    // ✨ one-shot AI: связи, теги, конспект дня
    suggestLinks: (id: string) =>
      request<{ title: string; why: string }[]>(`/notes/${encodeURIComponent(id)}/suggest-links`, { method: 'POST' }),
    suggestTags: (id: string) =>
      request<string[]>(`/notes/${encodeURIComponent(id)}/suggest-tags`, { method: 'POST' }),
    suggestTitle: (id: string) =>
      request<{ title: string }>(`/notes/${encodeURIComponent(id)}/suggest-title`, { method: 'POST' }),
    dailySummary: (date: string) =>
      request<NoteDetail>('/notes/daily/summary', { method: 'POST', body: JSON.stringify({ date }) }),
    toc: (id: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/toc`, { method: 'POST' }),
    translate: (id: string) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/translate`, { method: 'POST' }),
    get: (id: string) => request<NoteDetail>(`/notes/${encodeURIComponent(id)}`),
    backlinks: (id: string) => request<NoteBacklink[]>(`/notes/${encodeURIComponent(id)}/backlinks`),
    create: (dto: CreateNoteDto) =>
      request<NoteDetail>('/notes', { method: 'POST', body: JSON.stringify(dto) }),
    update: (id: string, dto: UpdateNoteDto) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(dto) }),
    delete: (id: string) =>
      request<void>(`/notes/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Задачи из заметок (флаг notes-task-sync): чекбоксы .md ↔ задачи
    tasks: (id: string) => request<NoteTask[]>(`/notes/${encodeURIComponent(id)}/tasks`),
    promoteTask: (id: string, line: number) =>
      request<Task>(`/notes/${encodeURIComponent(id)}/tasks/promote`, {
        method: 'POST', body: JSON.stringify({ line }),
      }),
    toggleTask: (id: string, line: number, done: boolean) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/tasks/toggle`, {
        method: 'POST', body: JSON.stringify({ line, done }),
      }),
    // Срок 📅 на строке-чекбоксе (дейт-пикер в секции); due=null — убрать
    setNoteTaskDue: (id: string, line: number, due: string | null) =>
      request<NoteDetail>(`/notes/${encodeURIComponent(id)}/tasks/set-due`, {
        method: 'POST', body: JSON.stringify({ line, due }),
      }),
  },

  // Персоны (олицетворённые ИИ-собеседники): CRUD персон владельца (флаг personas)
  personas: {
    // scope=context&projectId= — только доступные в контексте (глобальные + этого проекта)
    list: (opts?: { scope?: string; projectId?: string }) => {
      const qs = new URLSearchParams();
      if (opts?.scope) qs.set('scope', opts.scope);
      if (opts?.projectId) qs.set('projectId', opts.projectId);
      const s = qs.toString();
      return request<Persona[]>(`/personas${s ? `?${s}` : ''}`);
    },
    get: (id: string) => request<Persona>(`/personas/${encodeURIComponent(id)}`),
    create: (dto: CreatePersonaDto) =>
      request<Persona>('/personas', { method: 'POST', body: JSON.stringify(dto) }),
    update: (id: string, dto: UpdatePersonaDto) =>
      request<Persona>(`/personas/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(dto) }),
    // successorId — преемник дефолт-персоны: без него удаление текущей дефолтной вернёт 400
    // «выберите преемника»
    remove: (id: string, successorId?: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}${successorId ? `?successorId=${encodeURIComponent(successorId)}` : ''}`, { method: 'DELETE' }),
    // Назначить персону дефолтной: глобальную — личным дефолтом владельца, проектную —
    // дефолтом её проекта
    makeDefault: (id: string) =>
      request<Persona>(`/personas/${encodeURIComponent(id)}/make-default`, { method: 'POST' }),
    // Чаты, ведущиеся от лица персоны (этап 2): список + создание нового.
    // projectId — глобальная персона, позванная из проекта, получает чат В этом проекте.
    chats: (id: string) => request<Session[]>(`/personas/${encodeURIComponent(id)}/chats`),
    createChat: (id: string, body: { mode?: string; resumeSessionId?: string; name?: string; projectId?: string }) =>
      request<Session>(`/personas/${encodeURIComponent(id)}/chats`, { method: 'POST', body: JSON.stringify(body) }),
    // Подобрать максимально релевантную персону под задачу (для чат-действий AI-хаба). null — нет подходящей.
    // requiredTool — ключ инструментов, без которого действие не выполнить: персоны без него
    // в подборе не участвуют (иначе ответят «инструмент недоступен»).
    match: (task: string, projectId?: string | null, requiredTool?: string) =>
      request<{ personaId: string | null }>('/personas/match', {
        method: 'POST', body: JSON.stringify({ task, projectId: projectId ?? null, requiredTool: requiredTool ?? null }),
      }),

    // Пантеон OmO: каталог ролей-специалистов с бэкенда + идемпотентное подключение
    // всей команды (keys не передаём = все роли). После connect прилетит personas_changed.
    pantheon: () => request<{ templates: PantheonTemplate[] }>('/personas/pantheon'),
    connectPantheon: (keys?: string[]) =>
      request<Persona[]>('/personas/pantheon/connect', {
        method: 'POST',
        body: JSON.stringify({ keys: keys ?? null }),
      }),

    // Назначить/снять собеседника чату вне проекта: персона (personaId) либо .md-агент
    // (agentName) — взаимоисключающе, оба null = снять. 400, если чат уже начат.
    assignPersonaToChat: (chatId: string, personaId: string | null, agentName: string | null = null) =>
      request<Session>(`/chats/${encodeURIComponent(chatId)}/persona`, {
        method: 'POST',
        body: JSON.stringify({ personaId, agentName }),
      }),
    // То же для проектной сессии
    assignPersonaToSession: (projectId: string, sessionId: string, personaId: string | null, agentName: string | null = null) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}/persona`, {
        method: 'POST',
        body: JSON.stringify({ personaId, agentName }),
      }),

    // Долгая память персоны (этап 3): список / поиск / ручное добавление / забывание.
    // type — необязательный фильтр по категории.
    memory: (id: string, type?: PersonaMemoryType) =>
      request<PersonaMemoryEntry[]>(
        `/personas/${encodeURIComponent(id)}/memory${type ? `?type=${encodeURIComponent(type)}` : ''}`,
      ),
    memorySearch: (id: string, q: string, topK?: number) =>
      request<PersonaMemoryHit[]>(
        `/personas/${encodeURIComponent(id)}/memory/search?q=${encodeURIComponent(q)}${topK ? `&topK=${topK}` : ''}`,
      ),
    remember: (id: string, body: { type: PersonaMemoryType; text: string; tags?: string[] }) =>
      request<PersonaMemoryEntry>(`/personas/${encodeURIComponent(id)}/memory`, {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    updateMemory: (id: string, entryId: string, text: string) =>
      request<PersonaMemoryEntry>(`/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}`, {
        method: 'PUT',
        body: JSON.stringify({ text }),
      }),
    // Насосы Memory↔Notes (③-3.3)
    memoryToNote: (id: string, entryId: string) =>
      request<{ noteId: string; noteTitle: string }>(
        `/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}/to-note`,
        { method: 'POST' },
      ),
    noteToMemory: (id: string, noteId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/memory/from-note`, {
        method: 'POST',
        body: JSON.stringify({ noteId }),
      }),
    forget: (id: string, entryId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}`, {
        method: 'DELETE',
      }),
    // Подтвердить предложенную autolearn запись (③-3.2)
    confirmMemory: (id: string, entryId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}/confirm`, {
        method: 'POST',
      }),

    // Рабочий фокус персоны («что я сейчас делаю»): 204 без фокуса → null
    focus: (id: string) =>
      request<PersonaWorkingFocus | undefined>(`/personas/${encodeURIComponent(id)}/focus`)
        .then(f => f ?? null),
    clearFocus: (id: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/focus`, { method: 'DELETE' }),

    // Аватар (этап 4): можно ли генерировать (настроен ли fal),
    // генерация картинки и построение URL для <img>.
    avatarCaps: () => request<{ generate: boolean }>('/personas/avatar/caps'),
    // Генерация галереи кандидатов: возвращает имена файлов (аватар персоны НЕ меняется
    // до явного выбора). count — сколько вариантов (1..4).
    // timeoutMs — под серверный бюджет генерации (у glif 360 с): с дефолтными 30 с
    // браузер обрывал бы запрос раньше ответа с признаком queued.
    generateAvatar: (id: string, opts?: { prompt?: string; count?: number }) =>
      request<{ candidates: string[] }>(`/personas/${encodeURIComponent(id)}/avatar/generate`, {
        method: 'POST',
        body: JSON.stringify({
          prompt: opts?.prompt?.trim() || undefined,
          count: opts?.count,
        }),
        timeoutMs: 400_000,
      }),
    // Выбор кандидата — он становится аватаром персоны, возвращается обновлённая персона
    selectAvatar: (id: string, file: string) =>
      request<Persona>(`/personas/${encodeURIComponent(id)}/avatar/select`, {
        method: 'POST',
        body: JSON.stringify({ file }),
      }),
    // URL картинки-аватара для браузерного <img>: токен уходит через ?access_token=
    // (заголовок Authorization <img> не шлёт — как у notes attachment / files stream).
    // Возвращает null, если у персоны нет картинки. cache-busting по imageFile —
    // иначе после перегенерации браузер покажет старый кадр из кэша.
    avatarUrl: (persona: Persona): string | null => {
      if (persona.avatar?.kind !== 'image' || !persona.avatar.imageFile) return null;
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', persona.avatar.imageFile);
      return `/api/personas/${encodeURIComponent(persona.id)}/avatar?${params}`;
    },
    // Загрузка своего аватара: оригинал + кропнутый квадрат + параметры кропа.
    // Multipart — request() не ставит Content-Type для FormData (boundary от браузера).
    uploadAvatar: (id: string, original: File, cropped: Blob, crop: { scale: number; offsetX: number; offsetY: number }) => {
      const form = new FormData();
      form.append('original', original, original.name || 'original');
      form.append('cropped', cropped, 'avatar.jpg');
      form.append('crop', JSON.stringify(crop));
      return request<Persona>(`/personas/${encodeURIComponent(id)}/avatar/upload`, {
        method: 'POST', body: form, timeoutMs: 60_000,
      });
    },
    // Перекроп сохранённого оригинала (без повторной загрузки файла)
    recropAvatar: (id: string, cropped: Blob, crop: { scale: number; offsetX: number; offsetY: number }) => {
      const form = new FormData();
      form.append('cropped', cropped, 'avatar.jpg');
      form.append('crop', JSON.stringify(crop));
      return request<Persona>(`/personas/${encodeURIComponent(id)}/avatar/recrop`, {
        method: 'POST', body: form, timeoutMs: 60_000,
      });
    },
    // URL оригинала загруженного аватара (для перекропа) — токен через ?access_token=
    avatarOriginalUrl: (persona: Persona): string | null => {
      if (!persona.avatar?.originalFile) return null;
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', persona.avatar.originalFile);
      return `/api/personas/${encodeURIComponent(persona.id)}/avatar/original?${params}`;
    },
    // URL картинки-кандидата (галерея генерации) для <img>: токен через ?access_token=
    avatarCandidateUrl: (id: string, file: string): string => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      return `/api/personas/${encodeURIComponent(id)}/avatar/candidate/${encodeURIComponent(file)}?${params}`;
    },
    // Быстрое создание персоны по свободному промпту: LLM заполняет роль/имя/описание/
    // характер/приветствие/цвет, фото-аватар генерируется автоматически.
    // Запрос долгий (LLM ~10-40с + fal ~10-40с, до ~90с) — таймаут расширен. 502 — можно повторить.
    quickCreate: (body: { prompt: string; scope?: PersonaScope; projectId?: string }) =>
      request<Persona>('/personas/ai/quick-create', {
        method: 'POST',
        body: JSON.stringify(body),
        timeoutMs: 150_000,
      }),
    // AI-формирование команды: промпт + проект → LLM предлагает состав (черновики)
    aiTeam: (projectId: string, prompt: string) =>
      request<{ members: TeamMemberDraft[] }>('/personas/ai/team', {
        method: 'POST',
        body: JSON.stringify({ projectId, prompt }),
        timeoutMs: 150_000,
      }),
    // AI-редактирование характера: без current — генерация с нуля по имени/роли/описанию;
    // с current (legacy-текст или сериализованный контракт, + опц. instruction) — улучшение.
    // Возвращает структурированный контракт (P1). Может занять до ~30с; 502 при ошибке.
    aiCharacter: (body: { name?: string; role?: string; description?: string; current?: string; instruction?: string }) =>
      request<{ contract: PersonaContract }>('/personas/ai/character', {
        method: 'POST',
        body: JSON.stringify(body),
      }),

    // === Привязки «Знания и правила» (фича persona-bindings) ===
    // Мгновенное сохранение: каждая мутация — отдельный запрос, без общей формы.
    bindings: (id: string) =>
      request<PersonaBinding[]>(`/personas/${encodeURIComponent(id)}/bindings`),
    addBinding: (id: string, dto: PersonaBindingDto) =>
      request<PersonaBinding>(`/personas/${encodeURIComponent(id)}/bindings`, {
        method: 'POST', body: JSON.stringify(dto),
      }),
    updateBinding: (id: string, bindingId: string, dto: PersonaBindingDto) =>
      request<PersonaBinding>(`/personas/${encodeURIComponent(id)}/bindings/${encodeURIComponent(bindingId)}`, {
        method: 'PUT', body: JSON.stringify(dto),
      }),
    removeBinding: (id: string, bindingId: string) =>
      request<void>(`/personas/${encodeURIComponent(id)}/bindings/${encodeURIComponent(bindingId)}`, {
        method: 'DELETE',
      }),
    // Полная замена набора привязок одним запросом (атомарно на бэке) — напр. пресет «Минимум»
    setBindings: (id: string, bindings: PersonaBindingDto[]) =>
      request<PersonaBinding[]>(`/personas/${encodeURIComponent(id)}/bindings`, {
        method: 'PUT', body: JSON.stringify({ bindings }),
      }),
    // Каталог целей для пикера: type = project | knowledge | notes | tool | skill;
    // для notes с source= — папки внутри источника; для tool с personaId= —
    // дефолтное состояние каждого инструмента у этой персоны (defaultEnabled/defaultOrigin)
    bindingTargets: (type: string, source?: string, personaId?: string) => {
      const qs = new URLSearchParams({ type });
      if (source) qs.set('source', source);
      if (personaId) qs.set('personaId', personaId);
      return request<BindingTarget[]>(`/personas/binding-targets?${qs}`);
    },
    // AI-формулировка условия «когда пользоваться» по содержимому источника (LLM, до ~60с)
    aiBindingCondition: (body: { type: PersonaBindingType; target: string; path?: string | null }) =>
      request<{ condition: string }>('/personas/bindings/ai-condition', {
        method: 'POST', body: JSON.stringify(body), timeoutMs: 90_000,
      }),
    // AI-подбор привязок под роль персоны: кандидаты, ничего не сохраняется
    suggestBindings: (id: string) =>
      request<{ candidates: PersonaBinding[] }>(`/personas/${encodeURIComponent(id)}/bindings/suggest`, {
        method: 'POST', timeoutMs: 150_000,
      }),
    // Генерация привязок по свободному описанию пользователя: кандидаты, ничего не сохраняется
    generateBindings: (id: string, prompt: string) =>
      request<{ candidates: PersonaBinding[] }>(`/personas/${encodeURIComponent(id)}/bindings/generate`, {
        method: 'POST', body: JSON.stringify({ prompt }), timeoutMs: 150_000,
      }),

    // === Проактивность/автоматизации (правила «событие → действие») ===
    automation: (id: string) =>
      request<PersonaAutomationRule[]>(`/personas/${encodeURIComponent(id)}/automation`),
    addAutomation: (id: string, dto: AutomationRuleDto) =>
      request<PersonaAutomationRule>(`/personas/${encodeURIComponent(id)}/automation`, {
        method: 'POST', body: JSON.stringify(dto),
      }),
    updateAutomation: (id: string, ruleId: string, dto: AutomationRuleDto) =>
      request<PersonaAutomationRule>(
        `/personas/${encodeURIComponent(id)}/automation/${encodeURIComponent(ruleId)}`,
        { method: 'PUT', body: JSON.stringify(dto) },
      ),
    removeAutomation: (id: string, ruleId: string) =>
      request<void>(
        `/personas/${encodeURIComponent(id)}/automation/${encodeURIComponent(ruleId)}`,
        { method: 'DELETE' },
      ),
    // Ручной прогон: синтетическое событие, байпас троттлинга (UX «Проверить»)
    testAutomation: (id: string, ruleId: string) =>
      request<void>(
        `/personas/${encodeURIComponent(id)}/automation/${encodeURIComponent(ruleId)}/test`,
        { method: 'POST' },
      ),
    // AI-подбор правил автоматизации под роль персоны: кандидаты, ничего не сохраняется
    suggestAutomation: (id: string) =>
      request<{ candidates: PersonaAutomationRule[] }>(`/personas/${encodeURIComponent(id)}/automation/suggest`, {
        method: 'POST', timeoutMs: 150_000,
      }),
    // Генерация правил автоматизации по свободному промпту пользователя: кандидаты, ничего не сохраняется
    generateAutomation: (id: string, prompt: string) =>
      request<{ candidates: PersonaAutomationRule[] }>(`/personas/${encodeURIComponent(id)}/automation/generate`, {
        method: 'POST', body: JSON.stringify({ prompt }), timeoutMs: 150_000,
      }),
  },

  // Утренний бриф (флаг daily-briefing): собрать план дня в дневник
  briefing: {
    today: (date?: string) =>
      request<NoteDetail>('/briefing/today', { method: 'POST', body: JSON.stringify({ date: date ?? null }) }),
  },

  // Единый поиск (флаг unified-search): заметки + задачи в одной выдаче
  search: (q: string, topK = 8) =>
    request<SearchHit[]>(`/search?q=${encodeURIComponent(q)}&topK=${topK}`),

  // Онбординги: старт/резюм чат-интервью знакомства.
  // Идемпотентны: живая сессия онбординга возвращается как есть, удалённая — заменяется новой
  onboarding: {
    startUser: () => request<Session>('/onboarding/user/start', { method: 'POST' }),
    startProject: (projectId: string) =>
      request<Session>(`/onboarding/project/${encodeURIComponent(projectId)}/start`, { method: 'POST' }),
    // Страховка «применить итоги разговора»: LLM-прогон + генерация аватара до ~90 с,
    // таймаут 150 с — запас на fal и холодный старт модели
    applyTranscript: () =>
      request<Persona>('/onboarding/user/apply-transcript', { method: 'POST', timeoutMs: 150_000 }),
  },

  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    // Подобрать значки-иконки чатам проекта без них (действие AI-палитры «Проставить значки тем»)
    iconBatch: (projectId: string) =>
      request<{ processed: number; skipped: number }>(`/projects/${encodeURIComponent(projectId)}/sessions/icon-batch`, { method: 'POST' }),
    create: (projectId: string, mode = 'acceptEdits', resumeSessionId?: string, name?: string, model?: string, agentName?: string, effort?: string, desktop?: boolean) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        // desktop — ТИП чата (ADR-008), задаётся только при создании: из десктопного чата
        // нельзя продолжить обычный и наоборот, поэтому в update этого поля нет
        body: JSON.stringify({ mode, resumeSessionId, name, model, agentName, effort, desktop }),
      }),
    update: (projectId: string, sessionId: string, data: { name?: string | null; model?: string | null; effort?: string | null; expiresAfterMinutes?: number | null; tags?: string[]; excludeFromDossiers?: boolean | null; notificationsMuted?: boolean; voiceMode?: boolean }) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, sessionId: string) =>
      request<void>(`/projects/${projectId}/sessions/${sessionId}`, { method: 'DELETE' }),
    getHistory: (projectId: string, sessionId: string) =>
      request<unknown[]>(`/projects/${projectId}/sessions/${sessionId}/history`),
    // «Итог сессии»: конспект сессии заметкой (флаг notes-session-summary).
    // Маршрут по id сессии — работает и для проектных сессий, и для чатов
    summary: (sessionId: string) =>
      request<NoteDetail>(`/sessions/${sessionId}/summary`, { method: 'POST' }),
    // «Задачи из чата» (флаг chat-extract-tasks): извлечь кандидатов (не создаёт)
    extractTasks: (sessionId: string) =>
      request<ExtractTasksResponse>(`/sessions/${sessionId}/extract-tasks`, { method: 'POST' }),
    // Снять постоянное разрешение инструмента в чате («Всегда разрешать …»).
    // Маршрут по id сессии — работает и для проектных чатов, и для чатов вне проекта.
    // Отдаёт обновлённую сессию (как смена режима) — доборный GET не нужен
    revokeAutoAllow: (sessionId: string, tool: string) =>
      request<Session>(
        `/sessions/${encodeURIComponent(sessionId)}/auto-allow?tool=${encodeURIComponent(tool)}`,
        { method: 'DELETE' }),
    // Снять сообщение из очереди занятого чата (крестик на карточке-призраке).
    // Очередь живёт в памяти сервера — актуальный состав приходит событием pending_messages
    cancelPending: (sessionId: string, messageId: string) =>
      request<void>(`/sessions/${encodeURIComponent(sessionId)}/pending/${encodeURIComponent(messageId)}`,
        { method: 'DELETE' }),
    // Прервать идущий ход и доставить ждущее сообщение сейчас. Обычная отправка ход не
    // прерывает (он доживает сам) — это явный перебой по кнопке на карточке очереди
    preemptForPending: (sessionId: string) =>
      request<void>(`/sessions/${encodeURIComponent(sessionId)}/pending/preempt`, { method: 'POST' }),
    // Снимок промпта хода — что ушло модели (кнопка под постом). 404 — снимок вытеснен
    // ретеншном последних 50 ходов чата
    promptSnapshot: (sessionId: string, snapshotId: string) =>
      request<PromptSnapshot>(
        `/sessions/${encodeURIComponent(sessionId)}/prompt/${encodeURIComponent(snapshotId)}`),
    // Текст одного файла слоя CLI (CLAUDE.md) — грузится, только когда строку раскрыли:
    // в основной выдаче у него лишь размер, иначе открытие шторки тянуло бы десятки КБ
    promptSnapshotFile: (sessionId: string, snapshotId: string, key: string) =>
      request<PromptSection>(
        `/sessions/${encodeURIComponent(sessionId)}/prompt/${encodeURIComponent(snapshotId)}/file?key=${encodeURIComponent(key)}`),
    // Разбор промпта моделью. includeText=true — человек разрешил приложить фрагменты
    // текста секций (по умолчанию уходят только метаданные)
    // timeoutMs — это ход модели, дефолтных 30 с ему мало: обрыв по таймауту
    // трактуется как сетевая ошибка и выглядит как «Действие недоступно офлайн»
    analyzePrompt: (sessionId: string, snapshotId: string, includeText: boolean) =>
      request<{ analysis: string }>(
        `/sessions/${encodeURIComponent(sessionId)}/prompt/${encodeURIComponent(snapshotId)}/analyze`,
        { method: 'POST', body: JSON.stringify({ includeText }), timeoutMs: 180_000 }),
  },

  // «Стена»: per-user набор чатов колонками. Ресурс бэка — /api/me/wall
  // (MyWallController, конвенция per-user настроек), короткое имя здесь — для читаемости вызовов.
  wall: {
    // Состав стены — полные Session в порядке набора (мёртвые уже отфильтрованы сервером)
    get: () => request<{ chats: Session[] }>('/me/wall'),
    // Полная замена состава; ответ — итог после серверной чистки (дедуп/чужие/потолок)
    put: (chatIds: string[]) =>
      request<{ chats: Session[] }>('/me/wall', { method: 'PUT', body: JSON.stringify({ chatIds }) }),
    // Кандидаты для пикера: все чаты владельца, свежие сверху
    candidates: () => request<Session[]>('/me/wall/candidates'),
  },

  // Чаты вне проекта (project-less)
  chats: {
    list: () => request<Session[]>('/chats'),
    get: (id: string) => request<Session>(`/chats/${id}`),
    create: (mode = 'auto', resumeSessionId?: string, name?: string, model?: string, effort?: string) =>
      request<Session>('/chats', {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, effort }),
      }),
    update: (id: string, data: { name?: string | null; model?: string | null; effort?: string | null; pinned?: boolean; expiresAfterMinutes?: number | null; notificationsMuted?: boolean; voiceMode?: boolean }) =>
      request<Session>(`/chats/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    // Ручная группировка чатов (drag-and-drop в списке): вложить в родительский чат
    // либо вынести в корень (parentId: null). Один эндпоинт на оба списка —
    // работает и для проектных сессий, и для чатов вне проектов
    setParent: (id: string, parentId: string | null) =>
      request<Session>(`/chats/${encodeURIComponent(id)}/parent`, {
        method: 'PUT',
        body: JSON.stringify({ parentId }),
      }),
    // Отметить чат прочитанным (синк непрочитанности между устройствами).
    // Работает и для проектных сессий, и для чатов вне проектов; не двигает updatedAt
    markRead: (id: string) =>
      request<void>(`/chats/${encodeURIComponent(id)}/read`, { method: 'PUT' }),
    // Обновить название чата по текущей переписке (AI-хаб, действие chat.retitle)
    retitle: (id: string) =>
      request<Session>(`/chats/${encodeURIComponent(id)}/retitle`, { method: 'POST' }),
    // Групповой чат персон (флаг persona-group-chats): 2-4 участника, первый — ведущая.
    // Зона — по ведущей: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    createGroup: (personaIds: string[], mode = 'auto', name?: string) =>
      request<Session>('/chats/group', {
        method: 'POST',
        body: JSON.stringify({ personaIds, mode, name }),
      }),
    // Обновить состав участников группового чата (спикер сохраняется, если остался)
    setParticipants: (id: string, personaIds: string[]) =>
      request<Session>(`/chats/${id}/participants`, {
        method: 'PUT',
        body: JSON.stringify({ personaIds }),
      }),
    // Цикл «до готово» (флаг work-loop): агент работает итерациями до отчёта о завершении.
    // Работает и для проектных сессий, и для чатов вне проекта
    setWorkLoop: (id: string, enabled: boolean) =>
      request<Session>(`/chats/${id}/loop`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
    // Режим «Командная реализация»: вкл/выкл режима чата-штаба.
    // При включении можно сразу задать авто-волны и состав (пустой список = вся команда)
    setTeamImplement: (id: string, enabled: boolean, opts?: { autoWaves?: boolean; coordinatorPersonaId?: string; plannerPersonaId?: string; executorPersonaIds?: string[] }) =>
      request<Session>(`/chats/${id}/team-implement`, {
        method: 'PUT',
        body: JSON.stringify({ enabled, ...opts }),
      }),
    // Переключение авто-волн на ходу (из бейджа режима): трогает только флаг, не режим
    setTeamImplementAuto: (id: string, autoWaves: boolean) =>
      request<Session>(`/chats/${id}/team-implement/auto`, {
        method: 'PUT',
        body: JSON.stringify({ autoWaves }),
      }),
    // «Остановить» (кнопка человека): новые волны не стартуют, текущие исполнители
    // дорабатывают начатое. В ленте появляется карточка остановки с «Продолжить»
    stopTeamImplement: (id: string) =>
      request<Session>(`/chats/${id}/team-implement/stop`, { method: 'PUT' }),
    // Отдельное git worktree чата: вкл — сессия переезжает в изолированное дерево на новой
    // ветке (начатый чат — с переносом контекста), выкл — возврат в корень проекта.
    // force подтверждает потерю несохранённых правок дерева. Только проектные чаты.
    // Создание дерева = checkout репы, на большой может быть небыстрым
    setWorktree: (id: string, enabled: boolean, branch?: string, force = false) =>
      request<Session>(`/chats/${id}/worktree`, {
        method: 'PUT',
        body: JSON.stringify({ enabled, branch: branch ?? null, force }),
        timeoutMs: 120_000,
      }),
    // Миграция чата на другого провайдера («Продолжить на …» при исчерпании лимита):
    // транскрипт переезжает в профиль провайдера, контекст сохраняется. Работает и для
    // проектных сессий. subscriptionKey — явный выбор аккаунта того же пула подписок
    // (кнопка kind='subscription' карточки лимита); для сторонних провайдеров не передаётся
    migrateProvider: (id: string, model: string, subscriptionKey?: string) =>
      request<Session>(`/chats/${id}/migrate-provider`, {
        method: 'POST',
        body: JSON.stringify(subscriptionKey ? { model, subscriptionKey } : { model }),
      }),
    // Режим прав: сохраняем сразу при выборе в Composer, иначе он доехал бы до сессии
    // только вместе со следующим сообщением и терялся при уходе со страницы
    setMode: (id: string, mode: string) =>
      request<Session>(`/chats/${id}/mode`, {
        method: 'PUT',
        body: JSON.stringify({ mode }),
      }),
    delete: (id: string) => request<void>(`/chats/${id}`, { method: 'DELETE' }),
    getHistory: (id: string) => request<unknown[]>(`/chats/${id}/history`),
    uploadFile: async (id: string, file: File): Promise<{ path: string }> => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const form = new FormData();
      form.append('file', file);
      const res = await fetch(`/api/chats/${id}/files/upload`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
      return res.json();
    },
  },

  // Продуктовая история (AI-сводка по всем проектам): дни, сводка дня, счетчик новых
  history: {
    days: (sinceDays = 0) =>
      request<DaySummaryStub[]>(`/history/days${sinceDays > 0 ? `?sinceDays=${sinceDays}` : ''}`),
    day: (date: string) =>
      request<ChangelogDay>(`/history/day/${date}`),
    newCount: (sinceIso: string) =>
      request<{ count: number }>(`/history/new-count?since=${encodeURIComponent(sinceIso)}`),
    status: () => request<ChangelogStatus>('/history/status'),
    // Сбросить кеш одного дня (перегенерация) / всей истории (очистка)
    invalidateDay: (date: string) =>
      request<void>(`/history/day/${date}`, { method: 'DELETE' }),
    clear: () =>
      request<void>('/history', { method: 'DELETE' }),
  },

  // Документация проекта (README.md + docs/**) для панели «Документы»: корпус со связями,
  // а не файлы — отсюда отдельная секция рядом с files
  docs: {
    index: (projectId: string) =>
      request<DocEntry[]>(`/projects/${projectId}/docs`),
    doc: (projectId: string, path: string) =>
      request<DocDetail>(`/projects/${projectId}/docs/doc?path=${encodeURIComponent(path)}`),
    search: (projectId: string, q: string) =>
      request<DocSearchHit[]>(`/projects/${projectId}/docs/search?q=${encodeURIComponent(q)}`),
    // Область документации: что выбрано и что вообще годится (папки, файлы корня, типы)
    scope: (projectId: string) =>
      request<DocsScopeInfo>(`/projects/${projectId}/docs/scope`),
    // У каждой оси null — вернуть её к дефолту, [] — «ничего отсюда».
    // Ответ отдаёт СОХРАНЁННОЕ значение (сервер отбрасывает мусор), его и показываем
    setScope: (projectId: string, scope: Partial<DocsScope>) =>
      request<DocsScopeInfo>(`/projects/${projectId}/docs/scope`, {
        method: 'PUT',
        body: JSON.stringify({
          folders: scope.folders ?? null,
          rootFiles: scope.rootFiles ?? null,
          types: scope.types ?? null,
          // undefined — не трогать выбор «Начала»; '' — вернуть авто-README
          home: scope.home === undefined ? null : scope.home,
        }),
      }),
    // Вынести текущую область в файл .docs репозитория: дальше она версионируется и
    // одинакова у всех, кто открыл репозиторий, а setScope правит уже файл
    saveScopeFile: (projectId: string) =>
      request<DocsScopeInfo>(`/projects/${projectId}/docs/scope-file`, { method: 'POST' }),
    // Порядок страниц папки — правка .order в рабочем дереве, поэтому только по жесту
    // пользователя. items — имена БЕЗ расширения в новом порядке (как строки в файле);
    // это подмножество папки, остальные её строки сервер оставляет на своих местах.
    // Ответ — свежий индекс: порядок приезжает вместе с подтверждением
    // Создать документ или раздел. name — ЧЕЛОВЕЧЕСКОЕ название: имя файла из него делает
    // сервер (пробелы → дефисы), а само название становится заголовком первой строки.
    // Раздел создаётся парой «страница + папка» — в wiki он существует только так
    create: (projectId: string, folder: string, name: string, kind: 'doc' | 'section') =>
      request<{ path: string; index: DocEntry[] }>(`/projects/${projectId}/docs/create`, {
        method: 'POST',
        body: JSON.stringify({ folder, name, kind }),
      }),
    // Переименовать документ или раздел. Раздел переезжает парой со всем поддеревом:
    // moved — карта «старый путь → новый» по каждому переехавшему документу, по ней
    // панель чинит закреплённые и открытый документ. updateLinks=false оставляет чужие
    // файлы нетронутыми и возвращает число ссылок, оставшихся битыми
    rename: (projectId: string, path: string, newName: string, updateLinks: boolean) =>
      request<{ path: string; updatedDocs: number; brokenLinks: number; moved: Record<string, string>; index: DocEntry[] }>(
        `/projects/${projectId}/docs/rename`, {
          method: 'POST',
          body: JSON.stringify({ path, newName, updateLinks }),
        }),
    // Удалить документ или раздел. Раздел уходит парой «страница + папка» со всем
    // содержимым, включая файлы, которых панель не показывала (removedFiles).
    // brokenLinks — сколько ссылок на удалённое осталось: починить их нечем
    remove: (projectId: string, path: string) =>
      request<{ removed: string[]; brokenLinks: number; removedFiles: number; index: DocEntry[] }>(
        `/projects/${projectId}/docs/delete`, {
          method: 'POST',
          body: JSON.stringify({ path }),
        }),
    // Перенести документ или раздел в другую папку области. Раздел переезжает со всем
    // поддеревом; updateLinks чинит и чужие ссылки на переехавшее, и его собственные —
    // при смене папки меняется глубина, и относительные пути ломаются в обе стороны
    move: (projectId: string, path: string, targetFolder: string, updateLinks: boolean) =>
      request<{ path: string; updatedDocs: number; brokenLinks: number; moved: Record<string, string>; index: DocEntry[] }>(
        `/projects/${projectId}/docs/move`, {
          method: 'POST',
          body: JSON.stringify({ path, targetFolder, updateLinks }),
        }),
    setOrder: (projectId: string, folder: string, items: string[]) =>
      request<DocEntry[]>(`/projects/${projectId}/docs/order`, {
        method: 'PUT',
        body: JSON.stringify({ folder, items }),
      }),
    // Значение свойства в шапке документа: сервер правит строку «**Ключ:** …» прямо в md
    // (или добавляет её на место по схеме). value=null — снять свойство, '' — пустой слот.
    // Вместе со значением может обновиться «дата смены» — какие ключи изменились
    // фактически, говорит touched. Ответ несёт свежий индекс: метка в дереве обязана
    // приехать вместе с подтверждением, а не вторым запросом
    setProperty: (projectId: string, path: string, key: string, value: string | null) =>
      request<{ properties: DocProperty[]; touched: string[]; index: DocEntry[] }>(
        `/projects/${projectId}/docs/property`, {
          method: 'PUT',
          body: JSON.stringify({ path, key, value }),
        }),
    // Схема типов документов: перезаписывает секцию docTypes файла .docs. Файла нет — он
    // будет создан вместе с действующей областью (схеме больше негде жить)
    setDocTypes: (projectId: string, types: DocTypeSchema[]) =>
      request<{ scope: DocsScopeInfo; index: DocEntry[] }>(`/projects/${projectId}/docs/types`, {
        method: 'PUT',
        body: JSON.stringify({ types }),
      }),
  },

  // История решений (change-dossiers) — записи «зачем менялось, что решили,
  // что отвергли, какие грабли», привязанные к коммитам. ADR-004 §4/§6/§8.
  // Этап 1: просмотр. Этап 3: экспорт в ветку ccs/dossiers/v1 через git-плюминг.
  dossiers: {
    list: (projectId: string, filter?: { file?: string; symbol?: string; commit?: string }) => {
      const qs = new URLSearchParams();
      if (filter?.file) qs.set('file', filter.file);
      if (filter?.symbol) qs.set('symbol', filter.symbol);
      if (filter?.commit) qs.set('commit', filter.commit);
      const s = qs.toString();
      // Контракт GET /dossiers (ADR-004 §4, спринт «История решений» блок В):
      // { entries, coverage }. coverage — метрика охвата за окно periodDays:
      // знаменатель commits (с --follow при file), числитель dossiers. При сбое git
      // числитель/знаменатель обнуляются (контракт бэка — листинг не роняется).
      // coverage помечена опциональной: ранний ответ или альтернативная сборка бэка
      // могут её не прислать — панель тогда просто не рисует строку охвата.
      return request<{
        entries: DossierEntry[];
        coverage?: { periodDays: number; commits: number; dossiers: number } | null;
      }>(`/projects/${projectId}/dossiers${s ? `?${s}` : ''}`);
    },
    // Готовность проекта к экспорту (ADR-004 §6): isGitRepo гейтит кнопку в UI,
    // sharedFolder — предупреждение о втором владельце той же папки. hasDossierBranch —
    // наличие локальной refs/heads/ccs/dossiers/v1: пока ветки нет, импорт из неё
    // бессмыслен, кнопку «Загрузить» в UI гейтим этим признаком.
    // autoExport — причина гейта АВТОвыгрузки: панель выбирает по ней текст подсказки
    // (после сужения фона «ветка заведомо наша» общая фраза «выгружается само» врала
    // бы при чужом tip / одной origin-ветке / общей папке). null у не-git проекта.
    exportStatus: (projectId: string) =>
      request<{
        isGitRepo: boolean;
        sharedFolder: boolean;
        hasDossierBranch: boolean;
        autoExport: 'active' | 'foreignTip' | 'originOnly' | 'sharedFolder' | null;
      }>(
        `/projects/${encodeURIComponent(projectId)}/dossiers/export/status`),
    // Запуск экспорта. push=true — единственное место UI, откуда вызывается git push
    // (ADR §6: «Push — только вручную»). Ответ — состояние финальной карточки:
    // count подставляется в текст успеха, nothingToExport переключает на состояние «пусто».
    // timeoutMs 120 с: plumbing-экспорт на большой истории (~38 с на 217 паспортов
    // наблюдалось на проде) — дефолтные 30 с обрывали запрос, хотя сервер ещё работал.
    exportRun: (projectId: string, push: boolean) =>
      request<{ status: 'exported' | 'nothingToExport'; count: number }>(
        `/projects/${encodeURIComponent(projectId)}/dossiers/export`,
        { method: 'POST', body: JSON.stringify({ push }), timeoutMs: 120_000 }),
    // Импорт «Историй решений» из ветки ccs/dossiers/v1 (этап 4): читает ветку
    // plumbing-командами и кладёт записи в стор с origin='imported' и importedAuthor.
    // added — реально добавленные, skipped — остальные записи index.json
    // (уже были импортированными / нечитаемые / кривые sha). noBranch — ветки нет
    // ни локально, ни в origin (нейтральный ответ, не ошибка).
    importRun: (projectId: string) =>
      request<{ status: 'imported' | 'nothingToImport' | 'noBranch'; added: number; skipped: number }>(
        `/projects/${encodeURIComponent(projectId)}/dossiers/import`,
        { method: 'POST' }),
  },

  // Раздел «Телеметрия»: статус проброса SigNoz — фронт решает, показать iframe или заглушку
  telemetry: {
    status: () =>
      request<{ configured: boolean; reachable: boolean; proxyPath: string; discussProjectId?: string | null }>('/telemetry/status'),
    // Вкладка «Инциденты»: список отдаёт статус телеметрии ОТДЕЛЬНО от элементов —
    // пустой список при выключенном SigNoz нельзя показывать как «всё тихо»
    incidents: () => request<IncidentListResponse>('/telemetry/incidents'),
    incident: (fingerprint: string) =>
      request<IncidentDossier>(`/telemetry/incidents/${encodeURIComponent(fingerprint)}`),
    // Досье markdown'ом — описание задачи и черновик сообщения в чат
    incidentText: (fingerprint: string) =>
      request<{ text: string }>(`/telemetry/incidents/${encodeURIComponent(fingerprint)}/text`),
    // Разбор моделью — единственное место фичи, где участвует LLM, и только по кнопке
    incidentExplain: (fingerprint: string) =>
      request<{ text: string }>(
        `/telemetry/incidents/${encodeURIComponent(fingerprint)}/explain`, { method: 'POST' }),
    // Заглушить/вернуть звук: инцидент остаётся в списке, но уходит из счётчика и из push
    muteIncident: (fingerprint: string, muted: boolean) =>
      request<void>(
        `/telemetry/incidents/${encodeURIComponent(fingerprint)}/mute?muted=${muted}`,
        { method: 'POST' }),
  },

  files: {
    // URL файла проекта для браузерного <img>/<video>: токен через ?access_token=,
    // потому что тег не шлёт заголовки. Нужен картинкам в markdown — README ссылается
    // на них относительным путём, а base64 из files/content для <img src> не подходит
    fileUrl: (projectId: string, path: string): string => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const params = new URLSearchParams({ path });
      if (token) params.set('access_token', token);
      return `/api/projects/${encodeURIComponent(projectId)}/files/stream?${params}`;
    },
    list: (projectId: string, path = '') =>
      request<FileEntry[]>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`),
    tree: (projectId: string, path = '', showHidden?: boolean) =>
      request<FileEntry[]>(`/projects/${projectId}/files/tree?path=${encodeURIComponent(path)}${showHidden ? '&showHidden=true' : ''}`),
    search: (projectId: string, q: string) =>
      request<FileEntry[]>(`/projects/${projectId}/files/search?q=${encodeURIComponent(q)}`),
    getContent: (projectId: string, path: string) =>
      request<{ content: string | null; isBinary: boolean; isImage: boolean; isDocument?: boolean; docKind?: string; mimeType?: string; base64?: string; fileSize?: number }>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`),
    saveContent: (projectId: string, path: string, content: string) =>
      request<void>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`, {
        method: 'PUT',
        body: JSON.stringify({ content }),
      }),
    // Документы (pdf/docx/xlsx/pptx): конвертация в MD + ИИ-помощь (локальная модель / claude)
    documentConvert: (projectId: string, path: string) =>
      request<{ markdown: string }>(`/projects/${projectId}/files/document/convert?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentSummary: (projectId: string, path: string) =>
      request<{ summary: string }>(`/projects/${projectId}/files/document/summary?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentExtract: (projectId: string, path: string) =>
      request<{ decisions: string[]; dates: string[]; people: string[]; actionItems: string[] }>(`/projects/${projectId}/files/document/extract?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentTags: (projectId: string, path: string) =>
      request<{ tags: string[] }>(`/projects/${projectId}/files/document/tags?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    // Трансформировать любой файл в Markdown и сохранить (рядом или в targetDir).
    // enhance — восстановить разметку локальной моделью (заголовки/списки, для pdf).
    toMarkdown: (projectId: string, path: string, targetDir?: string | null, enhance = false) =>
      request<{ savedPath: string; markdown: string }>(`/projects/${projectId}/files/document/to-markdown`, {
        method: 'POST', body: JSON.stringify({ path, targetDir: targetDir ?? null, enhance }),
      }),
    getDiff: (projectId: string, path: string) =>
      request<{ diff: string | null }>(`/projects/${projectId}/files/diff?path=${encodeURIComponent(path)}`),
    // Панель «Изменения»: для присланных путей — какие ЕЩЁ чаты проекта их меняли.
    // Ключи ответа — ровно присланные строки path (см. FilesController.ChangedBy)
    changedBy: (projectId: string, paths: string[]) =>
      request<{ files: Record<string, ChangedBySession[]> }>(`/projects/${projectId}/files/changed-by`, {
        method: 'POST',
        body: JSON.stringify({ paths }),
      }),
    revert: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/revert`, { method: 'POST', body: JSON.stringify({ path }) }),
    createFile: (projectId: string, path: string, content?: string) =>
      request<void>(`/projects/${projectId}/files/create`, { method: 'POST', body: JSON.stringify({ path, content }) }),
    mkdir: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/mkdir`, { method: 'POST', body: JSON.stringify({ path }) }),
    rename: (projectId: string, oldPath: string, newPath: string) =>
      request<void>(`/projects/${projectId}/files/rename`, {
        method: 'POST',
        body: JSON.stringify({ oldPath, newPath }),
      }),
    delete: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
    saveFromUrl: (projectId: string, url: string, path: string) =>
      request<{ path: string }>(`/projects/${projectId}/files/save-from-url`, {
        method: 'POST',
        body: JSON.stringify({ url, path }),
      }),
    officeDiscard: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/files/office-discard?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    getOfficeVersion: (projectId: string, path: string) =>
      request<{ ms: number }>(`/projects/${projectId}/files/office-version?path=${encodeURIComponent(path)}`),
    officeForceSave: (projectId: string, path: string) =>
      request<{ ok: boolean; reason?: string }>(`/projects/${projectId}/files/office-force-save?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    upload: async (projectId: string, file: File, targetPath = ''): Promise<void> => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const form = new FormData();
      form.append('file', file);
      const res = await fetch(
        `/api/projects/${projectId}/files/upload?path=${encodeURIComponent(targetPath)}`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form },
      );
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
    },
  },

  // Просмотр файла вне корня проекта (абсолютный путь) — например, файл открытый
  // из карточки инструмента чата в другом проекте/дереве. Формат ответа тот же, что
  // у files/content; 403 — путь вне досягаемости песочницы, 400 — путь не абсолютный.
  hostFiles: {
    getContent: (path: string) =>
      request<{ content: string | null; isBinary: boolean; isImage: boolean; isVideo?: boolean; isAudio?: boolean; isDocument?: boolean; docKind?: string; mimeType?: string; base64?: string; fileSize?: number }>(`/host-files/content?path=${encodeURIComponent(path)}`),
  },

  // Git проекта (раздел «Файлы» → «Изменения»/«История»); ошибки операций — 409 { error }
  git: {
    // ?sessionId= (gq): активный worktree-чат переводит запросы в своё дерево — суффикс
    // добавляется ко всем операциям, достижимым из git-бара и панели «Изменения»
    status: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/status${gq(projectId)}`),
    diff: (projectId: string, path: string, staged = false) =>
      request<{ diff: string | null }>(`/projects/${projectId}/git/diff?path=${encodeURIComponent(path)}&staged=${staged}${gq(projectId, '&')}`),
    log: (projectId: string, limit = 100, branch?: string) =>
      request<GitLogEntry[]>(`/projects/${projectId}/git/log?limit=${limit}${branch ? `&branch=${encodeURIComponent(branch)}` : ''}${gq(projectId, '&')}`),
    // Незапушенные коммиты (впереди upstream) — стек скоупов панели «Изменения»
    unpushed: (projectId: string, limit = 100) =>
      request<GitLogEntry[]>(`/projects/${projectId}/git/unpushed?limit=${limit}${gq(projectId, '&')}`),
    // Настройка промпта AI-описания коммита: чтение (global/projectOverride/effective/default)
    getCommitPrompt: (projectId: string) =>
      request<GitCommitPromptInfo>(`/projects/${projectId}/git/commit-prompt`),
    // Сохранить оба промпта: global (per-user, всегда) + project (override при useProject)
    setCommitPrompt: (projectId: string, global: string, project: string, useProject: boolean) =>
      request<GitCommitPromptInfo>(`/projects/${projectId}/git/commit-prompt`, {
        method: 'PUT', body: JSON.stringify({ global, project, useProject }),
      }),
    // Определить стиль коммитов по истории репы → инструкция для поля (не сохраняет)
    detectCommitStyle: (projectId: string) =>
      request<{ prompt: string }>(`/projects/${projectId}/git/ai/detect-commit-style`, { method: 'POST', timeoutMs: 60_000 }),
    branches: (projectId: string) =>
      request<GitBranchInfo[]>(`/projects/${projectId}/git/branches${gq(projectId)}`),
    commitDetail: (projectId: string, sha: string) =>
      request<GitCommitDetail>(`/projects/${projectId}/git/commits/${sha}${gq(projectId)}`),
    commitFileDiff: (projectId: string, sha: string, path: string) =>
      request<{ diff: string | null }>(`/projects/${projectId}/git/commits/${sha}/diff?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    stage: (projectId: string, path: string) =>
      request<GitStatus>(`/projects/${projectId}/git/stage${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    unstage: (projectId: string, path: string) =>
      request<GitStatus>(`/projects/${projectId}/git/unstage${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    stageAll: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/stage-all${gq(projectId)}`, { method: 'POST' }),
    // Откат правок файла к HEAD — необратимо (подтверждение на фронте)
    discard: (projectId: string, path: string) =>
      request<GitStatus>(`/projects/${projectId}/git/discard${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    discardAll: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/discard-all${gq(projectId)}`, { method: 'POST' }),
    commit: (projectId: string, message: string, amend = false) =>
      request<{ sha: string }>(`/projects/${projectId}/git/commit${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ message, amend }),
      }),
    checkout: (projectId: string, branch: string) =>
      request<GitStatus>(`/projects/${projectId}/git/checkout${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ branch }) }),
    createBranch: (projectId: string, name: string, from?: string) =>
      request<GitStatus>(`/projects/${projectId}/git/branches${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ name, from: from ?? null }),
      }),
    fetch: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/fetch${gq(projectId)}`, { method: 'POST', timeoutMs: 60_000 }),
    pull: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/pull${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    push: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/push${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    // «Подтянуть и опубликовать»: rebase на origin + push (ветка разошлась с origin).
    // Дольше push: внутри две сетевые операции подряд
    sync: (projectId: string) =>
      request<GitStatus>(`/projects/${projectId}/git/sync${gq(projectId)}`, { method: 'POST', timeoutMs: 180_000 }),
    // Частичный stage: patch — unified diff одного хунка/строк (сервер применяет с --recount)
    stageHunk: (projectId: string, patch: string) =>
      request<GitStatus>(`/projects/${projectId}/git/stage-hunk${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ patch }) }),
    unstageHunk: (projectId: string, patch: string) =>
      request<GitStatus>(`/projects/${projectId}/git/unstage-hunk${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ patch }) }),
    stashList: (projectId: string) =>
      request<GitStashEntry[]>(`/projects/${projectId}/git/stash${gq(projectId)}`),
    // Файлы отложенного (для просмотра в верхней зоне панели «Изменения», как у коммита)
    stashShow: (projectId: string, index: number) =>
      request<{ files: GitFileChange[] }>(`/projects/${projectId}/git/stash/${index}${gq(projectId)}`),
    stashPush: (projectId: string, message?: string) =>
      request<GitStatus>(`/projects/${projectId}/git/stash${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ message: message ?? null }) }),
    stashPop: (projectId: string, index: number) =>
      request<GitStatus>(`/projects/${projectId}/git/stash/${index}/pop${gq(projectId)}`, { method: 'POST' }),
    // Удаление стэша — необратимо (подтверждение на фронте)
    stashDrop: (projectId: string, index: number) =>
      request<GitStatus>(`/projects/${projectId}/git/stash/${index}${gq(projectId)}`, { method: 'DELETE' }),
    // Безопасная отмена коммита: новый обратный коммит; конфликт → 409 { error }
    revertCommit: (projectId: string, sha: string) =>
      request<GitStatus>(`/projects/${projectId}/git/commits/${sha}/revert${gq(projectId)}`, { method: 'POST', timeoutMs: 60_000 }),
    blame: (projectId: string, path: string) =>
      request<GitBlameLine[]>(`/projects/${projectId}/git/blame?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    // Данные входа в веб-UI Forgejo (пароль нужен: приватные репо анониму отдают 404)
    forgejoCredentials: (projectId: string) =>
      request<{ login: string; password: string | null }>(`/projects/${projectId}/git/forgejo-credentials`),
    resetForgejoPassword: (projectId: string) =>
      request<{ login: string; password: string }>(`/projects/${projectId}/git/forgejo-credentials/reset`, { method: 'POST', timeoutMs: 30_000 }),
    // История одного файла (--follow) — вкладка «История» просмотра файла
    fileLog: (projectId: string, path: string, limit = 100) =>
      request<GitLogEntry[]>(`/projects/${projectId}/git/file-log?path=${encodeURIComponent(path)}&limit=${limit}${gq(projectId, '&')}`),
    // Содержимое файла в конкретной версии («открыть, как было»); null — бинарь/нет файла
    fileAtCommit: (projectId: string, sha: string, path: string) =>
      request<{ content: string | null }>(`/projects/${projectId}/git/commits/${sha}/file?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    // Документный режим: вернуть файл к версии из коммита (в авто-режиме сразу коммитится)
    restoreFile: (projectId: string, sha: string, path: string) =>
      request<GitStatus>(`/projects/${projectId}/git/commits/${sha}/restore-file${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ path }), timeoutMs: 60_000,
      }),
    // Документный режим: «Сохранить сейчас» (✨-сообщение + push при авто-пуше)
    saveNow: (projectId: string) =>
      request<{ committed: boolean; sha?: string }>(`/projects/${projectId}/git/save-now${gq(projectId)}`, { method: 'POST', timeoutMs: 180_000 }),
    // LLM-помощь: описание коммита по staged-диффу / название стэша (генерация небыстрая — старт CLI)
    aiCommitMessage: (projectId: string) =>
      request<{ summary: string; description: string }>(`/projects/${projectId}/git/ai/commit-message${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    aiStashName: (projectId: string) =>
      request<{ name: string }>(`/projects/${projectId}/git/ai/stash-name${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    // git init + при настроенном Forgejo создание удалённого репозитория
    init: (projectId: string) =>
      request<{ status: GitStatus; htmlUrl: string | null }>(`/projects/${projectId}/git/init`, { method: 'POST', timeoutMs: 60_000 }),
    remote: (projectId: string) =>
      request<GitRemoteInfo>(`/projects/${projectId}/git/remote`),
    setAutoCommit: (projectId: string, enabled: boolean, push: boolean) =>
      request<{ autoCommit: boolean; autoPush: boolean }>(`/projects/${projectId}/git/auto-commit`, {
        method: 'PUT', body: JSON.stringify({ enabled, push }),
      }),
  },

  knowledge: {
    getStatus: (projectId: string) =>
      request<{ datasetId: string | null; documents: DifyDocument[]; total: number }>(`/projects/${projectId}/knowledge`),
    indexFile: (projectId: string, relativePath: string) =>
      request<{ datasetId: string; document: DifyDocument }>(
        `/projects/${projectId}/knowledge/index`,
        { method: 'POST', body: JSON.stringify({ relativePath }) }
      ),
    indexFolder: (projectId: string, relativePath: string) =>
      request<{ indexed: number; skipped: number; documents: DifyDocument[] }>(
        `/projects/${projectId}/knowledge/index-folder`,
        { method: 'POST', body: JSON.stringify({ relativePath }) }
      ),
    deleteDocument: (projectId: string, documentId: string) =>
      request<void>(`/projects/${projectId}/knowledge/documents/${documentId}`, { method: 'DELETE' }),
    deleteDataset: (projectId: string) =>
      request<void>(`/projects/${projectId}/knowledge`, { method: 'DELETE' }),
    setDocumentTags: (projectId: string, documentName: string, documentId: string, tags: string[]) =>
      request<void>(`/projects/${projectId}/knowledge/tags`, {
        method: 'PUT',
        body: JSON.stringify({ documentName, documentId, tags }),
      }),
  },

  // Раздел «Знания»: менеджер баз знаний Dify (личные + публичные), не путать с
  // проектным knowledge выше. Dify — источник истины; configured=false — не настроен.
  knowledgeBases: {
    list: () => request<KnowledgeListResponse>('/knowledge'),
    get: (id: string) => request<KnowledgeBaseDetail>(`/knowledge/${encodeURIComponent(id)}`),
    // Сгенерировать описание базы по составу документов (локальная модель / claude) и сохранить
    describe: (id: string) =>
      request<{ description: string }>(`/knowledge/${encodeURIComponent(id)}/ai/describe`, { method: 'POST' }),
    create: (dto: CreateKnowledgeBaseDto) =>
      request<{ id: string; title: string; visibility: string }>('/knowledge', {
        method: 'POST', body: JSON.stringify(dto),
      }),
    remove: (id: string) => request<void>(`/knowledge/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    // Добавить документ текстом
    addDocumentText: (id: string, name: string, text: string) =>
      request<{ id: string; name: string; indexingStatus: string }>(
        `/knowledge/${encodeURIComponent(id)}/documents`,
        { method: 'POST', body: JSON.stringify({ name, text }) },
      ),
    // Загрузить документ файлом (multipart — request() не ставит Content-Type для FormData)
    addDocumentFile: async (id: string, file: File, name?: string): Promise<{ id: string; name: string; indexingStatus: string }> => {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const form = new FormData();
      form.append('file', file);
      if (name) form.append('name', name);
      const res = await fetch(`/api/knowledge/${encodeURIComponent(id)}/documents/file`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
      return res.json();
    },
    removeDocument: (id: string, docId: string) =>
      request<void>(`/knowledge/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`, { method: 'DELETE' }),
    // Содержимое документа — сегменты (чанки) по порядку
    getDocument: (id: string, docId: string) =>
      request<KnowledgeDocumentContent>(`/knowledge/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`),
    // method: semantic (по смыслу) | fulltext (точный полнотекстовый)
    search: (id: string, q: string, method: 'semantic' | 'fulltext', topK = 8) =>
      request<{ items: KnowledgeSearchHit[] }>(
        `/knowledge/${encodeURIComponent(id)}/search?q=${encodeURIComponent(q)}&topK=${topK}&method=${method}`),
  },

  skills: {
    list: (projectId: string) => request<SkillsData>(`/projects/${projectId}/skills`),
    // Глобальные скиллы без привязки к проекту (для чатов вне проекта)
    listGlobal: () => request<SkillInfo[]>('/skills'),
    getSkill: (skillName: string) => request<{ content: string }>(`/skills/${skillName}`),
    saveSkill: (skillName: string, content: string) =>
      request<void>(`/skills/${skillName}`, { method: 'PUT', body: JSON.stringify({ content }) }),
    createSkill: (name: string, content: string) =>
      request<{ name: string }>('/skills', { method: 'POST', body: JSON.stringify({ name, content }) }),
    getAgent: (projectId: string, agentName: string) =>
      request<{ content: string }>(`/projects/${projectId}/agents/${agentName}`),
    saveAgent: (projectId: string, agentName: string, content: string) =>
      request<void>(`/projects/${projectId}/agents/${agentName}`, { method: 'PUT', body: JSON.stringify({ content }) }),
    createAgent: (projectId: string, name: string, content: string) =>
      request<{ name: string }>(`/projects/${projectId}/agents`, { method: 'POST', body: JSON.stringify({ name, content }) }),

    // --- Реестр skills.sh (обёртка npx skills) ---
    // Поиск навыков по реестру; owner — опциональное сужение по GitHub-владельцу.
    // Русский запрос переводится на английский (реестр англоязычный) — translatedQuery
    // показывает, что реально искали (null, если перевод не понадобился).
    // Все операции с реестром долгие (перевод LLM, клонирование репозиториев, подбор) —
    // задаём щедрый timeoutMs, иначе дефолтные 30с обрывают запрос и офлайн-слой
    // ложно решает, что мы офлайн («Действие недоступно офлайн»).
    find: (q: string, owner?: string) =>
      request<{ query: string; translatedQuery: string | null; results: RegistrySkill[] }>(
        `/skills/find?q=${encodeURIComponent(q)}${owner ? `&owner=${encodeURIComponent(owner)}` : ''}`,
        { timeoutMs: 90_000 }),
    // Установка навыка: scope 'project' требует projectId, 'global' — нет
    install: (source: string, skill: string, scope: 'project' | 'global', projectId?: string) =>
      request<{ installed: string; scope: string }>('/skills/install', {
        method: 'POST', body: JSON.stringify({ source, skill, scope, projectId }), timeoutMs: 180_000,
      }),
    uninstall: (skill: string, scope: 'project' | 'global', projectId?: string) =>
      request<void>(`/skills/installed?skill=${encodeURIComponent(skill)}&scope=${scope}${projectId ? `&projectId=${projectId}` : ''}`,
        { method: 'DELETE', timeoutMs: 90_000 }),
    // LLM-подбор: ровно один из personaId / projectId / query
    suggest: (ctx: { personaId?: string; projectId?: string; query?: string }) =>
      request<{ candidates: SkillSuggestion[] }>('/skills/suggest',
        { method: 'POST', body: JSON.stringify(ctx), timeoutMs: 200_000 }),
    // LLM-генерация нового навыка (SKILL.md) по свободному промпту: кандидат для превью, не сохраняется
    generate: (prompt: string) =>
      request<GeneratedSkill>('/skills/generate',
        { method: 'POST', body: JSON.stringify({ prompt }), timeoutMs: 200_000 }),
    // Установить навык персоне: глобальная установка + привязка (Skill)
    installForPersona: (personaId: string, source: string, skill: string) =>
      request<{ installed: string; bound: boolean; warning?: string }>(`/personas/${personaId}/skills`, {
        method: 'POST', body: JSON.stringify({ source, skill }), timeoutMs: 180_000,
      }),
  },

  workflow: {
    getAgents: (transcriptDir: string) =>
      request<{ agents: WorkflowAgentInfo[] }>(
        `/workflow-agents?transcriptDir=${encodeURIComponent(transcriptDir)}`
      ),
    // Полный поток одного агента (текст/thinking/инструменты) — лениво при раскрытии карточки
    getTimeline: (transcriptDir: string, agentId: string) =>
      request<{ blocks: WorkflowAgentBlock[] }>(
        `/workflow-agents/timeline?transcriptDir=${encodeURIComponent(transcriptDir)}&agentId=${encodeURIComponent(agentId)}`
      ),
  },

  sync: {
    list: (projectId: string) => request<SyncMark[]>(`/projects/${projectId}/sync`),
    add: (projectId: string, path: string, isDirectory: boolean) =>
      request<void>(`/projects/${projectId}/sync`, {
        method: 'POST',
        body: JSON.stringify({ path, isDirectory }),
      }),
    remove: (projectId: string, path: string) =>
      request<void>(`/projects/${projectId}/sync?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
  },

  reader: {
    // Контракт ADR-005 (docs/adr/ADR-005-link-reader-server.md): успех — { title, siteName?,
    // byline?, markdown }, отказ — { error: { code, httpStatus? } }. Отказ — ОЖИДАЕМЫЙ исход
    // (часть сайтов не читается, это норма, а не поломка), поэтому здесь гасим исключение
    // request() при non-2xx и возвращаем обе ветки как данные — вызывающему нечего ловить.
    read: (url: string) => readReaderPage(url),
    // Серверная проба встраиваемости (ADR-006 §1): POST /api/reader/embed-check, вердикт по
    // заголовкам финального ответа (X-Frame-Options / CSP frame-ancestors). Поле reason —
    // телеметрия/отладка; фронт на него НЕ ветвится — любой исход, кроме явного
    // embeddable:true (включая 401/403/429 и сетевые сбои), означает «не встраивается»
    // и тихо уводит панель в MD-режим.
    embedCheck: (url: string) => checkReaderEmbeddable(url),
  },
};

async function checkReaderEmbeddable(url: string): Promise<{ embeddable: boolean }> {
  try {
    const data = await request<{ embeddable?: unknown }>('/reader/embed-check', {
      method: 'POST',
      body: JSON.stringify({ url }),
    });
    return { embeddable: !!data && data.embeddable === true };
  } catch {
    // Сбой пробы — не исключение для показа, а сигнал идти MD-путём (ADR-006 §1)
    return { embeddable: false };
  }
}

function normalizeReaderError(raw: unknown, httpStatus?: number | null): { code: ReaderErrorCode; httpStatus?: number | null } {
  const code = raw && typeof raw === 'object' && 'code' in raw && typeof (raw as { code?: unknown }).code === 'string'
    ? (raw as { code: string }).code
    : null;
  const known = code != null && READER_ERROR_CODES.has(code as ReaderErrorCode);
  const rawStatus = raw && typeof raw === 'object' && 'httpStatus' in raw
    ? (raw as { httpStatus?: number | null }).httpStatus
    : null;
  return { code: known ? (code as ReaderErrorCode) : 'server-error', httpStatus: rawStatus ?? httpStatus ?? null };
}

const READER_ERROR_CODES = new Set<ReaderErrorCode>([
  'invalid-url', 'local-address', 'dns-failed', 'unreachable', 'tls-invalid',
  'timeout', 'auth-required', 'blocked-by-site', 'not-found', 'server-error',
  'too-many-redirects', 'not-a-page', 'pdf', 'too-large', 'not-readable',
]);

export type ReaderReadResult =
  | { ok: true; page: ReaderPage }
  | { ok: false; error: { code: ReaderErrorCode; httpStatus?: number | null } };

// Backend может сигналить отказ ЛИБО non-2xx статусом (request() бросает исключение,
// тело ошибки летит в err.body), ЛИБО 200 с discriminated union в теле — конвенция ещё
// не устоялась (эндпоинт делает Денис параллельно), поэтому распознаём оба варианта.
async function readReaderPage(url: string): Promise<ReaderReadResult> {
  try {
    const data = await request<ReaderPage | { error: unknown }>('/reader/read', {
      method: 'POST', body: JSON.stringify({ url }),
    });
    if (data && typeof data === 'object' && 'error' in data) {
      return { ok: false, error: normalizeReaderError((data as { error: unknown }).error) };
    }
    return { ok: true, page: data as ReaderPage };
  } catch (e) {
    const err = e as Error & { status?: number; body?: { error?: unknown } };
    return { ok: false, error: normalizeReaderError(err.body?.error, err.status) };
  }
}
