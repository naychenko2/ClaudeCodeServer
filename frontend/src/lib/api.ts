// ПОТОЛОК РОСТА (frontend-api-split-2026-11.md): файл только худеет. Лимит строк — в
// lib/__tests__/apiFileBudget.test.ts; он снижается после каждого выноса домена в
// features/<домен>/api.ts, поднимать его нельзя. Новые методы — сразу в features/<домен>/.
import type { Project, ProjectTag, ProjectSubject, ProjectSubjectEdit, SubjectRunState, Session, FileEntry, SyncMark, WorkflowAgentInfo, WorkflowAgentBlock, PermissionRule, SystemPromptPart, Task, CreateTaskDto, UpdateTaskDto, BoardColumn, BoardItem, NoteDetail, ExtractTasksResponse, Persona, CreatePersonaDto, UpdatePersonaDto, PersonaScope, PersonaMemoryType, PersonaMemoryEntry, PersonaMemoryHit, PersonaContract, PersonaWorkingFocus, PantheonTemplate, PersonaBinding, PersonaBindingDto, PersonaVoice, VoiceCandidatesResponse, PersonaBindingType, BindingTarget, TeamMemoryEntry, TeamMemoryType, TeamMemberDraft, PersonaAutomationRule, AutomationRuleDto, ProjectService, LaunchConfigEntry, GitStatus, GitBranchInfo, GitLogEntry, GitCommitDetail, GitStashEntry, GitFileChange, GitBlameLine, GitRemoteInfo, GitCommitPromptInfo, CodeGraph, PromptSnapshot, PromptSection, ApplyDefaultBindingsResult, LocalHandsChatStatus, BackgroundResult, ChangedBySession, ExternalPreviewLink, ExternalLinkIssued, SessionContextEntry, MapHygieneReport, MapHygieneApplyResult } from '../types';
import { localModelApi } from '../features/localModel/api';
import { providerAccountsApi } from '../features/providerAccounts/api';
import { accountApi } from '../features/account/api';
import { deployApi } from '../features/deploy/api';
import { hostControlApi } from '../features/hostControl/api';
import { modelsApi } from '../features/models/api';
import { spendApi } from '../features/spend/api';
import { modulesApi } from '../features/modules/api';
import { homeApi } from '../features/home/api';
import { chatAuxApi } from '../features/chat/api';
import { videoApi } from '../features/video/api';
import { devicesApi } from '../features/desktop/api';
import { spheresApi } from '../features/spheres/api';
import { specialtiesApi } from '../features/specialties/api';
import { knowledgeApi } from '../features/knowledge/api';
import { dossiersApi } from '../features/dossiers/api';
import { telemetryApi } from '../features/telemetry/api';
import { skillsApi } from '../features/skills/api';
import { plansApi } from '../features/plan/api';
import { imageGenerationApi } from '../features/imageGeneration/api';
import { mcpApi } from '../features/mcp/api';
import { readerApi } from '../features/reader/api';
import { pluginsApi } from '../features/plugins/api';
import { notesApi } from '../features/notes/api';
import { docsApi } from '../features/docs/api';
import { cachedToken, readStoredToken, request, writeStoredToken } from './offline';
import type { FeatureRun } from '../types/featureRun';
import type { FeaturesIndex, FeaturePage } from '../types/featureIndex';
import { forgetChatContextSession } from './chatContext/forget';
import { assertServerRoute, noteProject, noteProjects, projectRequest, projectRouteOf, uploadAgentAttachment } from './deviceAgent';
import type { HistoryCursorQuery, HistoryReply } from './historyTail';

// Query-строка курсора докачки истории: after обязателен, fp — только для настоящего курсора
const historyCursorParams = (q: HistoryCursorQuery) =>
  `after=${q.after}${q.fp ? `&fp=${encodeURIComponent(q.fp)}` : ''}`;

// Версия схемы миниатюр аватаров (?size=): bump-ит URL, чтобы браузеры, закэшировавшие
// оригинал (1,5 МБ) с immutable, сделали один свежий запрос и получили миниатюру.
// Сервер параметр может игнорировать; при смене схемы миниатюр поднять число.
const AVATAR_THUMB_SCHEME_VERSION = 2;

// Кандидат значка проекта (ADR-009 §2.2): name — имя lucide-иконки из белого списка.
// Сервер валидирует на входе; на фронте достаточно хранить как есть.
export interface GlyphCandidate {
  name?: string | null;
}

// Типы «Локальных моделей» живут в features/localModel/types.ts, методы — в features/localModel/api.ts
export type * from '../features/localModel/types';
// Типы «Аккаунтов провайдеров» (Higgsfield) живут в features/providerAccounts/types.ts
export type * from '../features/providerAccounts/types';
// Типы «Аккаунта» (слоты моделей) живут в features/account/types.ts
export type * from '../features/account/types';
// Тип внешнего модуля (ModuleInfo) живёт в features/modules/types.ts
export type * from '../features/modules/types';
// Типы питания и пульта удалённых команд живут в features/hostControl/types.ts
export type * from '../features/hostControl/types';
// Типы выкатки (трей-раннер и журнал из чата) живут в features/deploy/types.ts
export type * from '../features/deploy/types';

// Итог «Собрать архитектуру» (POST /projects/{id}/architecture/generate).
// graphBuiltAt — время снимка графа кода, которым помечена карта (ISO); added/matched —
// новые и сохранённые элементы: повторная сборка ручные описания не трогает.
export interface ArchitectureGenerateResult {
  modelPath: string;
  graphBuiltAt: string | null;
  generatedAt: string;
  containers: number;
  components: number;
  added: number;
  matched: number;
  connectionsAdded: number;
  // Проход 1 «Собрать архитектуру»: внешние системы-кандидаты (тег «кандидат»), элементы из
  // кода, которых больше нет (тег «нет в коде»), снятые пометки и удалённые руками (не
  // пересоздаются); missing/candidatesSkipped — имена для сводки
  candidates?: number;
  markedMissing?: number;
  unmarked?: number;
  skippedDeleted?: number;
  missing?: string[] | null;
  candidatesSkipped?: string[] | null;
  // Проход 2 (withAgent): задача исполнителю, его персона (null — без персоны-архитектора)
  // и код отказа; launch_failed — задача создана, но исполнитель не стартовал
  agentTaskId?: string | null;
  agentPersonaId?: string | null;
  agentError?: string | null;
  warnings?: ArchitectureModelFinding[] | null;
}

// Несостыковка модели (ArchitectureModelValidator на бэке): висящая ссылка, точка связи,
// которой у карточки нет, дубль id. Предупреждение — запись она не блокирует
export interface ArchitectureModelFinding {
  kind: 'dangling_parent' | 'dangling_connection' | 'dangling_flow_step' | 'unknown_handle' | 'duplicate_id';
  elementId: string;
  text: string;
}

// Модель раздела «Архитектура» (GET /projects/{id}/architecture/model). version — SHA-256
// содержимого файла; updatedBy — имя человека или «Сборка из кода».
export interface ArchitectureModelDto {
  exists: boolean;
  content: string | null;
  version: string | null;
  updatedAt: string | null;
  updatedBy: string | null;
  warnings?: ArchitectureModelFinding[];
}

export interface ArchitectureSaveResult {
  version: string;
  updatedAt: string | null;
  updatedBy: string | null;
  warnings?: ArchitectureModelFinding[];
}


// Потолок ожидания preview/start. DevServerService.StartAsync (у сервера и у агента один) ждёт
// порт по часам: не дольше DevServerLaunchPolicy.DotnetRunReadyTimeout (300 с, `dotnet run`
// со сборкой; прочим — 120 с), плюс одна последняя итерация (пауза 500 мс и проба до 400 мс
// на IPv4 и IPv6). Сверху запас на старт процесса и дорогу. Меняется серверный потолок —
// меняй и этот: обрыв раньше сервера покажет «нет связи» поверх живого старта.
const DEV_SERVER_PORT_WAIT_MS = 300_000;
const PREVIEW_START_TIMEOUT_MS = DEV_SERVER_PORT_WAIT_MS + 30_000;

