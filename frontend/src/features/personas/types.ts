// Типы домена «Персоны» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

import type { ModelTierValue } from '../../types';
import type { PersonaSpecialty } from '../specialties/types';

// ===== Персоны (олицетворённые ИИ-собеседники) =====

// Зона контекста персоны: глобально (личное пространство), в рамках проекта или сферы (флаг spheres)
export type PersonaScope = 'global' | 'project' | 'sphere';

// Профиль доступа персоны (P6): full — без ограничений; readOnly — смотрит и
// советует, но ничего не меняет; custom — свой список запрещённых инструментов
export type PersonaAccess = 'full' | 'readOnly' | 'custom';

// Пол персоны: female/male — явный выбор, unspecified — дефолт «авто по имени».
// camelCase-строки из бэкенда (JsonStringEnumConverter(CamelCase))
export type PersonaGender = 'female' | 'male' | 'unspecified';


// Параметры кропа загруженного аватара: масштаб + смещение центра окна
// от центра картинки (в пикселях исходника)
export interface AvatarCropStateDto {
  scale: number;
  offsetX: number;
  offsetY: number;
}

// Аватар персоны: инициалы на цветном фоне или загруженная картинка (этап 4).
// color — ключ палитры AGENT_COLORS; imageFile — имя файла в хранилище персоны;
// originalFile/crop — оригинал загруженного файла и параметры кропа (для «Перекроить»).
export interface PersonaAvatar {
  kind: 'initials' | 'image';
  color?: string;
  imageFile?: string;
  originalFile?: string | null;
  crop?: AvatarCropStateDto | null;
}

// Структурированный контракт персоны (P1): характер разложен по слотам,
// каждый слот попадает в свою секцию системного промпта. Отсутствие контракта —
// legacy-режим: весь характер живёт единым текстом в systemPrompt.
export interface PersonaContract {
  character?: string;         // характер и манера общения (свободный текст)
  tone?: string;              // тон одной фразой («тепло и на равных»)
  mustDo?: string[];          // правила «всегда …»
  mustNot?: string[];         // правила «никогда …»
  outputFormat?: string;      // требования к формату ответов
  speechExamples?: string[];  // примеры реплик (образцы стиля)
  instructions?: string;      // полный регламент роли (длинный markdown)
}

// Голос из каталога синтеза: канонический ключ, подпись для человека, пол и амплуа.
// model — модель синтеза (general / livetts), speedSupported — настраивается ли темп;
// старый сервер их не шлёт
export interface TtsVoiceInfo {
  voice: string;
  label: string;
  gender: 'female' | 'male';
  roles: string[];
  model?: string;
  speedSupported?: boolean;
}

// configured=false — синтеза на сервере нет: предлагать слушать нечего
export interface TtsVoicesResponse {
  configured: boolean;
  voices: TtsVoiceInfo[];
}

// Кандидат подбора голоса под персону: голос, амплуа (null — обычное) и почему подходит
export interface VoiceCandidate {
  voice: string;
  role: string | null;
  why: string;
}

// Ответ подбора: до трёх обычных голосов, отдельно живой (LiveTTS, дороже) и признак
// «непоказанных обычных голосов больше нет»
export interface VoiceCandidatesResponse {
  candidates: VoiceCandidate[];
  live: VoiceCandidate | null;
  exhausted: boolean;
}

// Как персона звучит в голосовом режиме; отсутствует — голосом инстанса из конфига
export interface PersonaVoice {
  voice?: string;        // имя голоса SpeechKit (белый список на сервере)
  role?: string;         // амплуа голоса («good», «strict», …); не у всех голосов есть
  speed?: number | null; // темп 0.1–3.0; null/отсутствие — из конфига
}

