// «Проекты» и «Ссылки внешнего доступа» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// тонкая обёртка над живым REST; типы проекта (Project и его спутники) — types/index.ts,
// граф кода, превью/запуск, значки и «Архитектура» — ./types.ts.
import { readStoredToken, request } from '../../lib/offline';
import { noteProject, noteProjects, projectRequest } from '../../lib/deviceAgent';
import type {
  BackgroundResult, BoardColumn, MapHygieneApplyResult, MapHygieneReport, PermissionRule, Project,
  ProjectSubject, ProjectSubjectEdit, ProjectTag, Session, SubjectRunState,
  SystemPromptPart, TeamMemoryEntry, TeamMemoryType,
} from '../../types';
import type {
  ArchitectureGenerateResult, ArchitectureModelDto, ArchitectureSaveResult, CodeGraph,
  ExternalLinkIssued, ExternalPreviewLink, GlyphCandidate, LaunchConfigEntry,
  ProjectMemberRole, ProjectMembersView, ProjectService,
} from './types';

// Потолок ожидания preview/start. DevServerService.StartAsync (у сервера и у агента один) ждёт
// порт по часам: не дольше DevServerLaunchPolicy.DotnetRunReadyTimeout (300 с, `dotnet run`
// со сборкой; прочим — 120 с), плюс одна последняя итерация (пауза 500 мс и проба до 400 мс
// на IPv4 и IPv6). Сверху запас на старт процесса и дорогу. Меняется серверный потолок —
// меняй и этот: обрыв раньше сервера покажет «нет связи» поверх живого старта.
const DEV_SERVER_PORT_WAIT_MS = 300_000;
const PREVIEW_START_TIMEOUT_MS = DEV_SERVER_PORT_WAIT_MS + 30_000;

