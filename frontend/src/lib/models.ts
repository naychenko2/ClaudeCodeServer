// Доступные модели для выбора в чате. Актуальный список приходит с бэка
// (/api/models — сервер спрашивает claude CLI и кэширует), до загрузки или при
// ошибке — статический fallback. Паттерн стора — как у featureFlags.ts.
// Пустое value → флаг --model не передаётся (дефолтная модель CLI).

import { useSyncExternalStore } from 'react';
import { api } from './api';

export interface ModelOption {
  value: string;
  label: string;
  description?: string;
  provider?: string;       // "claude" | ключ CLI-провайдера (deepseek/glm/…); отсутствует у fallback = claude
  contextWindow?: number;  // точное окно с бэка (модели CLI-провайдеров); иначе regex-фолбэк
  curated?: boolean;       // false — модель из опроса API провайдера (без карточки/описания)
}

// Возможности провайдера (блок providers из /api/models) — UI скрывает недоступное
export interface ProviderCapabilities {
  provider: string;
  displayName: string;
  supportsPlanMode: boolean;
  supportsCompact: boolean;
  supportsMcp: boolean;
  supportsEffort: boolean;
  supportsPermissionModes: boolean;
  supportsImages: boolean;
  supportsAgents: boolean;
  hasBalance?: boolean; // провайдер отдаёт баланс аккаунта (/api/providers/{key}/balance)
  // Настроен ли (ключ задан ≠ пустой). /api/models отдаёт ВСЕ провайдеры, включая
  // ненастроенные, — флаг нужен плитке «Подключённые модели» диалога поставщиков.
  configured?: boolean;
  // Предустановленная тройка моделей для слотов сильная/средняя/слабая (если провайдер
  // охвачен алиасами/картой моделей); null — нет готовой тройки, чипс не рисуем.
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
}

// У Claude доступно всё — это и дефолт до загрузки списка с бэка
const CLAUDE_CAPS: ProviderCapabilities = {
  provider: 'claude',
  displayName: 'Claude',
  supportsPlanMode: true,
  supportsCompact: true,
  supportsMcp: true,
  supportsEffort: true,
  supportsPermissionModes: true,
  supportsImages: true,
  supportsAgents: true,
};

// Алиасы вместо конкретных версий — не протухают при выходе новых моделей
export const FALLBACK_MODELS: ModelOption[] = [
  { value: '', label: 'По умолчанию' },
  { value: 'opus', label: 'Opus' },
  { value: 'sonnet', label: 'Sonnet' },
  { value: 'haiku', label: 'Haiku' },
];

// Подписи для id, сохранённых в старых сессиях (их нет в динамическом списке CLI)
const LEGACY_LABELS: Record<string, string> = {
  'claude-opus-4-8': 'Opus 4.8',
  'claude-sonnet-4-6': 'Sonnet 4.6',
  'claude-haiku-4-5-20251001': 'Haiku 4.5',
};

let _models: ModelOption[] = FALLBACK_MODELS;
let _providers: Record<string, ProviderCapabilities> = { claude: CLAUDE_CAPS };
// Резолвнутые модели агентных мест из /api/models (ключ каталога → модель или null).
// Пункт «По умолчанию» в пикере означает модель СВОЕГО места, поэтому значение нужно всем
// экранам. Бэкенд уже применил цепочку «назначение → слот тира», фронт слоты не знает.
let _assignments: Record<string, string | null> = {};
const _listeners = new Set<() => void>();

// Ключи агентных мест (LocalActionCatalog): по ним подписывается пункт «По умолчанию»
export const USAGE = {
  chatNew: 'chat-new',
  chatPersona: 'chat-persona',
  tasksExecutor: 'tasks-executor',
} as const;
export type UsageKey = typeof USAGE[keyof typeof USAGE];

function emit() {
  _listeners.forEach(fn => fn());
}

// Русские описания моделей Claude по стабильному id-алиасу: описания из CLI приходят
// на английском, а UI русский. Формулировки версионно-нейтральные (без номеров версий),
// чтобы не устаревать при обновлении моделей Claude; незнакомый алиас → описание из CLI.
const CLAUDE_DESC_RU: Record<string, string> = {
  'default': 'Универсальная · сложные повседневные задачи',
  'opus': 'Универсальная · сложные повседневные задачи',
  'claude-fable-5': 'Самая мощная · трудные и долгие задачи',
  'sonnet': 'Экономичная · рутинные задачи',
  'haiku': 'Самая быстрая · короткие ответы',
};

// Ключ описания — алиас без суффикса окна («opus[1m]» → «opus»)
function claudeDescKey(value: string): string {
  return value.replace(/\[1m\]$/i, '');
}