export interface Persona {
  id: string;
  ownerId: string;
  name: string;
  role?: string;              // роль персоны (главная подпись: «Роль (Имя)»)
  handle: string;             // машинное имя (@handle) — уникально в контексте (проект/глобально)
  handleCustom?: boolean;     // handle задан вручную (миграция/авто-переименования не трогают)
  description?: string;
  systemPrompt?: string;      // «характер» — legacy-текст (у персон без contract)
  contract?: PersonaContract | null; // структурированный контракт (P1)
  model?: string;
  // Уровень модели персоны (слот); отсутствует — не задан. Слабее явной model и слабее
  // уровня самой задачи, когда персона выступает исполнителем.
  // Семантика (ADR-007 §2): уровень разворачивается по самой узкой заполненной матрице —
  // своя (tierStrong/… ниже) → специальности → слоты владельца.
  modelTier?: ModelTierValue;
  // Свои модели по уровням: id модели ИЛИ "preset:{id}"; пусто — наследуется
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
  effort?: string;
  scope: PersonaScope;
  projectId?: string;         // задан только у проектной зоны
  sphereId?: string;          // задан только у зоны сферы (флаг spheres)
  avatar: PersonaAvatar;
  voice?: PersonaVoice | null; // личный голос; null/отсутствие — голос инстанса
  greeting?: string;          // приветствие персоны в начале чата
  memoryEnabled: boolean;     // долгая память (этап 2)
  // Облегчённый контекст: краткая карта вместо полного CLAUDE.md, урезанные инструменты и MCP.
  // null/отсутствие — решение не принято (читается как «выкл»)
  lightContext?: boolean | null;
  // Пол: female/male — явный выбор, unspecified — дефолт «авто по имени»
  gender?: PersonaGender;
  // Специальность (функциональная роль) для оркестрации; отсутствие/none — не задана
  specialty?: PersonaSpecialty;
  // Возможности персоны (ключи tasks/notes/web); null/отсутствие — без ограничений
  tools?: string[] | null;
  // Профиль доступа (P6); отсутствие — full
  access?: PersonaAccess;
  // Свой список запрещённых инструментов (только при access === 'custom')
  disallowedTools?: string[] | null;
  // Права (access/tools/disallowedTools) как группа, наследуемая от специальности: true — при
  // чтении сервер подставляет эффективный шаблон специальности (PersonaManager.Hydrate), поля
  // не свои; false/отсутствие (старый бэк) — поля персоны есть её override. Та же читка, что на бэке.
  inheritsAccess?: boolean;
  // Ключ шаблона пантеона OmO, из которого подключена персона (null — создана вручную)
  templateKey?: string | null;
  // Привязки к источникам знаний и правилам (фича persona-bindings); null — нет
  bindings?: PersonaBinding[] | null;
  // Доступ ко всем проектам владельца — текущим и будущим (только scope === 'global').
  // Привязки типа project/projectPath остаются подсказкой «когда каким пользоваться»
  // и зону не сужают, пока этот флаг включён.
  allProjectsAccess?: boolean;
  // Правила автоматизации (событийно-управляемая проактивность); null — нет
  automationRules?: PersonaAutomationRule[] | null;
  createdAt: string;
  updatedAt: string;
}

// Шаблон роли пантеона OmO (GET /api/personas/pantheon): каталог живёт на бэкенде,
// connectedPersonaId — id уже подключённой персоны владельца (null — не подключена)
export interface PantheonTemplate {
  key: string;
  role: string;
  name: string;
  description: string;
  contract: PersonaContract;
  greeting: string;
  color: string;
  tools?: string[] | null;
  access?: PersonaAccess;
  model?: string;
  effort?: string;
  // Специальность роли (функциональный тег) — ставится в specialty подключённой персоны
  specialty?: PersonaSpecialty;
  connectedPersonaId?: string | null;
}

// === Привязки персоны: «знания и правила» (фича persona-bindings) ===
// Тип источника: project — проект целиком; projectPath — папка/файл проекта;
// knowledge — база знаний (Dify-датасет); notes — источник заметок; tool —
// инструмент workspace; skill — глобальный навык.
// ─── Проактивность/автоматизации персон: правила «событие → действие» ───
export type AutomationTriggerType = 'timer' | 'file' | 'note' | 'gitCommit' | 'taskStatus' | 'mention';
export type AutomationActionWeight = 'gate' | 'work';