// Ответ сервера с проектом обновляет память «сервер или агент» (deviceAgent.ts)
function noted(project: Project): Project {
  noteProject(project);
  return project;
}

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом.
export const projectsApi = {
// Ссылки внешнего доступа — СКВОЗНЫЕ по проектам владельца, поэтому не под /projects/{id}:
  // забытая открытой витрина в соседнем проекте иначе осталась бы невидимой.
  externalPreview: {
    list: () => request<{ enabled: boolean; links: ExternalPreviewLink[] }>('/preview/external-links'),
    revoke: (jti: string) =>
      request<{ revoked: number }>(`/preview/external-links/${encodeURIComponent(jti)}`, { method: 'DELETE' }),
    revokeAll: () => request<{ revoked: number }>('/preview/external-links', { method: 'DELETE' }),
  },

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
      request<Project>(`/projects/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(data) }).then(noted),
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
    delete: (id: string) => request<void>(`/projects/${encodeURIComponent(id)}`, { method: 'DELETE' }),

    // --- Участники общего проекта (shared-projects, план §5 S1-F2) ---
    //
    // Эндпоинты — ProjectMembersController: GET /members, POST /members, PUT /members/{userId},
    // DELETE /members/{userId}, DELETE /members/me. И чтение, и каждая мутация отвечают одним
    // телом — ProjectMembersView (участники, владелец, моя роль): после правки список берут
    // из ответа, отдельный запрос не нужен. У отказа — { error, code }: текст готовый для
    // человека, машинный код — из MEMBER_REFUSAL (lib/offline прикрепляет тело ошибки целиком
    // к err.body, читают его как в ImageGenNote.isImageGenQueued).
    // Права: приглашает/меняет роль/убирает только владелец (403 not_owner); участник
    // может только покинуть сам. Снаружи проекта — 404 от [ProjectScope] (код
    // project_scope), существование не раскрываем.
    members: {
      // Живой запрос: список участников из офлайн-кэша показал бы уже убранного человека
      list: (id: string) =>
        request<ProjectMembersView>(`/projects/${encodeURIComponent(id)}/members`, { live: true }),
      // Пригласить по ТОЧНОМУ username (регистр не важен, как при входе). Поиска по части
      // имени на бэке нет: нет пользователя → 404 user_not_found; уже участвует → 409
      // already_member; сам себе → 400 self; флаг выключен у кого-то из них → 409
      // flag_disabled; проект на устройстве → 409 local_project.
      add: (id: string, username: string, role: ProjectMemberRole) =>
        request<ProjectMembersView>(`/projects/${encodeURIComponent(id)}/members`, {
          method: 'POST', body: JSON.stringify({ username, role }),
        }),
      // Сменить роль участника (только владелец). Не участника → 404 not_member.
      changeRole: (id: string, userId: string, role: ProjectMemberRole) =>
        request<ProjectMembersView>(
          `/projects/${encodeURIComponent(id)}/members/${encodeURIComponent(userId)}`,
          { method: 'PUT', body: JSON.stringify({ role }) },
        ),
      // Убрать участника (только владелец). Его нет в списке → 404 not_member.
      remove: (id: string, userId: string) =>
        request<ProjectMembersView>(
          `/projects/${encodeURIComponent(id)}/members/${encodeURIComponent(userId)}`,
          { method: 'DELETE' },
        ),
      // Покинуть проект: та же операция удаления, но её делает сам участник — id берёт
      // сервер из токена, «me» в пути — маркер, не id. Владелец → 400 owner_cannot_leave.
      leave: (id: string) =>
        request<ProjectMembersView>(
          `/projects/${encodeURIComponent(id)}/members/me`, { method: 'DELETE' },
        ),
    },

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
    getEffectivePrompt: (id: string) => request<{ parts: SystemPromptPart[] }>(`/projects/${encodeURIComponent(id)}/effective-prompt`),
    // Кастомные колонки Kanban-доски проекта (пустой массив → дефолтные 3)
    updateBoardColumns: (id: string, columns: BoardColumn[]) =>
      request<Project>(`/projects/${encodeURIComponent(id)}/board-columns`, { method: 'PUT', body: JSON.stringify({ columns }) }),
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
      projectRequest<{ services: ProjectService[]; activeServiceId: string | null }>(`/projects/${encodeURIComponent(id)}/services`),
    previewStart: (id: string, svc: {
      serviceId: string; name: string; command: string; args: string[];
      cwd?: string; port?: number; autoPort?: boolean; env?: Record<string, string>;
    }) =>
      projectRequest<{ status: string; port?: number; error?: string; serviceId: string }>(`/projects/${encodeURIComponent(id)}/preview/start`, {
        method: 'POST', body: JSON.stringify(svc), timeoutMs: PREVIEW_START_TIMEOUT_MS,
      }),
    previewStop: (id: string, serviceId: string) =>
      projectRequest<{ status: string }>(`/projects/${encodeURIComponent(id)}/preview/stop`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    previewStatus: (id: string) =>
      projectRequest<{ running: { serviceId: string; name: string; port: number | null; status: string; error: string | null }[]; activeServiceId: string | null }>(`/projects/${encodeURIComponent(id)}/preview/status`),
    previewActive: (id: string, serviceId: string) =>
      projectRequest<{ activeServiceId: string }>(`/projects/${encodeURIComponent(id)}/preview/active`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    // Сервис поднят вне продукта (Rider, терминал) — показать его в превью.
    // Порт выбирает сервер по конфигурации сервиса, клиент его не передаёт.
    previewActiveExternal: (id: string, serviceId: string) =>
      projectRequest<{ activeServiceId: string; port: number }>(`/projects/${encodeURIComponent(id)}/preview/active-external`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    // Внешний доступ: ссылка на сервис по отдельному поддомену. Порт, как и выше, выбирает
    // сервер — принимать его от клиента нельзя, эндпоинт стал бы туннелем наружу.
    // Остановить процесс, поднятый вне продукта. 409 с needsConfirm — порт держит чужой
    // процесс, и гасить его можно только после явного согласия человека.
    previewStopExternal: (id: string, serviceId: string, confirm = false) =>
      projectRequest<{ status: string; pid: number }>(`/projects/${encodeURIComponent(id)}/preview/stop-external`, {
        method: 'POST', body: JSON.stringify({ serviceId, confirm }),
      }),
    previewExternalLink: (id: string, serviceId: string) =>
      projectRequest<ExternalLinkIssued>(`/projects/${encodeURIComponent(id)}/preview/external-link`, {
        method: 'POST', body: JSON.stringify({ serviceId }),
      }),
    getLaunchConfig: (id: string) =>
      projectRequest<{ configurations: LaunchConfigEntry[] }>(`/projects/${encodeURIComponent(id)}/launch-config`),
    putLaunchConfig: (id: string, configurations: LaunchConfigEntry[]) =>
      projectRequest<{ configurations: LaunchConfigEntry[] }>(`/projects/${encodeURIComponent(id)}/launch-config`, {
        method: 'PUT', body: JSON.stringify({ configurations }),
      }),
  },
};