// Потолок ожидания «Продолжить в новом чате»: ход записки-передачи — полноценный ход
// агента, бэкенд ждёт его до HandoffTurnTimeout = 15 мин (ChatContinueService.cs). Берём
// чуть больше (16 мин): обрыв по этому таймауту — RequestTimeoutError («Сервер не ответил
// за N с»), а не OfflineError, и приложение не уводится в офлайн (offline.ts:423–447).
const CONTINUE_TIMEOUT_MS = 16 * 60 * 1000;

// Потолок ожидания «Перенести в новый чат» (фича prompt-cache, шаг F-4): тёплый путь —
// видимый ход самого чата, бэкенд ждёт его до WarmTurnTimeout = 5 мин (ChatHandoffService.cs);
// холодный — быстрый раннер по транскрипту. Берём 6 мин: обрыв по этому таймауту —
// RequestTimeoutError, а не OfflineError (тот же приём, что у CONTINUE_TIMEOUT_MS).
const HANDOFF_TIMEOUT_MS = 6 * 60 * 1000;

export type { WorkflowAgentInfo, WorkflowAgentBlock };

// Типы «Знаний» (DifyDocument) живут в features/knowledge/types.ts
export type * from '../features/knowledge/types';
// Типы ридера (ReaderReadResult) живут в features/reader/types.ts
export type * from '../features/reader/types';
// Типы «Плагинов владельца» живут в features/plugins/types.ts; panelEntryPath — публичный
// экспорт и раньше (снаружи сейчас не импортируется), набор экспортов не меняем
export type * from '../features/plugins/types';
export { panelEntryPath } from '../features/plugins/api';

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
// Ответ сервера с проектом обновляет память «сервер или агент» (deviceAgent.ts)
function noted(project: Project): Project {
  noteProject(project);
  return project;
}

function gq(projectId: string, sep: '?' | '&' = '?'): string {
  return gitSessionCtx?.projectId === projectId ? `${sep}sessionId=${gitSessionCtx.sessionId}` : '';
}

// По-чатовой класс кэша для git-запросов проекта: ответ с ?sessionId= — это ответ про
// дерево конкретного чата, без sessionId — про корень проекта (общий класс).
// Ключ без чата в URL не должен множить по-чатовые записи (панель «Изменения»
// открывается в дереве чата, и там ключ иначе был бы новым на каждый чат).
function gitChatScope(projectId: string): string | undefined {
  return gitSessionCtx?.projectId === projectId ? gitSessionCtx.sessionId : undefined;
}

// Перезапись cc_token после смены пароля: старый токен сервер отозвал, а хранилище
// выбирает не вызывающий — токен пишется туда, откуда его читает readStoredToken.
export function setStoredToken(token: string) {
  if (typeof window === 'undefined') return;
  writeStoredToken(token);
}

// Резюме фичи (W5-F2, «Точка 6 · резюме»): результат модельного анализа хронологии.
// text — markdown с разделами «Сделано», «Где застряло», «Дальше» (или голые пункты
// без заголовков — пока так просит промпт feature-summary); model=null — локальная
// модель; cached — результат взят из кэша бэка, без нового прогона. Ошибки: 502 —
// модель не ответила (причина в error), 503 — подсистема выключена, 404 — фичи нет.
export interface FeatureSummaryResult {
  text: string;
  generatedAt: string;
  model: string | null;
  cached: boolean;
  usage?: unknown;
  // Число событий хронологии, на которых построено резюме (W4-ANB): для шапки
  // «по N событиям» (бейдж устаревания — без числа). Бэк отдаёт всегда; optional —
  // защита от ответов старой версии сервера.
  eventCount?: number;
}