// Триггер: args — гибкий JSON-объект, ключи зависят от type
// (см. AutomationTrigger на бэке: timer/file/note/gitCommit/taskStatus/mention).
export interface AutomationTrigger {
  type: AutomationTriggerType;
  args?: Record<string, unknown> | null;
}
export interface AutomationCondition {
  onlyIf?: string | null;       // текст-предикат для LLM-гейта
  quietFrom?: string | null;    // "23:00"
  quietTo?: string | null;      // "07:00" (переход через полночь допустим)
  minIntervalMinutes?: number | null;
}
export interface AutomationAction {
  weight: AutomationActionWeight; // gate — one-shot гейт+сообщение; work — полный агентский ход
  instruction: string;
  rememberInHistory: boolean;
  // Время жизни чата правила (мин); null — бессрочно. Применяется один раз при создании.
  expiresAfterMinutes?: number | null;
}
export interface PersonaAutomationRule {
  id: string;
  enabled: boolean;
  name: string;
  trigger: AutomationTrigger;
  condition?: AutomationCondition | null;
  action: AutomationAction;
  createdAt: string;
  updatedAt: string;
}
// DTO для POST/PUT /api/personas/{id}/automation (partial-merge на бэке: null-поля наследуются).
export interface AutomationRuleDto {
  enabled?: boolean;
  name?: string;
  triggerType?: AutomationTriggerType;
  triggerArgs?: Record<string, unknown> | null;
  conditionOnlyIf?: string | null;
  quietFrom?: string | null;
  quietTo?: string | null;
  minIntervalMinutes?: number | null;
  actionWeight?: AutomationActionWeight;
  actionInstruction?: string;
  rememberInHistory?: boolean;
  // Не указано (undefined, поле опущено в теле запроса) — сохранить текущее/дефолт 1440
  // при создании; null — бессрочно; N>0 — TTL в минутах.
  actionExpiresAfterMinutes?: number | null;
}

// projectPersonas/projectTasks — кросс-проектные привязки: доступ к персонам/задачам
// ДРУГОГО проекта того же владельца (target — projectId; path — сужение: personaId для
// projectPersonas, "readonly" для projectTasks; пусто — вся команда / полный доступ)
// plugin (ADR-031) — навык плагина: target — «plugin:{id}/{skill}» (id пакета {publisher}/{slug})
export type PersonaBindingType = 'project' | 'projectPath' | 'knowledge' | 'notes' | 'tool' | 'skill' | 'plugin' | 'projectPersonas' | 'projectTasks';

// Режим привязки: auto — персона обращается по условию; always — выжимка в каждый ход; off — выключена
export type PersonaBindingMode = 'auto' | 'always' | 'off';

export interface PersonaBinding {
  id: string;
  type: PersonaBindingType;
  // Цель по типам: project/projectPath → projectId; knowledge → datasetId;
  // notes → ключ источника; tool → ключ инструмента; skill — имя навыка;
  // plugin → «plugin:{id}/{skill}» (навык плагина, ADR-031)
  target: string;
  // Путь внутри цели: папка/файл проекта или папка источника заметок
  path?: string | null;
  // Условие «когда пользоваться» — попадает в системный промпт
  condition: string;
  mode: PersonaBindingMode;
  createdAt: string;
  updatedAt: string;
  // true — умение пришло от специальности (эффективный список бэка, A2): своей записи нет,
  // менять/удалять нельзя, выключается привязкой Mode=Off с тем же именем навыка
  inherited?: boolean;
}

// Тело создания/обновления привязки (POST/PUT bindings)
export interface PersonaBindingDto {
  type: PersonaBindingType;
  target: string;
  path?: string | null;
  condition?: string;
  mode?: PersonaBindingMode;
}

// Элемент каталога целей привязки (GET /api/personas/binding-targets)
export interface BindingTarget {
  id: string;
  label: string;
  hint?: string | null;
  // Происхождение цели; для типа tool с ключом «mcp:<…>» (сервер личного реестра) — 'mcp'
  meta?: string | null;
  // Дефолт инструмента у конкретной персоны (только type=tool с personaId):
  // включён ли без привязки и чем задан дефолт (настройки / пресет роли / системный)
  defaultEnabled?: boolean | null;
  defaultOrigin?: 'settings' | 'role' | null;
  // Последний известный статус сервера личного реестра (только type=tool, ключ «mcp:<…>»,
  // см. McpServerStatuses на бэке: connected/failed/needs-auth/unknown). Строка, а не литерал —
  // набор значений живёт на бэке. Нет данных (старый бэк / не mcp-ключ) — точка статуса
  // в пикере не показывается.
  status?: string | null;
}

