// Методы REST «Персон» (олицетворённые ИИ-собеседники): CRUD персон владельца, их
// память, привязки, автоматизации и аватары. lib/api.ts подмешивает их в объект api
// spread'ом (адрес api.personas.* остаётся прежним для потребителей); типы персон —
// в features/personas/types.ts, доступны через ../../types.
import { cachedToken, readStoredToken, request } from '../../lib/offline';
import type {
  ApplyDefaultBindingsResult,
  AutomationRuleDto,
  BindingTarget,
  CreatePersonaDto,
  PantheonTemplate,
  Persona,
  PersonaAutomationRule,
  PersonaBinding,
  PersonaBindingDto,
  PersonaBindingType,
  PersonaContract,
  PersonaMemoryEntry,
  PersonaMemoryHit,
  PersonaMemoryType,
  PersonaScope,
  PersonaVoice,
  PersonaWorkingFocus,
  Session,
  TeamMemberDraft,
  UpdatePersonaDto,
  VoiceCandidatesResponse,
} from '../../types';

// Версия схемы миниатюр аватаров (?size=): bump-ит URL, чтобы браузеры, закэшировавшие
// оригинал (1,5 МБ) с immutable, сделали один свежий запрос и получили миниатюру.
// Сервер параметр может игнорировать; при смене схемы миниатюр поднять число.
const AVATAR_THUMB_SCHEME_VERSION = 2;

export const personasApi = {
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
};
