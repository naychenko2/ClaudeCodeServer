// Типы домена «Специальности и пресеты моделей» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

import type { ModelTierValue } from '../../types';
import type { Persona, PersonaAccess, PersonaBindingMode, PersonaBindingType } from '../personas/types';

// Специальность персоны — функциональная роль для оркестрации (НЕ отображаемое имя роли):
// конвейер (analyst→planner→reviewer→executor), голос брифинга (secretary),
// группировка/статус команды, роутинг памяти команды. none — не задана.
// backendExecutor/frontendExecutor/devopsExecutor — профильные исполнители; wire-значения
// совпадают с ключами бэкендного SpecialtyCatalog (camelCase от enum).
export type PersonaSpecialty =
  | 'none' | 'analyst' | 'planner' | 'reviewer' | 'executor' | 'secretary'
  | 'coordinator' | 'mentor' | 'designer' | 'consultant' | 'librarian' | 'tester'
  | 'backendExecutor' | 'frontendExecutor' | 'devopsExecutor';

// Шаблон прав и инструментов специальности: подставляется в поля персоны
// при выборе специальности, дальше правится вручную.
export interface SpecialtyTemplate {
  access: PersonaAccess;
  // null — все возможности (tasks+notes+web)
  tools: string[] | null;
  // Имеет смысл только при access === 'custom'
  disallowedTools: string[] | null;
}

// Запись каталога специальностей из GET /api/specialties: подписи, описание (для
// карточек панели «Инструкции для роли»), значок/цвет роли (из белого списка
// SpecialtyCatalog.Entry.Icon/Color на бэке) и эффективный шаблон прав
// вызывающего (настройки поверх дефолтов кода) приходят с бэкенда. icon/color
// опциональны: бэкенды младше волны их ещё не отдают, фронт отрисует lucide-глиф
// в цветном круге как последний фолбэк.
export interface SpecialtyCatalogEntry {
  key: PersonaSpecialty;
  label: string;
  description?: string;
  executorFamily: boolean;
  template: SpecialtyTemplate | null;
  icon?: string | null;
  color?: string | null;
}

// Именованный пресет — упорядоченная цепочка шагов (ADR-007 §1). Шаг — маршрут
// в лексике назначений мест: tier:strong|medium|weak, id модели, local, claude, default.
// Ссылаться на другой пресет шаг не может (вложенность запрещена бэкенд-валидацией).
export interface ModelRoutePreset {
  id: string;
  name: string;
  description?: string | null;
  steps: string[];
}

// Пресет в объединённом списке GET /api/specialties/settings: личные впереди,
// затем общие; scope — признак слоя (общий чужой читается, но не правится).
// 'user' — админский user-слой для конкретного пользователя (PresetScope.User)
export interface ScopedPreset extends ModelRoutePreset {
  scope: 'owner' | 'global' | 'user';
}

// Настройка специальности в слое (глобальном или личном): шаблон прав + матрица
// моделей по уровням (ADR-007 §2). Значение ячейки — id модели ИЛИ "preset:{id}";
// пустая ячейка — «спроси матрицу шире». defaultTier — уровень по умолчанию для
// персон специальности без своего. Запись в личном слое заменяет глобальную ЦЕЛИКОМ.
// PromptSections и DefaultBindings — посекочное наследование (отдельный резолв по
// каждой секции/записи): переопределение одной секции не затирает соседние. Соответствует
// SpecialtyTemplateSettings на бэке (Services/SpecialtySettingsStore.cs).
export interface SpecialtyTemplateSettings {
  access: PersonaAccess;
  tools?: string[] | null;
  disallowedTools?: string[] | null;
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
  defaultTier?: ModelTierValue | null;
  // Секции промптов специальности (плана «Секции промптов»): null/[] — слой секций
  // не задаёт, наследование вниз (owner → user → global → дефолт кода).
  promptSections?: SpecialtyPromptSection[] | null;
  // Типовой профиль умений роли (материализуется в личные привязки при создании
  // персоны, см. PersonaMaterialization). null/[] — слой профиль не задаёт.
  defaultBindings?: SpecialtyDefaultBinding[] | null;
}

// Секция промпта специальности в слое (id из каталога SpecialtyPromptPresets.Sections).
// enabled и text наследуются НЕЗАВИСИМО: переопределение enabled не сбрасывает текст и
// наоборот. На бэке соответствует SpecialtyPromptSectionSettings.
export interface SpecialtyPromptSection {
  id: string;
  enabled: boolean;
  // null/пусто — текст наследуется снизу (или дефолт кода, если нигде не задан)
  text?: string | null;
}

// Типовое умение роли: при создании персоны специальности материализуется в её
// личные привязки (модель «копия при создании», не динамическое наследование). Цель
// НЕ хранится: конкретную цель подбирает AI по каталогу владельца; исключение — тип
// «Навык» (Skill), там явное имя скилла (skillName), отсутствующие в каталоге скиллы
// пропускаются молча. На бэке соответствует SpecialtyDefaultBinding.
export interface SpecialtyDefaultBinding {
  type: PersonaBindingType;
  mode: PersonaBindingMode;
  // Условие «когда применять» — попадает в индекс системного промпта
  condition: string;
  // Имя скилла из каталога владельца — только при type === 'skill'
  skillName?: string | null;
}