// Загрузить список с сервера (вызывается при старте после проверки auth).
// value 'default' у CLI означает «модель по умолчанию» — маппим в '' (не передавать --model).
export async function loadModels(): Promise<void> {
  try {
    const res = await api.models.list();
    const opts: ModelOption[] = res.models.map(m => ({
      value: m.value === 'default' ? '' : m.value,
      label: m.value === 'default' ? 'По умолчанию' : m.displayName,
      // Claude: русский перевод по алиасу, иначе описание из CLI как есть
      description: (m.provider ?? 'claude') === 'claude'
        ? (CLAUDE_DESC_RU[claudeDescKey(m.value)] ?? m.description ?? undefined)
        : (m.description ?? undefined),
      provider: m.provider ?? undefined,
      contextWindow: m.contextWindow ?? undefined,
      curated: m.isCurated ?? true,
    }));
    if (opts.length > 0) {
      _models = opts;
      if (res.providers) _providers = { claude: CLAUDE_CAPS, ...res.providers };
      if (res.assignments) _assignments = res.assignments;
      emit();
    }
  } catch {
    // сервер недоступен/ошибка — остаёмся на fallback
  }
}

// Модель, которая стоит за пунктом «По умолчанию» для места. Пусто — назначение ведёт
// на незаданный слот, решение остаётся за CLI.
export function assignedModel(usage: UsageKey = USAGE.chatNew): string {
  return _assignments[usage] ?? '';
}

// Подпись пункта «По умолчанию» с расшифровкой, какая модель за ним стоит для этого места.
// Назначение ведёт на незаданный слот → без уточнения (за модель отвечает CLI).
export function defaultModelLabel(usage: UsageKey = USAGE.chatNew): string {
  const m = assignedModel(usage);
  return m ? `По умолчанию (${modelLabel(m)})` : 'По умолчанию';
}

// Готовая строка-опция «По умолчанию» для пикеров — одна на весь продукт: подпись, описание
// и окно контекста собираются здесь, чтобы формулировка не разъезжалась по экранам.
export function defaultModelOption(usage: UsageKey = USAGE.chatNew): ModelOption {
  const m = assignedModel(usage);
  return {
    value: '',
    label: defaultModelLabel(usage),
    description: m
      ? 'Модель из настроек — назначена этому виду чатов'
      : 'Не задана — модель выбирает CLI',
    contextWindow: m ? contextWindowFor(m) : undefined,
  };
}

// Провайдер модели: из динамического списка, иначе по префиксу id
// (ключи известных провайдеров: deepseek-* → deepseek, glm-* → glm).
// Пустая модель = «по умолчанию» → провайдер берётся у назначения места: иначе смена модели
// чата на этот пункт выглядела бы как «остаёмся у claude» и упиралась в guard смены провайдера.
export function modelProvider(value?: string | null, usage: UsageKey = USAGE.chatNew): string {
  if (!value) {
    const assigned = assignedModel(usage);
    return assigned ? modelProvider(assigned) : 'claude';
  }
  const fromCatalog = _models.find(m => m.value === value)?.provider;
  if (fromCatalog) return fromCatalog;
  const v = value.toLowerCase();
  return Object.keys(_providers).find(key => key !== 'claude' && v.startsWith(key)) ?? 'claude';
}

// Возможности провайдера выбранной модели; неизвестный провайдер = как Claude
export function modelCaps(value?: string | null): ProviderCapabilities {
  return _providers[modelProvider(value)] ?? CLAUDE_CAPS;
}

// Каким запросом менять модель чата: обычным update или миграцией к другому провайдеру.
// У НАЧАТОГО чата смена провайдера обычным update упирается в guard (транскрипт живёт
// у эндпоинта провайдера), поэтому идёт миграцией. Логика вынесена из ChatPanel чистой
// функцией: связка «пункт „По умолчанию“ × сменившееся назначение места» уже давала баг
// с тостом «Не указана модель», и её надо держать под тестом.
export type ModelChangePlan =
  | { kind: 'migrate'; model: string }
  | { kind: 'update' };

export function planModelChange(
  model: string,
  chat: { model?: string | null; claudeSessionId?: string | null },
  usage: UsageKey = USAGE.chatNew,
): ModelChangePlan {
  // «По умолчанию» — пустой value каталога, а миграции нужна КОНКРЕТНАЯ модель целевого
  // провайдера (она перевозит транскрипт и продолжает через --resume). Разворачиваем
  // пустую в модель назначения места ДО решения.
  const effective = model || assignedModel(usage);
  const crossProvider = modelProvider(effective, usage) !== modelProvider(chat.model, usage);
  // Пустая модель в миграцию не уходит НИКОГДА: назначение места может быть не задано
  // (слот пуст — модель выбирает CLI), и тогда migrateProvider ответил бы
  // «Не указана модель» тостом в чат. Такой выбор — сброс, его место в update.
  if (crossProvider && chat.claudeSessionId && effective) return { kind: 'migrate', model: effective };
  return { kind: 'update' };
}

// Отображаемое имя ассистента по модели сессии — для строк в UI («… закончил», «Спросите …»).
// У Claude ассистент брендируется нейтральным «AI» (имя провайдера «Claude» остаётся для
// баланса/usage и групп моделей); у сторонних провайдеров — их собственное имя.
export function assistantName(value?: string | null): string {
  const caps = modelCaps(value);
  return caps.provider === 'claude' ? 'AI' : (caps.displayName || 'AI');
}

