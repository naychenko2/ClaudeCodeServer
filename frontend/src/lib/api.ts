// ПОТОЛОК РОСТА (frontend-api-split-2026-11.md): файл только худеет. Лимит строк — в
// lib/__tests__/apiFileBudget.test.ts; он снижается после каждого выноса домена в
// features/<домен>/api.ts, поднимать его нельзя. Новые методы — сразу в features/<домен>/.
import type { Project, ProjectTag, ProjectSubject, ProjectSubjectEdit, SubjectRunState, Session, FileEntry, SyncMark, WorkflowAgentInfo, WorkflowAgentBlock, PermissionRule, SystemPromptPart, BoardColumn, NoteDetail, ExtractTasksResponse, TeamMemoryEntry, TeamMemoryType, ProjectService, LaunchConfigEntry, CodeGraph, PromptSnapshot, PromptSection, LocalHandsChatStatus, BackgroundResult, ChangedBySession, ExternalPreviewLink, ExternalLinkIssued, SessionContextEntry, MapHygieneReport, MapHygieneApplyResult } from '../types';
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
// eslint-disable-next-line module/no-cross-feature-import -- lib/api собирает объект api из api-модулей фич spread'ом; публичного index.ts у notes нет (баррел заведётся отдельно)
import { notesApi } from '../features/notes/api';
import { docsApi } from '../features/docs/api';
import { tasksApi } from '../features/tasks/api';
import { personasApi } from '../features/personas/api';
import { gitApi } from '../features/git/api';
import { readStoredToken, request, writeStoredToken } from './offline';
import { forgetChatContextSession } from './chatContext/forget';
import { assertServerRoute, noteProject, noteProjects, projectRequest, projectRouteOf, uploadAgentAttachment } from './deviceAgent';
import type { HistoryCursorQuery, HistoryReply } from './historyTail';

// Query-строка курсора докачки истории: after обязателен, fp — только для настоящего курсора
const historyCursorParams = (q: HistoryCursorQuery) =>
  `after=${q.after}${q.fp ? `&fp=${encodeURIComponent(q.fp)}` : ''}`;

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
// Контекст worktree-чата для git-запросов: состояние живёт в features/git/api.ts,
// здесь только переэкспорт — адреса потребителей (ChatPanel, lib/git.ts) не меняются
export { setGitSessionContext, getGitSessionContext } from '../features/git/api';

// Ответ сервера с проектом обновляет память «сервер или агент» (deviceAgent.ts)
function noted(project: Project): Project {
  noteProject(project);
  return project;
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

  // Мелкие домены «Дома» (сводка, бриф, поиск, онбординг, «стена», быстрые фразы,
  // продуктовая история) — в features/home/api.ts
  ...homeApi,

  // Группы проектов и сферы — в features/spheres/api.ts
  ...spheresApi,

  // Задачи и доска агентов (диспетчерская) — в features/tasks/api.ts
  ...tasksApi,

  // Заметки (Obsidian-совместимая база знаний) — в features/notes/api.ts
  ...notesApi,

  // Персоны (олицетворённые ИИ-собеседники) — в features/personas/api.ts
  ...personasApi,

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

  // Git проекта с контекстом worktree-чата — в features/git/api.ts
  ...gitApi,

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