// Каталог секций промптов: дефолты кода, единые для всех (фича specialty-prompt-sections).
// Источник — GET /api/specialties/prompt-sections. textLimit жёстко ограничивает длину
// текста секции (1024 символа на бэке, SpecialtyPromptPresets.SectionTextLimit).
export interface SpecialtyPromptSectionsCatalog {
  textLimit: number;
  sections: SpecialtyPromptSectionMeta[];
  specialties: Record<string, SpecialtyPromptSectionsForSpecialty>;
}

export interface SpecialtyPromptSectionMeta {
  id: string;
  label: string;
  description: string;
}

// Состав каталога по конкретной специальности: включённость и типовой текст каждой
// секции + профиль типовых умений роли (дефолты кода).
export interface SpecialtyPromptSectionsForSpecialty {
  sections: { id: string; enabled: boolean; text: string }[];
  defaultBindings: SpecialtyDefaultBinding[];
}

// Результат POST /api/personas/{id}/bindings/apply-defaults: сколько типовых умений
// роли ДОБАВЛЕНО в личные привязки персоны (0 — профиль пуст или ничего не подошло).
export interface ApplyDefaultBindingsResult {
  persona: Persona;
  applied: number;
}

// Слой настроек специальностей и пресетов: шаблоны + «любая специальность» + пресеты-цепочки
export interface SpecialtySettingsLayer {
  specialties: Record<string, SpecialtyTemplateSettings>;
  // «Любая специальность»: применяется, когда у конкретной специальности записи нет
  defaultSpecialty?: SpecialtyTemplateSettings | null;
  presets: ModelRoutePreset[];
}

// Ответ GET /api/specialties/settings: единственный глобальный слой (волна 4 убрала
// owner/user-слои — запись всегда идёт в global) и объединённый список пресетов
// с признаком слоя (для UI/диагностики).
export interface SpecialtySettingsResponse {
  version: number;
  // Эффективный бюджет подмен цепочки хода (per-owner → global → дефолт, кламп 1..5):
  // шаги пресета за пределом бюджета+1 приглушаются как «обычно не используется»
  maxSubstitutions?: number;
  global: SpecialtySettingsLayer;
  presets: ScopedPreset[];
}

// Ответ серверного сброса исключений (GET .../reset/{scope}/preview, POST .../reset/{scope}):
// specialties/personas — число ФАКТИЧЕСКИ изменённых записей (у preview — сколько изменится);
// shadowed — состояние слоя ПОСЛЕ операции (ключи специальностей, оставшихся ради своих прав,
// а не дельта вызова); personaNames — имена изменённых персон (только scope=owner)
export interface ResetResult {
  specialties: number;
  shadowed: string[];
  personas: number;
  personaNames: string[];
}

// Чип модели на карточке персоны-сабагента (спека «Чип модели…»): разрешённое
// состояние модели для пары (персона, сессия). Готовые label/hint считает бэкенд
// (ModelAssignmentResolver.SubagentChipFor) — фронт логику не пересобирает;
// kind — машинный код состояния (tier|chat-model|provider-main|provider-medium|provider-fast).
export interface SubagentModelChip {
  kind: string;
  label: string;
  hint: string;
}

// Ответ GET /api/models/preview — эффективный резолв для строки «Сейчас пойдёт»
// (спека, блок 4). model — первая развёрнутая модель хода (null — пустой резолв или
// битый пресет); source — где выбрано значение; tier/tierOrigin — эффективный уровень
// и кто его задал; preset — раскрытие ссылки preset:{id}; chain — план фолбэка.
export interface ModelPreviewResponse {
  model: string | null;
  source: 'persona-model' | 'persona-cell' | 'specialty-cell'
    | 'owner-slot' | 'instance-slot' | 'place-assignment' | 'explicit' | null;
  tier: ModelTierValue | null;
  tierOrigin: 'task' | 'persona' | 'specialty' | 'place' | null;
  preset: { id: string; name: string | null; steps: string[]; broken: boolean } | null;
  chain: string[];
  // Чип модели сабагента — присутствует только при запросе с sessionId + personaId
  subagentChip?: SubagentModelChip | null;
}

// Ответ GET /api/models/presets/{id}/usage — места, где выбран пресет
// (диалог удаления, спека блок 6). ownerId — null у общих мест (инстанс/место каталога)
export interface PresetUsageEntry {
  kind: 'instance-slot' | 'owner-slot' | 'specialty-cell' | 'persona-model' | 'persona-cell' | 'place';
  label: string;
  ownerId?: string | null;
}
export interface PresetUsageResponse {
  presetId: string;
  count: number;
  usages: PresetUsageEntry[];
}