// Projects
export const api = {
  // Методы «Аккаунта» (auth/push/users/settings/слоты моделей) — в features/account/api.ts
  ...accountApi,

  // Расход токенов и лимиты подписок — в features/spend/api.ts
  ...spendApi,

  // Провайдеры, модели и исполнитель фоновых действий — в features/models/api.ts
  ...modelsApi,

  ...localModelApi,


  // Питание, пульт команд, бэкапы и файлы хоста — в features/hostControl/api.ts
  ...hostControlApi,

  // Выкатка трей-раннером и журнал выкатки из чата — в features/deploy/api.ts
  ...deployApi,

  ...providerAccountsApi,

  // Генератор картинок по местам — в features/imageGeneration/api.ts
  ...imageGenerationApi,

  // Модули, MF-remote подсистем и фича-флаги — в features/modules/api.ts
  ...modulesApi,

  // Личный реестр MCP-серверов — в features/mcp/api.ts
  ...mcpApi,

  // Устройства владельца и сопряжение — в features/desktop/api.ts
  ...devicesApi,

  // Плагины владельца (ADR-031 §2.3, §4) — в features/plugins/api.ts
  ...pluginsApi,

  // Раздел «Видео» — в features/video/api.ts
  ...videoApi,

// Ссылки внешнего доступа — СКВОЗНЫЕ по проектам владельца, поэтому не под /projects/{id}:
  // забытая открытой витрина в соседнем проекте иначе осталась бы невидимой.
  externalPreview: {
    list: () => request<{ enabled: boolean; links: ExternalPreviewLink[] }>('/preview/external-links'),
    revoke: (jti: string) =>
      request<{ revoked: number }>(`/preview/external-links/${encodeURIComponent(jti)}`, { method: 'DELETE' }),
    revokeAll: () => request<{ revoked: number }>('/preview/external-links', { method: 'DELETE' }),
  },

  // Специальности персон и настройки к ним — в features/specialties/api.ts
  ...specialtiesApi,

  projects: {
    list: () => request<Project[]>('/projects').then(list => { noteProjects(list); return list; }),
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
      git?: { enableGit?: boolean; gitAutoCommit?: boolean; gitAutoPush?: boolean }, color?: string | null,
      // ADR-016 §3.4: локальный проект создаётся с привязкой к устройству. deviceId
      // берётся из реестра ADR-008. Контракт 3.1 (CreateProjectRequest): путь НА устройстве
      // едет в том же rootPath, отдельного поля под него нет — local.rootPath заменяет
      // аргумент rootPath. Передавать, только когда создаётся локальный проект.
      local?: { deviceId: string; rootPath: string }) =>
      request<Project>('/projects', { method: 'POST', body: JSON.stringify({
        name, rootPath: local ? local.rootPath : rootPath, createDirectory, groupId, ...git, color,
        deviceId: local?.deviceId,
      }) }).then(noted),
    update: (id: string, data: { name?: string; rootPath?: string; systemPrompt?: string; showHiddenFiles?: boolean; permissionRules?: PermissionRule[]; groupId?: string | null; color?: string | null; mcpServersOn?: string[]; autoImportDossiers?: boolean; mcpCatalogConfirmed?: boolean }) =>
      request<Project>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(data) }).then(noted),
    // Перепривязка устройства (ADR-016 §3.4): deviceId=null — отвязать проект
    // (сделать серверным). При существующих чатах — 409 с `{error}`. rootPath — новый путь
    // (на устройстве при привязке); без него бэк оставит прежний. Возвращает обновлённый
    // Project с пересобранной матрицей capabilities.
    setDevice: (id: string, body: { deviceId: string | null; rootPath?: string }) =>
      request<Project>(`/projects/${encodeURIComponent(id)}/device`, {
        method: 'PUT', body: JSON.stringify(body),
      }).then(noted),
    // Тумблер рук локального проекта (ADR-016 §7): включение сервер пускает только по
    // матрице (иначе 400 с причиной), выключение — всегда
    setHands: (id: string, enabled: boolean) =>
      request<Project>(`/projects/${encodeURIComponent(id)}/hands`,
        { method: 'PUT', body: JSON.stringify({ enabled }) }),
    // Реестр общих тегов проекта: перезапись целиком (бэк нормализует order по позиции
    // массива и валидирует уникальность имён без учёта регистра)
    updateTags: (id: string, registry: ProjectTag[]) =>
      request<Project>(`/projects/${encodeURIComponent(id)}/tags`, {
        method: 'PUT', body: JSON.stringify(registry),
      }),
    // --- Темы чатов (docs/features/chat-subjects.md) ---
    // Реестр целиком: правка, порядок, новые темы человека, удаление (тема, которой нет в
    // запросе). 400 — плохое имя или дубль, текст причины в теле
    subjects: {
      list: (id: string) => request<ProjectSubject[]>(`/projects/${encodeURIComponent(id)}/subjects`),
      replace: (id: string, subjects: ProjectSubjectEdit[]) =>
        request<ProjectSubject[]>(`/projects/${encodeURIComponent(id)}/subjects`, {
          method: 'PUT', body: JSON.stringify({ subjects }),
        }),
      // Перенос чата: subjectId — в тему, null — «убрать из темы» (оба закрепляют);
      // release — «Вернуть автоматике». Ответ — обновлённая сессия
      moveSession: (id: string, sessionId: string, body: { subjectId: string | null } | { release: true }) =>
        request<Session>(`/projects/${encodeURIComponent(id)}/sessions/${encodeURIComponent(sessionId)}/subject`, {
          method: 'PUT', body: JSON.stringify(body),
        }),
      // «Разложить сейчас» / «Разложить заново» (снять назначения модели): 202 с состоянием;
      // проход уже идёт — тоже 202, started:false
      run: (id: string, resetAi = false) =>
        request<SubjectRunState>(`/projects/${encodeURIComponent(id)}/subjects/${resetAi ? 'reset-ai' : 'run'}`, { method: 'POST' }),
      // Опрос прогресса — живой: состояние из офлайн-кэша показало бы давно кончившийся проход
      runState: (id: string) => request<SubjectRunState>(`/projects/${encodeURIComponent(id)}/subjects/run`, { live: true }),
    },
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
    setIconMode: (id: string, kind: 'initials' | 'glyph' | 'image') =>
      request<Project>(`/projects/${encodeURIComponent(id)}/icon/mode`, {
        method: 'POST', body: JSON.stringify({ kind }),
      }),
    // Своя картинка иконкой (svg/png/jpg/webp/ico до 512 КБ): формат сервер определяет
    // по байтам, имя файла роли не играет. Ответ — проект с icon.kind = 'image'.
    uploadIconImage: (id: string, file: File) => {
      const form = new FormData();
      form.append('file', file, file.name || 'icon');
      return request<Project>(`/projects/${encodeURIComponent(id)}/icon/upload`, {
        method: 'POST', body: form, timeoutMs: 60_000,
      });
    },
    // URL загруженной картинки для <img>: токен через ?access_token= (заголовок <img> не
    // поставит), v — cache-buster по имени файла. null — картинку не загружали.
    iconImageUrl: (project: Project): string | null => {
      if (!project.icon?.imageFile) return null;
      const token = readStoredToken();
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', project.icon.imageFile);
      return `/api/projects/${encodeURIComponent(project.id)}/icon/image?${params}`;
    },
    getBuiltinPrompt: () => request<{ content: string }>('/projects/builtin-prompt'),
    // --- Уборка карты проекта (CLAUDE.md), фронтовая часть (docs/research/project-map-hygiene-plan-2026-09.md) ---
    // Скан — мгновенно, без модели и без трат. Review/apply — за фич-флагом и закрыты
    // на сервере (404 при выключенной фиче, Р11 плана). Грабли: дефолтный timeoutMs у
    // request() — 30 с (offline.ts:318), а ход модели идёт до минуты — без явного 180_000
    // обрыв покажется пользователю как «Действие недоступно офлайн» (образец — analyzePrompt
    // строкой выше). 409 на обоих путях — файл изменился между сканом и запросом, тело
    // несёт error: "staleBaseSha" и актуальный baseSha — фронт читает err.body.baseSha.
    mapHygiene: {
      scan: (id: string) =>
        request<MapHygieneReport>(`/projects/${encodeURIComponent(id)}/map-hygiene/scan`),
      // Отдаёт ПОЛНЫЙ отчёт той же формы, что scan (и что поле scan в ответе apply) —
      // с вклеенными suggestions[].modelSays и серверным modelNote. Generic здесь
      // объявлен руками, tsc ему верит на слово: расхождение с бэком не ловится ничем,
      // кроме живого запуска, — сторож на стороне сервера (ProjectMapControllerTests,
      // «Ревью_ОтдаётПолныйОтчёт_ТойЖеФормыЧтоСкан»)
      review: (id: string, baseSha: string) =>
        request<MapHygieneReport>(`/projects/${encodeURIComponent(id)}/map-hygiene/review`, {
          method: 'POST', body: JSON.stringify({ baseSha }), timeoutMs: 180_000,
        }),
      apply: (id: string, baseSha: string, ids: string[]) =>
        request<MapHygieneApplyResult>(`/projects/${encodeURIComponent(id)}/map-hygiene/apply`, {
          method: 'POST', body: JSON.stringify({ baseSha, ids }), timeoutMs: 180_000,
        }),
    },

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
      const token = readStoredToken();
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
    // «Собрать из кода» (раздел «Архитектура»): стартовая модель Viaduct из графа кода,
    // слияние с docs/architecture/model.viaduct.json без затирания ручных описаний.
    // Если графа нет, бэкенд строит его в том же запросе (на CCS ~1.5 мин), отсюда таймаут.
    // 409 code=model_corrupt — файл модели битый, 503 code=graph_unavailable — графа нет.
    // withAgent — проход 2: задача исполнителю (персона-архитектор, если есть). 409
    // code=build_in_progress (+agentTaskId, result) — агент уже собирает; 503
    // code=agent_unavailable (+result) — собрано без агента.
    architectureGenerate: (id: string, withAgent = false) =>
      request<ArchitectureGenerateResult>(`/projects/${encodeURIComponent(id)}/architecture/generate`,
        { method: 'POST', body: JSON.stringify({ withAgent }), timeoutMs: 300_000 }),
    // Хранилище модели раздела «Архитектура»: content — байты файла как есть (null —
    // модели ещё нет). live: устаревшая модель из офлайн-кэша дала бы ложный конфликт.
    architectureModel: (id: string) =>
      request<ArchitectureModelDto>(`/projects/${encodeURIComponent(id)}/architecture/model`, { live: true }),
    // Запись от версии baseVersion (null — «модели не было»); 409 code=version_conflict,
    // в err.body.current — состояние файла на сервере.
    architectureSaveModel: (id: string, content: string, baseVersion: string | null) =>
      request<ArchitectureSaveResult>(`/projects/${encodeURIComponent(id)}/architecture/model`,
        { method: 'PUT', body: JSON.stringify({ content, baseVersion }) }),
    // Preview: сервисы проекта (инференс из манифестов + сохранённые в .claude/launch.json).
    // У локального проекта — у агента устройства (projectRequest); внешнего доступа там нет
    services: (id: string) =>
      projectRequest<{ services: ProjectService[]; activeServiceId: string | null }>(`/projects/${id}/services`),
    previewStart: (id: string, svc: {
      serviceId: string; name: string; command: string; args: string[];
      cwd?: string; port?: number; autoPort?: boolean; env?: Record<string, string>;
    }) =>
      projectRequest<{ status: string; port?: number; error?: string; serviceId: string }>(`/projects/${id}/preview/start`, {
        method: 'POST', body: JSON.stringify(svc), timeoutMs: PREVIEW_START_TIMEOUT_MS,
      }),
    previewStop: (id: string, serviceId: string) =>
      projectRequest<{ status: string }>(`/projects/${id}/preview/stop`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    previewStatus: (id: string) =>
      projectRequest<{ running: { serviceId: string; name: string; port: number | null; status: string; error: string | null }[]; activeServiceId: string | null }>(`/projects/${id}/preview/status`),
    previewActive: (id: string, serviceId: string) =>
      projectRequest<{ activeServiceId: string }>(`/projects/${id}/preview/active`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    // Сервис поднят вне продукта (Rider, терминал) — показать его в превью.
    // Порт выбирает сервер по конфигурации сервиса, клиент его не передаёт.
    previewActiveExternal: (id: string, serviceId: string) =>
      projectRequest<{ activeServiceId: string; port: number }>(`/projects/${id}/preview/active-external`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    // Внешний доступ: ссылка на сервис по отдельному поддомену. Порт, как и выше, выбирает
    // сервер — принимать его от клиента нельзя, эндпоинт стал бы туннелем наружу.
    // Остановить процесс, поднятый вне продукта. 409 с needsConfirm — порт держит чужой
    // процесс, и гасить его можно только после явного согласия человека.
    previewStopExternal: (id: string, serviceId: string, confirm = false) =>
      projectRequest<{ status: string; pid: number }>(`/projects/${id}/preview/stop-external`, {
        method: 'POST', body: JSON.stringify({ serviceId, confirm }),
      }),
    previewExternalLink: (id: string, serviceId: string) =>
      projectRequest<ExternalLinkIssued>(`/projects/${id}/preview/external-link`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    getLaunchConfig: (id: string) =>
      projectRequest<{ configurations: LaunchConfigEntry[] }>(`/projects/${id}/launch-config`),
    putLaunchConfig: (id: string, configurations: LaunchConfigEntry[]) =>
      projectRequest<{ configurations: LaunchConfigEntry[] }>(`/projects/${id}/launch-config`, {
        method: 'PUT', body: JSON.stringify({ configurations }),
      }),
  },

  // Доска агентов (диспетчерская)
  // Мелкие домены «Дома» (сводка, бриф, поиск, онбординг, «стена», быстрые фразы,
  // продуктовая история) — в features/home/api.ts
  ...homeApi,
  board: {
    // live: true — ответ крупный (замер часа работы: ~0,86 МБ), а доска читается
    // на каждый чих статуса. В офлайн-кэш IndexedDB такая частота писала 258 МБ за
    // час, и подставленный прошлый ответ выдавал бы доску «кто сейчас работает» за
    // текущую — ровно тот класс вранья, из-за которого live: живёт у статуса
    // выкатки и питания.
    agents: () => request<{ items: BoardItem[] }>('/board/agents', { live: true }),
    interrupt: (sessionId: string) =>
      request<void>(`/board/agents/${sessionId}/interrupt`, { method: 'POST' }),
    allowPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/allow`, { method: 'POST' }),
    denyPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/deny`, { method: 'POST' }),
  },

  // Группы проектов и сферы — в features/spheres/api.ts
  ...spheresApi,

  tasks: {
    // Все задачи пользователя (для календаря)
    listAll: (from?: string, to?: string) => {
      const qs = new URLSearchParams();
      if (from) qs.set('from', from);
      if (to) qs.set('to', to);
      const q = qs.toString();
      // live: список живёт одним экземпляром в сторе (tasks:all), пассивный GET-кэш его не дублирует
      return request<Task[]>(`/tasks${q ? `?${q}` : ''}`, { live: true });
    },
    listByProject: (projectId: string) => request<Task[]>(`/projects/${projectId}/tasks`, { live: true }),
    // Поиск по названию/описанию/меткам на сервере (по ПОЛНОМУ описанию, которого нет
    // в облегчённом списке); элементы ответа — облегчённые, нужны id совпавших
    // live: ключ кэша зависит от текста запроса — без live каждый ввод оставлял бы новую запись в IndexedDB
    search: (q: string) => request<Task[]>(`/tasks?q=${encodeURIComponent(q)}`, { live: true }),
    // Задачи, порученные персоне-исполнителю (assignee=claude + personaId)
    listByPersona: (personaId: string) =>
      request<Task[]>(`/tasks?personaId=${encodeURIComponent(personaId)}`, { live: true }),
    // Агрегаты фич чата (режим «Исполнение фичи»): готовые `FeatureRun` из бэка.
    // sessionId — id сессии-чата; без него — все фичи владельца.
    getFeatures: (sessionId?: string) =>
      request<FeatureRun[]>(`/tasks/features${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, { live: true }),
    // Индекс фич (фасет «Фичи» и плашка в чате): строки фич + связи «чат × фича».
    // projectId — область ответа; без него — все проекты владельца и чаты вне проектов.
    // 503 при выключенной подсистеме Tasks (как getFeatures).
    featuresIndex: (projectId?: string) =>
      request<FeaturesIndex>(`/tasks/features/index${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`),
    // Страница фичи (волна 2 «Обзор фич»): агрегат `FeatureRun` одной фичи + лента событий.
    // fid — id эпика; eventsLimit — «all»/«0» → все, N (целое > 0) → N, иначе 100 (бэк).
    // 404 — фичи нет/чужая/удалена (один 404); 503 — подсистема Tasks выключена.
    // signal — отмена в-полёте запроса (быстрая смена fid в useFeaturePage): не плодим
    // параллельных GET, устаревший отменяется (AbortError игнорируется в featurePage).
    getFeature: (fid: string, eventsLimit?: string, signal?: AbortSignal) =>
      request<FeaturePage>(
        `/tasks/features/${encodeURIComponent(fid)}${eventsLimit ? `?eventsLimit=${encodeURIComponent(eventsLimit)}` : ''}`,
        signal ? { signal, live: true } : { live: true },
      ),
    // Резюме фичи моделью (W5-F2, «Точка 6 · резюме»): POST summary[?force=true].
    // force=false — берётся кэш бэка, если есть; force=true — новый прогон. 200 —
    // FeatureSummaryResult; 502 — модель не ответила (текст причины в error); 503 —
    // подсистема выключена; 404 — фичи нет. signal — «Остановить» в шапке резюме.
    // Клиентский таймаут 240 с — по конвенции модельные вызовы задают свой лимит (см. analyze-
    // запросы выше). Серверный бюджет цепочки выше: локаль 45 с + облако 180 с (LocalActionCatalog),
    // а при таймаоте облачного шага он повторяется один раз (CheapTextRunner.cs:381-386) — до 405 с;
    // на маршруте Model — ещё больше. Клиентский таймаут обрывает только ожидание ответа: сервер
    // продолжает считать и кладёт результат в свой кэш (FeatureSummaryService.cs:76), когда
    // доработает (до ~405 с). Повтор запроса до этой записи встаёт в single-flight и ждёт тот же
    // расчёт (FeatureSummaryService.cs:51-59), а не даёт мгновенный кэш-хит; хит — только после
    // попадания в кэш. Это не задуманное поведение, а известная проблема: на HTTP/1.1 отмена
    // клиента не доходит до модельного вызова, работа продолжается (открытый дефект 1f065f3c).
    featureSummary: (fid: string, force?: boolean, signal?: AbortSignal) =>
      request<FeatureSummaryResult>(
        `/tasks/features/${encodeURIComponent(fid)}/summary${force ? '?force=true' : ''}`,
        { method: 'POST', ...(signal ? { signal } : {}), timeoutMs: 240_000 },
      ),
    // Мерж фичи (волна 2 «Обзор фич», POST /api/tasks/features/{fid}/merge):
    // создаёт задачу role=merge с персоной DevopsExecutor и запускает её. Fail-closed:
    // 503 (подсистема выключена), 404 (нет/чужой эпик), 409 (финал не принят / 0 или >1
    // исполнителей DevOps) — причина в err.body.error (и err.message). Идемпотентна:
    // открытая merge-задача → вернуть её (повторный вызов — та же задача, вторую не заводим).
    mergeFeature: (fid: string) =>
      request<Task>(`/tasks/features/${encodeURIComponent(fid)}/merge`, { method: 'POST' }),
    // Пауза/черновик фичи (W4-HOLD-F2, ручки W4-HOLD-B2): POST hold {reason} —
    // удержание (reason: 'manual' — пауза человека, 'draft' — черновик), DELETE — снять.
    // Fail-closed: 403 (сервисный токен — удержание ставит только человек), 404, 409
    // (merged/closed/merging; draft после запуска шага; manual поверх draft; снятие без
    // удержания) — причина в body.error. 200 — ходы, прерванные этой паузой:
    // { interrupted: [taskId] } (контракт §1, :364).
    setFeatureHold: (fid: string, reason: string) =>
      request<{ interrupted: string[] }>(`/tasks/features/${encodeURIComponent(fid)}/hold`, { method: 'POST', body: JSON.stringify({ reason }) }),
    // Снятие паузы (W4-HOLD-F2, хвост R-HOLD-H): 200 — { resumed: [taskId], expired: [taskId] }:
    // resumed — задачи, которым пауза мешала запуститься и теперь разрешены (не очередь
    // автостарта), expired — те, чьё ожидание истекло (фронтом не показывается).
    clearFeatureHold: (fid: string) =>
      request<{ resumed: string[]; expired: string[] }>(`/tasks/features/${encodeURIComponent(fid)}/hold`, { method: 'DELETE' }),
    // projectId === null → личная задача (вне проекта)
    create: (projectId: string | null, dto: CreateTaskDto) =>
      request<Task>(projectId ? `/projects/${projectId}/tasks` : '/tasks', { method: 'POST', body: JSON.stringify(dto) }),
    // live: true — полная карточка (description + resultMarkdown, ~90 КБ) в пассивный GET-кэш не
    // пишется: он без вытеснения. Открытые полные задачи держит стор (LRU FULL_TASKS_CAP, снимок
    // tasks:all); офлайн ensureFullTask получает OfflineError → состояние «error» с «Повторить».
    get: (taskId: string) => request<Task>(`/tasks/${taskId}`, { live: true }),
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

  // Заметки (Obsidian-совместимая база знаний) — в features/notes/api.ts
  ...notesApi,

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
    // size — размер показа (device px, 0 — оригинал): сервер отдаёт миниатюру ближайшей
    // ступени (64/128/256), сгенерированную один раз; v в URL — имя файла, поэтому
    // смена аватара сбрасывает кэш.
    avatarUrl: (persona: Persona, size = 0): string | null => {
      if (persona.avatar?.kind !== 'image' || !persona.avatar.imageFile) return null;
      const token = cachedToken();
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', persona.avatar.imageFile);
      if (size > 0) {
        params.set('size', String(size));
        // Версия схемы миниатюр: сменила URL у всех, кто закэшировал оригинал (см. константу).
        params.set('t', String(AVATAR_THUMB_SCHEME_VERSION));
      }
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
      const token = readStoredToken();
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      params.set('v', persona.avatar.originalFile);
      return `/api/personas/${encodeURIComponent(persona.id)}/avatar/original?${params}`;
    },
    // URL картинки-кандидата (галерея генерации) для <img>: токен через ?access_token=
    avatarCandidateUrl: (id: string, file: string): string => {
      const token = readStoredToken();
      const params = new URLSearchParams();
      if (token) params.set('access_token', token);
      return `/api/personas/${encodeURIComponent(id)}/avatar/candidate/${encodeURIComponent(file)}?${params}`;
    },
    // Быстрое создание персоны по свободному промпту: LLM заполняет роль/имя/описание/
    // характер/приветствие/цвет, фото-аватар генерируется автоматически.
    // Запрос долгий (LLM ~10-40с + fal ~10-40с, до ~90с) — таймаут расширен. 502 — можно повторить.
    quickCreate: (body: { prompt: string; scope?: PersonaScope; projectId?: string; sphereId?: string }) =>
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
    // Подбор голосов по характеру: до трёх обычных кандидатов и отдельно живой (LiveTTS).
    // Без id: характер в момент подбора ещё не сохранён, а у новой персоны id нет вовсе.
    // exclude — уже показанные голоса («Подобрать ещё»); пустой список — не ошибка
    suggestVoiceCandidates: (req: {
      name?: string; role?: string; description?: string; character?: string; tone?: string; exclude?: string[];
    }) =>
      request<VoiceCandidatesResponse>('/personas/ai/voice/candidates', {
        method: 'POST', body: JSON.stringify(req),
      }),
    // Личный голос персоны; пустой объект снимает его — персона снова говорит голосом инстанса
    setVoice: (id: string, voice: PersonaVoice | null) =>
      request<Persona>(`/personas/${encodeURIComponent(id)}/voice`, {
        method: 'PUT', body: JSON.stringify(voice ?? {}),
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
    // Применить типовые умения специальности к существующей персоне (кнопка
    // «Применить типовые» на вкладке «Умения»). Материализует профиль роли в личные
    // привязки поверх текущих; без специальности бэк отвечает 400.
    applyDefaultBindings: (id: string) =>
      request<ApplyDefaultBindingsResult>(`/personas/${encodeURIComponent(id)}/bindings/apply-defaults`, {
        method: 'POST',
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


  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    // Начальное состояние бейджа рук локального проекта; дальше — событие hands_status
    handsStatus: (sessionId: string) =>
      request<LocalHandsChatStatus>(`/sessions/${encodeURIComponent(sessionId)}/hands-status`),
    // Подобрать значки-иконки чатам проекта без них (действие AI-палитры «Проставить значки чатов»)
    iconBatch: (projectId: string) =>
      request<{ processed: number; skipped: number }>(`/projects/${encodeURIComponent(projectId)}/sessions/icon-batch`, { method: 'POST' }),
    create: (projectId: string, mode = 'acceptEdits', resumeSessionId?: string, name?: string, model?: string, agentName?: string, effort?: string) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, agentName, effort }),
      }),
    update: (projectId: string, sessionId: string, data: { name?: string | null; model?: string | null; effort?: string | null; expiresAfterMinutes?: number | null; tags?: string[]; excludeFromDossiers?: boolean | null; notificationsMuted?: boolean; voiceMode?: boolean; voiceStyle?: string }) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, sessionId: string) =>
      request<void>(`/projects/${projectId}/sessions/${sessionId}`, { method: 'DELETE' })
        .then(r => { forgetChatContextSession(sessionId); return r; }),
    // Состав контекста чата (фича chat-context) с признаками missing. live: true —
    // обязательный: офлайн-кэш после переключения чата подставил бы чужой/старый состав
    getContext: (projectId: string, sessionId: string) =>
      request<SessionContextEntry[]>(`/projects/${projectId}/sessions/${sessionId}/context`, { live: true }),
    // Идемпотентная замена состава целиком. Ответ PUT — записи БЕЗ missing (бэкенд его
    // считает только в GET), поэтому после записи дёргаем GET — он же обновит кеш
    putContext: async (projectId: string, sessionId: string, entries: Pick<SessionContextEntry, 'type' | 'id' | 'title'>[]) =>
      { await request<void>(`/projects/${projectId}/sessions/${sessionId}/context`, {
        method: 'PUT',
        body: JSON.stringify(entries),
      }); return api.sessions.getContext(projectId, sessionId); },
    getHistory: (projectId: string, sessionId: string) =>
      request<unknown[]>(`/projects/${projectId}/sessions/${sessionId}/history`, { chatScope: sessionId }),
    // Докачка хвоста истории по курсору сервера (см. lib/historyTail). after=-1 — первая
    // загрузка (полная история + якорь), её можно кэшировать офлайн; настоящий курсор — live:
    // хвост в офлайн-кэше был бы мусором (подстановка куска вместо ленты).
    // Полная история — по-чатовой класс кэша: она тяжёлая (до 2,5 МБ на чат), и без своего
    // потолка по-чатовые записи съедали бы общий бюджет кэша и росли вместе с числом чатов.
    // onBytes — необязательный колбэк веса ответа (мера та же, что у офлайн-кэша)
    getHistoryTail: (projectId: string, sessionId: string, q: HistoryCursorQuery, onBytes?: (n: number) => void) =>
      request<HistoryReply>(
        `/projects/${projectId}/sessions/${sessionId}/history?${historyCursorParams(q)}`,
        { live: q.after >= 0, chatScope: q.after < 0 ? sessionId : undefined, onBytes }),
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


  // Чаты вне проекта (project-less)
  chats: {
    list: () => request<Session[]>('/chats'),
    // id чатов (в т.ч. проектных) с живой фоновой работой: отдельно агенты, отдельно фоновые
    // команды Bash. Снимок нужен потому, что событие bg_agents_presence приходит только на
    // смене состояния — открывший список позже старта иначе о нём не узнает
    agentsPresence: () => request<{ agents: string[]; commands: string[] }>('/chats/agents-presence'),
    get: (id: string) => request<Session>(`/chats/${id}`, { chatScope: id }),
    create: (mode?: string, resumeSessionId?: string, name?: string, model?: string, effort?: string) =>
      request<Session>('/chats', {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, effort }),
      }),
    update: (id: string, data: { name?: string | null; model?: string | null; effort?: string | null; pinned?: boolean; expiresAfterMinutes?: number | null; notificationsMuted?: boolean; voiceMode?: boolean; voiceStyle?: string }) =>
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
    createGroup: (personaIds: string[], mode?: string, name?: string) =>
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
    // Полный снимок пульса волны (Э2 КР-наблюдаемости): пульс + список задач +
    // пороги liveness. Зовётся при КАЖДОМ открытии поповера бейджа — кэшировать
    // нельзя. live: true + cache: 'no-store' по той же причине, что и у статуса
    // выкатки: офлайн-слой кладёт GET в IndexedDB и при обрыве подставил бы
    // прошлый ответ — поповер показывал бы «в работе, 5 мин» при мёртвой волне.
    // Для индикатора живости это хуже, чем не показать ничего
    getTeamWaveSnapshot: (id: string) =>
      request<import('../types').TeamWaveSnapshot>(
        `/chats/${id}/team-wave-snapshot`,
        { cache: 'no-store', live: true },
      ),
    // Перезапуск одной под-задачи волны (КР-наблюдаемость, этап 3): та же перевыдача,
    // что у провала хода — потолок попыток общий. 409 несёт человеческий текст
    // (живая задача, завершена, повторный клик) — показываем его, а не пустую кнопку
    restartTeamWaveTask: (id: string, taskId: string) =>
      request<import('../types').TeamTaskRestartResponse>(
        `/chats/${id}/team-wave/tasks/${taskId}/restart`,
        { method: 'POST', live: true },
      ),
    // Перезапуск волны: без confirm — предупреждение о живых исполнениях (liveTasks),
    // повтор с confirm=true останавливает их и перевыдаёт незакрытое. Done не трогается
    restartTeamWave: (id: string, confirm: boolean) =>
      request<import('../types').TeamWaveRestartResponse>(
        `/chats/${id}/team-wave/restart`,
        { method: 'POST', body: JSON.stringify({ confirm }), live: true },
      ),
    // Перезапуск зависшего хода штаба: kill → валидация транскрипта → новый ход с
    // --resume, отложенные сообщения уходят. startFresh=true — «начать ход заново» при
    // повреждённом транскрипте (409 code=transcript_damaged). Долгий: kill и ожидание
    // смерти процесса могут занять десятки секунд
    restartTeamWaveTurn: (id: string, startFresh = false) =>
      request<import('../types').TeamTurnRestartResponse>(
        `/chats/${id}/team-wave/restart-turn`,
        { method: 'POST', body: JSON.stringify({ startFresh }), live: true, timeoutMs: 90_000 },
      ),
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
    // Ветвление чата (фича chat-branch): новый чат с копией истории до userMessageIndex
    // (включительно или нет — задаёт include). anchorText — сверка «тот ли это шаг»: на
    // расхождении текста сервер отдаёт 409, без молчаливого отреза не там. Имя опционально,
    // дефолт — «{имя оригинала} — ветка». Ответ — chatId+claudeSessionId новой ветки и draft
    // (null при include='turn', текст исходного промпта при include='beforePrompt' для
    // композера). Серверные 400 «ветвить нечего» / 409 «идёт ход» / 409 «границу не
    // сопоставить» несут человеческий текст — он доезжает до вызывающего кода через Error.message
    // (request() пробрасывает server body как Error & { body })
    branch: (
      id: string,
      body: {
        userMessageIndex: number;
        anchorText: string;
        include: 'turn' | 'beforePrompt';
        name?: string;
      },
    ) =>
      request<{ chatId: string; projectId: string | null; claudeSessionId: string; draft: string | null }>(
        `/chats/${id}/branch`,
        { method: 'POST', body: JSON.stringify(body) },
      ),
    // «Продолжить в новом чате»: агент пишет записку-передачу (один ход, десятки секунд),
    // платформа открывает новый чат той же персоны. 409 — чат занят, 400 — нет ходов агента;
    // оба несут человеческий текст в Error.message. timeoutMs чуть больше серверного лимита
    // хода (15 мин) — см. CONTINUE_TIMEOUT_MS: обрыв по таймауту не уводит в офлайн
    continue: (id: string, note?: string) =>
      request<{ newSessionId: string; handoffNoteId: string | null }>(
        `/chats/${id}/continue`,
        { method: 'POST', body: JSON.stringify(note ? { note } : {}), timeoutMs: CONTINUE_TIMEOUT_MS },
      ),
    // «Перенести в новый чат» (фича prompt-cache, шаг F-4): путь выбирает сервер — тёплый
    // (записку пишет сам чат, пока его кэш тёплый) или холодный (её собирает дешёвый раннер
    // по транскрипту). Ответ — id нового чата, его проект и черновик композера. 409 — чат
    // занят/повторный клик/не тот тип чата, 502 — модель не дала записку; текст ошибки
    // человеческий и показывается как есть. Тёплый путь — видимый ход чата до 5 мин,
    // таймаут фронта чуть больше — см. HANDOFF_TIMEOUT_MS.
    handoff: (id: string) =>
      request<{ chatId: string; projectId: string | null; draft: string | null; warm: boolean }>(
        `/chats/${id}/handoff`,
        { method: 'POST', timeoutMs: HANDOFF_TIMEOUT_MS },
      ),
    // Режим прав: сохраняем сразу при выборе в Composer, иначе он доехал бы до сессии
    // только вместе со следующим сообщением и терялся при уходе со страницы
    setMode: (id: string, mode: string) =>
      request<Session>(`/chats/${id}/mode`, {
        method: 'PUT',
        body: JSON.stringify({ mode }),
      }),
    // «Продолжить в стандартном окне 200K» под карточкой отказа Window1MUnavailable.
    // Единственный путь, которым суффикс [1m] снимается с чата: тихая деградация длинного
    // разговора в 200K — мина, решение принимает человек, сервер только исполняет.
    // Возвращает обновлённый чат: фронт по нему перерисовывает выбранную модель,
    // отдельного сигнала не приходит
    dropWindow1M: (id: string) =>
      request<Session>(`/chats/${id}/window-1m/drop`, { method: 'POST' }),
    delete: (id: string) => request<void>(`/chats/${id}`, { method: 'DELETE' })
      .then(r => { forgetChatContextSession(id); return r; }),
    getHistory: (id: string) => request<unknown[]>(`/chats/${id}/history`, { chatScope: id }),
    // То же, что api.sessions.getHistoryTail, для чата вне проекта
    getHistoryTail: (id: string, q: HistoryCursorQuery, onBytes?: (n: number) => void) =>
      request<HistoryReply>(`/chats/${id}/history?${historyCursorParams(q)}`,
        { live: q.after >= 0, chatScope: q.after < 0 ? id : undefined, onBytes }),
    // Вложение локального проекта ложится на машину проекта через агента: сервер на свой
    // маршрут для него отвечает отказом G1
    uploadFile: async (id: string, file: File, projectId?: string): Promise<{ path: string }> => {
      if (projectId && projectRouteOf(projectId) === 'agent') return uploadAgentAttachment(projectId, file);
      const token = readStoredToken();
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

  // Сторожа чатов, workflow-агенты и голоса TTS — в features/chat/api.ts
  ...chatAuxApi,

  // Документация проекта (README.md + docs/**) — в features/docs/api.ts
  ...docsApi,

  // «История решений» — в features/dossiers/api.ts; «Телеметрия» — в features/telemetry/api.ts
  ...dossiersApi,
  ...telemetryApi,

  files: {
    // URL файла проекта для браузерного <img>/<video>: токен через ?access_token=,
    // потому что тег не шлёт заголовки. Нужен картинкам в markdown — README ссылается
    // на них относительным путём, а base64 из files/content для <img src> не подходит
    // sessionId (F1a, «Резолвинг ссылок по worktree»): забрать файл из дерева чата,
    // а не из анкера проекта; параметр необязательный — старые вызовы не меняются.
    fileUrl: (projectId: string, path: string, sessionId?: string): string => {
      // Локальный проект: поток отдаёт агент по узкому билету на путь, а его выдача
      // асинхронна — синхронного URL нет (точка — agentStreamUrl в deviceAgent.ts).
      // Основной билет проекта в URL не кладём никогда
      const route = projectRouteOf(projectId);
      if (route === 'agent') return '';
      const token = readStoredToken();
      const params = new URLSearchParams({ path });
      if (sessionId) params.set('sessionId', sessionId);
      if (token) params.set('access_token', token);
      // С другого устройства файл отдаёт ретранслятор — целиком, без Range, под тем же JWT
      const base = route === 'relay' ? 'relay/files/stream' : 'files/stream';
      return `/api/projects/${encodeURIComponent(projectId)}/${base}?${params}`;
    },
    list: (projectId: string, path = '') =>
      projectRequest<FileEntry[]>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`),
    tree: (projectId: string, path = '', showHidden?: boolean) =>
      projectRequest<FileEntry[]>(`/projects/${projectId}/files/tree?path=${encodeURIComponent(path)}${showHidden ? '&showHidden=true' : ''}`),
    search: (projectId: string, q: string) =>
      projectRequest<FileEntry[]>(`/projects/${projectId}/files/search?q=${encodeURIComponent(q)}`),
    // Где путь реально лежит в деревьях проекта (анкер + worktree). paths ≤ 50,
    // иначе сервер 400; в ответе только найденные пути. Формат — DTO endpoints
    // files/locate (не TreeHit: TreeHit — внутренний тип локатора, без path и
    // alternatives). alternatives — число других совпадений (совпадений − 1).
    locate: (projectId: string, paths: string[], sessionId?: string) =>
      projectRequest<{ path: string; branch?: string; isAnchor: boolean; alternatives?: number }[]>(
        `/projects/${projectId}/files/locate`,
        { method: 'POST', body: JSON.stringify({ paths, ...(sessionId ? { sessionId } : {}) }) },
      ),
    // featureId (W4-5B2): читать из проверенного дерева эпика; без него — корень проекта.
    // sessionId (F1a): резолвить путь в дереве чата; ответ несёт branch (в каком дереве
    // нашли) и isAnchor (true — файл найден в заявленном дереве-анкере, false — в другом).
    getContent: (projectId: string, path: string, featureId?: string, opts?: { sessionId?: string }) =>
      projectRequest<{ content: string | null; isBinary: boolean; isImage: boolean; isDocument?: boolean; docKind?: string; mimeType?: string; base64?: string; fileSize?: number; branch?: string | null; isAnchor?: boolean }>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}${featureId ? `&featureId=${encodeURIComponent(featureId)}` : ''}${opts?.sessionId ? `&sessionId=${encodeURIComponent(opts.sessionId)}` : ''}`),
    saveContent: (projectId: string, path: string, content: string) =>
      projectRequest<void>(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`, {
        method: 'PUT',
        body: JSON.stringify({ content }),
      }),
    // Документы (pdf/docx/xlsx/pptx): конвертация в MD + ИИ-помощь (локальная модель / claude)
    documentConvert: (projectId: string, path: string) =>
      projectRequest<{ markdown: string }>(`/projects/${projectId}/files/document/convert?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentSummary: (projectId: string, path: string) =>
      projectRequest<{ summary: string }>(`/projects/${projectId}/files/document/summary?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentExtract: (projectId: string, path: string) =>
      projectRequest<{ decisions: string[]; dates: string[]; people: string[]; actionItems: string[] }>(`/projects/${projectId}/files/document/extract?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    documentTags: (projectId: string, path: string) =>
      projectRequest<{ tags: string[] }>(`/projects/${projectId}/files/document/tags?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    // Трансформировать любой файл в Markdown и сохранить (рядом или в targetDir).
    // enhance — восстановить разметку локальной моделью (заголовки/списки, для pdf).
    toMarkdown: (projectId: string, path: string, targetDir?: string | null, enhance = false) =>
      projectRequest<{ savedPath: string; markdown: string }>(`/projects/${projectId}/files/document/to-markdown`, {
        method: 'POST', body: JSON.stringify({ path, targetDir: targetDir ?? null, enhance }),
      }),
    getDiff: (projectId: string, path: string) =>
      projectRequest<{ diff: string | null }>(`/projects/${projectId}/files/diff?path=${encodeURIComponent(path)}`),
    // Панель «Изменения»: для присланных путей — какие ЕЩЁ чаты проекта их меняли.
    // Ключи ответа — ровно присланные строки path (см. FilesController.ChangedBy)
    changedBy: (projectId: string, paths: string[]) =>
      projectRequest<{ files: Record<string, ChangedBySession[]> }>(`/projects/${projectId}/files/changed-by`, {
        method: 'POST',
        body: JSON.stringify({ paths }),
      }),
    revert: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/revert`, { method: 'POST', body: JSON.stringify({ path }) }),
    createFile: (projectId: string, path: string, content?: string) =>
      projectRequest<void>(`/projects/${projectId}/files/create`, { method: 'POST', body: JSON.stringify({ path, content }) }),
    mkdir: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/mkdir`, { method: 'POST', body: JSON.stringify({ path }) }),
    rename: (projectId: string, oldPath: string, newPath: string) =>
      projectRequest<void>(`/projects/${projectId}/files/rename`, {
        method: 'POST',
        body: JSON.stringify({ oldPath, newPath }),
      }),
    delete: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`, { method: 'DELETE' }),
    saveFromUrl: (projectId: string, url: string, path: string) =>
      projectRequest<{ path: string }>(`/projects/${projectId}/files/save-from-url`, {
        method: 'POST',
        body: JSON.stringify({ url, path }),
      }),
    officeDiscard: (projectId: string, path: string) =>
      projectRequest<void>(`/projects/${projectId}/files/office-discard?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    getOfficeVersion: (projectId: string, path: string) =>
      projectRequest<{ ms: number }>(`/projects/${projectId}/files/office-version?path=${encodeURIComponent(path)}`),
    officeForceSave: (projectId: string, path: string) =>
      projectRequest<{ ok: boolean; reason?: string }>(`/projects/${projectId}/files/office-force-save?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    upload: async (projectId: string, file: File, targetPath = ''): Promise<void> => {
      assertServerRoute(projectId, 'POST', 'files/upload');
      const token = readStoredToken();
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

  // Git проекта (раздел «Файлы» → «Изменения»/«История»); ошибки операций — 409 { error }
  git: {
    // ?sessionId= (gq): активный worktree-чат переводит запросы в своё дерево — суффикс
    // добавляется ко всем операциям, достижимым из git-бара и панели «Изменения»
    status: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/status${gq(projectId)}`, { chatScope: gitChatScope(projectId) }),
    diff: (projectId: string, path: string, staged = false) =>
      projectRequest<{ diff: string | null }>(`/projects/${projectId}/git/diff?path=${encodeURIComponent(path)}&staged=${staged}${gq(projectId, '&')}`),
    log: (projectId: string, limit = 100, branch?: string) =>
      projectRequest<GitLogEntry[]>(`/projects/${projectId}/git/log?limit=${limit}${branch ? `&branch=${encodeURIComponent(branch)}` : ''}${gq(projectId, '&')}`, { chatScope: gitChatScope(projectId) }),
    // Незапушенные коммиты (впереди upstream) — стек скоупов панели «Изменения».
    // live: ответ не кэшируется вовсе: это самый тяжёлый по-чатовой класс (до 29 КБ на
    // чат, ключ с sessionId), а в офлайне он бесполезен — пушить без сети нельзя.
    unpushed: (projectId: string, limit = 100) =>
      projectRequest<GitLogEntry[]>(`/projects/${projectId}/git/unpushed?limit=${limit}${gq(projectId, '&')}`, { live: true }),
    // Настройка промпта AI-описания коммита: чтение (global/projectOverride/effective/default)
    getCommitPrompt: (projectId: string) =>
      projectRequest<GitCommitPromptInfo>(`/projects/${projectId}/git/commit-prompt`),
    // Сохранить оба промпта: global (per-user, всегда) + project (override при useProject)
    setCommitPrompt: (projectId: string, global: string, project: string, useProject: boolean) =>
      projectRequest<GitCommitPromptInfo>(`/projects/${projectId}/git/commit-prompt`, {
        method: 'PUT', body: JSON.stringify({ global, project, useProject }),
      }),
    // Определить стиль коммитов по истории репы → инструкция для поля (не сохраняет)
    detectCommitStyle: (projectId: string) =>
      projectRequest<{ prompt: string }>(`/projects/${projectId}/git/ai/detect-commit-style`, { method: 'POST', timeoutMs: 60_000 }),
    branches: (projectId: string) =>
      projectRequest<GitBranchInfo[]>(`/projects/${projectId}/git/branches${gq(projectId)}`),
    commitDetail: (projectId: string, sha: string) =>
      projectRequest<GitCommitDetail>(`/projects/${projectId}/git/commits/${sha}${gq(projectId)}`),
    commitFileDiff: (projectId: string, sha: string, path: string) =>
      projectRequest<{ diff: string | null }>(`/projects/${projectId}/git/commits/${sha}/diff?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    stage: (projectId: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stage${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    unstage: (projectId: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/unstage${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    stageAll: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stage-all${gq(projectId)}`, { method: 'POST' }),
    // Откат правок файла к HEAD — необратимо (подтверждение на фронте)
    discard: (projectId: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/discard${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ path }) }),
    discardAll: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/discard-all${gq(projectId)}`, { method: 'POST' }),
    commit: (projectId: string, message: string, amend = false) =>
      projectRequest<{ sha: string }>(`/projects/${projectId}/git/commit${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ message, amend }),
      }),
    checkout: (projectId: string, branch: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/checkout${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ branch }) }),
    createBranch: (projectId: string, name: string, from?: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/branches${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ name, from: from ?? null }),
      }),
    fetch: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/fetch${gq(projectId)}`, { method: 'POST', timeoutMs: 60_000 }),
    pull: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/pull${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    push: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/push${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    // «Подтянуть и опубликовать»: rebase на origin + push (ветка разошлась с origin).
    // Дольше push: внутри две сетевые операции подряд
    sync: (projectId: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/sync${gq(projectId)}`, { method: 'POST', timeoutMs: 180_000 }),
    // Частичный stage: patch — unified diff одного хунка/строк (сервер применяет с --recount)
    stageHunk: (projectId: string, patch: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stage-hunk${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ patch }) }),
    unstageHunk: (projectId: string, patch: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/unstage-hunk${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ patch }) }),
    stashList: (projectId: string) =>
      projectRequest<GitStashEntry[]>(`/projects/${projectId}/git/stash${gq(projectId)}`),
    // Файлы отложенного (для просмотра в верхней зоне панели «Изменения», как у коммита)
    stashShow: (projectId: string, index: number) =>
      projectRequest<{ files: GitFileChange[] }>(`/projects/${projectId}/git/stash/${index}${gq(projectId)}`),
    stashPush: (projectId: string, message?: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stash${gq(projectId)}`, { method: 'POST', body: JSON.stringify({ message: message ?? null }) }),
    stashPop: (projectId: string, index: number) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stash/${index}/pop${gq(projectId)}`, { method: 'POST' }),
    // Удаление стэша — необратимо (подтверждение на фронте)
    stashDrop: (projectId: string, index: number) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/stash/${index}${gq(projectId)}`, { method: 'DELETE' }),
    // Безопасная отмена коммита: новый обратный коммит; конфликт → 409 { error }
    revertCommit: (projectId: string, sha: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/commits/${sha}/revert${gq(projectId)}`, { method: 'POST', timeoutMs: 60_000 }),
    blame: (projectId: string, path: string) =>
      projectRequest<GitBlameLine[]>(`/projects/${projectId}/git/blame?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    // Данные входа в веб-UI Forgejo (пароль нужен: приватные репо анониму отдают 404)
    forgejoCredentials: (projectId: string) =>
      projectRequest<{ login: string; password: string | null }>(`/projects/${projectId}/git/forgejo-credentials`),
    resetForgejoPassword: (projectId: string) =>
      projectRequest<{ login: string; password: string }>(`/projects/${projectId}/git/forgejo-credentials/reset`, { method: 'POST', timeoutMs: 30_000 }),
    // История одного файла (--follow) — вкладка «История» просмотра файла
    fileLog: (projectId: string, path: string, limit = 100) =>
      projectRequest<GitLogEntry[]>(`/projects/${projectId}/git/file-log?path=${encodeURIComponent(path)}&limit=${limit}${gq(projectId, '&')}`),
    // Содержимое файла в конкретной версии («открыть, как было»); null — бинарь/нет файла
    fileAtCommit: (projectId: string, sha: string, path: string) =>
      projectRequest<{ content: string | null }>(`/projects/${projectId}/git/commits/${sha}/file?path=${encodeURIComponent(path)}${gq(projectId, '&')}`),
    // Документный режим: вернуть файл к версии из коммита (в авто-режиме сразу коммитится)
    restoreFile: (projectId: string, sha: string, path: string) =>
      projectRequest<GitStatus>(`/projects/${projectId}/git/commits/${sha}/restore-file${gq(projectId)}`, {
        method: 'POST', body: JSON.stringify({ path }), timeoutMs: 60_000,
      }),
    // Документный режим: «Сохранить сейчас» (✨-сообщение + push при авто-пуше)
    saveNow: (projectId: string) =>
      projectRequest<{ committed: boolean; sha?: string }>(`/projects/${projectId}/git/save-now${gq(projectId)}`, { method: 'POST', timeoutMs: 180_000 }),
    // LLM-помощь: описание коммита по staged-диффу / название стэша (генерация небыстрая — старт CLI)
    aiCommitMessage: (projectId: string) =>
      projectRequest<{ summary: string; description: string }>(`/projects/${projectId}/git/ai/commit-message${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    aiStashName: (projectId: string) =>
      projectRequest<{ name: string }>(`/projects/${projectId}/git/ai/stash-name${gq(projectId)}`, { method: 'POST', timeoutMs: 120_000 }),
    // git init + при настроенном Forgejo создание удалённого репозитория
    init: (projectId: string) =>
      projectRequest<{ status: GitStatus; htmlUrl: string | null }>(`/projects/${projectId}/git/init`, { method: 'POST', timeoutMs: 60_000 }),
    remote: (projectId: string) =>
      projectRequest<GitRemoteInfo>(`/projects/${projectId}/git/remote`),
    // Подключить/обновить origin введённым адресом
    setRemote: (projectId: string, url: string) =>
      projectRequest<GitRemoteInfo>(`/projects/${projectId}/git/remote`, {
        method: 'POST', body: JSON.stringify({ url }), timeoutMs: 30_000,
      }),
    // Завести репозиторий на встроенном Forgejo и подключить его как origin
    createServerRepo: (projectId: string) =>
      projectRequest<GitRemoteInfo>(`/projects/${projectId}/git/remote/server`, { method: 'POST', timeoutMs: 60_000 }),
    setAutoCommit: (projectId: string, enabled: boolean, push: boolean) =>
      projectRequest<{ autoCommit: boolean; autoPush: boolean }>(`/projects/${projectId}/git/auto-commit`, {
        method: 'PUT', body: JSON.stringify({ enabled, push }),
      }),
  },

  // «Знания»: проекльный индекс Dify и менеджер баз знаний — в features/knowledge/api.ts
  ...knowledgeApi,

  // «Навыки» и реестр skills.sh — в features/skills/api.ts
  ...skillsApi,

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


  // Карта плана — в features/plan/api.ts
  ...plansApi,

  // «Ридер ссылок» с хелперами — в features/reader/api.ts
  ...readerApi,
};