// Метки виртуальных «провайдеров» — групп, которых нет в реестре LlmProviders, но которые
// появляются в каталоге моделей отдельной группой (напр. прямой HTTP-адаптер OpenRouter).
const VIRTUAL_PROVIDER_LABELS: Record<string, string> = {
  'openrouter-direct': 'OpenRouter · прямой вызов',
};

// Подпись провайдера по ключу (для группировки в ModelPicker, вкладок и т.п.)
export function providerLabel(key: string): string {
  return VIRTUAL_PROVIDER_LABELS[key]
    ?? _providers[key]?.displayName
    ?? (key === 'claude' ? 'Claude' : key.charAt(0).toUpperCase() + key.slice(1));
}

// Возможности провайдера по ключу (для вкладок «Использования» и т.п.)
export function providerCapsByKey(key: string): ProviderCapabilities {
  return _providers[key] ?? CLAUDE_CAPS;
}

// Ключи CLI-провайдеров (без claude) — для generic-обвязки (вкладки «Использования»).
// ВСЕ из каталога, включая ненастроенные: у тех caps.configured === false, отсеивать их
// (или показывать подсказку про ключ) — дело потребителя
export function cliProviderKeys(): string[] {
  return Object.keys(_providers).filter(k => k !== 'claude');
}

// Все провайдеры из блока /api/models (включая ненастроенные, у тех configured=false) —
// для плитки «Подключённые модели». Claude всегда первая и configured=true.
export interface ProviderInfo { key: string; caps: ProviderCapabilities; }
export function getProviders(): ProviderInfo[] {
  return Object.entries(_providers).map(([key, caps]) => ({ key, caps }));
}

// Реактивные возможности: ре-рендер после догрузки списка моделей/провайдеров
export function useModelCaps(value?: string | null): ProviderCapabilities {
  useModels();
  return modelCaps(value);
}

// Реактивный список всех провайдеров (для плитки «Подключённые модели»): ре-рендер
// после догрузки каталога моделей. Claude всегда первая.
export function useProviders(): ProviderInfo[] {
  useModels();
  return getProviders();
}

export function getModels(): ModelOption[] {
  return _models;
}

export function subscribeModels(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Реактивный список моделей для компонентов
export function useModels(): ModelOption[] {
  return useSyncExternalStore(subscribeModels, getModels, getModels);
}

// Реактивная подпись модели: ре-рендер, когда динамический список догрузился
export function useModelLabel(value?: string | null): string {
  useModels();
  return modelLabel(value);
}

// Реактивная опция «По умолчанию»: ре-рендер и при догрузке каталога,
// и при смене назначений из диалога поставщиков (оба зовут emit)
export function useDefaultModelOption(usage: UsageKey = USAGE.chatNew): ModelOption {
  useModels();
  return defaultModelOption(usage);
}

// Короткая подпись модели для отображения (id → label).
// Неизвестный id показываем как есть — например, фактическую модель из session_started.
export function modelLabel(value?: string | null): string {
  if (!value) return 'По умолчанию';
  return _models.find(m => m.value === value)?.label
    ?? FALLBACK_MODELS.find(m => m.value === value)?.label
    ?? LEGACY_LABELS[value]
    ?? value;
}

// Размер контекстного окна модели (токены) для индикатора заполнения.
// Матч по подстроке: фактический id из session_started (claude-opus-5-...)
// не совпадает с алиасом из MODELS.
//   Opus 4.6+/Opus 5/Sonnet 4.6+/Sonnet 5/Fable 5 — 1M; Haiku 4.5 — 200k; старые модели — 200k.
// Порядок важен: конкретные (haiku) раньше общих. ВАЖНО: это спека модели;
// эффективное окно, от которого claude CLI считает авто-компакт, может быть
// меньше (200k) — если проценты разойдутся с реальным компактом, свериться.
export const DEFAULT_CONTEXT_WINDOW = 200_000;
const CONTEXT_1M = 1_000_000;

const CONTEXT_WINDOWS: Array<{ match: RegExp; window: number }> = [
  { match: /haiku/i, window: 200_000 },
  { match: /opus-(4-[678]|5)|opus-4\.[678]/i, window: CONTEXT_1M },
  { match: /sonnet-(4-6|5)|sonnet-4\.6/i, window: CONTEXT_1M },
  { match: /fable|mythos/i, window: CONTEXT_1M },
  { match: /deepseek/i, window: CONTEXT_1M }, // V4-модели — 1M (точное окно приходит с бэка)
  { match: /glm.*\[1m\]/i, window: CONTEXT_1M }, // glm-5.2[1m] — окно 1M
  // Общий фолбэк для opus/sonnet без узнаваемой версии — консервативно 200k
  { match: /opus|sonnet/i, window: 200_000 },
];

export function contextWindowFor(model?: string | null): number {
  if (!model) return DEFAULT_CONTEXT_WINDOW;
  // Точное значение из каталога (модели CLI-провайдеров несут окно с бэка) приоритетнее regex
  const exact = _models.find(m => m.value === model)?.contextWindow;
  if (exact) return exact;
  return CONTEXT_WINDOWS.find(m => m.match.test(model))?.window ?? DEFAULT_CONTEXT_WINDOW;
}