// Тело создания персоны (POST /api/personas). Большинство полей опциональны.
export interface CreatePersonaDto {
  name: string;
  role?: string;
  description?: string;
  systemPrompt?: string;
  // Ручной @handle (latin-slug). Создание: пусто — авто из имени. Обновление: undefined —
  // не менять, "" — сбросить к авто-генерации. Занят/невалиден → 400
  handle?: string;
  // Контракт характера; при обновлении: undefined — не менять, пустые слоты — сбросить
  contract?: PersonaContract;
  // Только создание: голос персоны; undefined — сервер подберёт его по характеру сам.
  // При обновлении бэкенд поле игнорирует — голос правится PUT /personas/{id}/voice
  voice?: PersonaVoice;
  model?: string;
  // Уровень модели персоны: '' = сбросить, undefined — не менять/не задавать
  modelTier?: ModelTierValue | '';
  effort?: string;
  scope?: PersonaScope;
  projectId?: string;
  sphereId?: string;
  color?: string;             // ключ палитры AGENT_COLORS для аватара-инициалов
  greeting?: string;
  memoryEnabled?: boolean;
  lightContext?: boolean;
  // Пол: female/male — явный выбор, unspecified — дефолт «авто по имени»
  gender?: PersonaGender;
  // Возможности (tasks/notes/web); полный набор бэкенд нормализует в «без ограничений»
  tools?: string[];
  // Профиль доступа (P6): full | readOnly | custom
  access?: PersonaAccess;
  // Свой список запрещённых инструментов (для custom)
  disallowedTools?: string[];
  // Права как группа, наследуемая от специальности (true) или свой override (false);
  // та же семантика, что у Persona.inheritsAccess
  inheritsAccess?: boolean;
  // Специальность (функциональная роль) для оркестрации; отсутствие/none — не задана
  specialty?: PersonaSpecialty;
  // Доступ ко всем проектам владельца (текущим и будущим); только для scope === 'global'
  allProjectsAccess?: boolean;
  // true — сгенерировать фото-аватар сразу после создания (опт-ин для авто/LLM-путей,
  // где человек не выбирает аватар сам — напр. пакетное создание команды)
  autoAvatar?: boolean;
  // Описание внешности для фотопортрета (англ.); пусто — берётся из роли/описания персоны
  avatarPrompt?: string;
}

// Тело обновления персоны (PUT /api/personas/{id}) — все поля опциональны
export type UpdatePersonaDto = Partial<CreatePersonaDto>;

// === Долгая память персоны (этап 3) ===
// Тип записи памяти:
//  - semantic   — факты («Факты»): устойчивые сведения о мире/пользователе
//  - episodic   — эпизоды («Эпизоды»): что произошло в конкретном разговоре
//  - procedural — приёмы («Приёмы»): как персона привыкла что-то делать
export type PersonaMemoryType = 'semantic' | 'episodic' | 'procedural';

// Запись долгой памяти персоны
export interface PersonaMemoryEntry {
  id: string;
  personaId: string;
  type: PersonaMemoryType;
  text: string;
  tags?: string[];
  salience: number;           // значимость (для приоритизации/забывания)
  sourceSessionId?: string;   // чат, из которого запомнилось
  pending?: boolean;          // предложено autolearn, ждёт подтверждения (③-3.2)
  createdAt: string;
  lastAccessedAt: string;
}

// Рабочий фокус персоны — «что я сейчас делаю» (одна ячейка рабочей памяти,
// живёт отдельно от записей; в recall подмешивается первым блоком)
export interface PersonaWorkingFocus {
  what: string;
  status: string;
  nextStep?: string;
  sourceSessionId?: string;
  updatedAt: string;
}

// Результат семантического поиска по памяти
export interface PersonaMemoryHit {
  id: string;
  type: PersonaMemoryType;
  text: string;
  tags: string[];
  score: number;
  createdAt: string;
}

// Тип записи командной памяти: решение / договорённость(конвенция) / факт / термин
export type TeamMemoryType = 'fact' | 'decision' | 'convention' | 'glossary';

// Источник записи командной памяти: вручную / автоизвлечение из хода / из совещания
export type TeamMemorySource = 'manual' | 'autoTurn' | 'autoMeeting';

// Запись общей памяти команды проекта (③-3.4) — recall'ят все персоны команды
export interface TeamMemoryEntry {
  id: string;
  ownerId: string;
  projectId: string;
  text: string;
  type: TeamMemoryType;
  tags?: string[];
  salience: number;
  source: TeamMemorySource;
  sourceSessionId?: string;
  createdAt: string;
}

// Элемент манифеста recall (F3): что персона подтянула в ход. Kind ∈ memory|note|knowledge|team
// (team — память команды проекта, ③-3.4).
export interface RecallItem {
  kind: string;
  ref?: string | null;
  title: string;
  snippet?: string | null;
}

// Черновик персоны из AI-формирования команды (POST /api/personas/ai/team)
export interface TeamMemberDraft {
  name?: string;
  role?: string;
  description?: string;
  character?: string;
  tone?: string;
  specialty?: string;
  color?: string;
  greeting?: string;
  // Описание внешности для фото-аватара (англ.) — используется при автогенерации после создания
  avatarPrompt?: string;
}
