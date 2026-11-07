// Типы домена «Расход, лимиты и квоты аккаунтов» (переехали из types/index.ts, S5-P4 и P11). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ===== Аналитика расхода токенов (Spend Analytics v2, /api/spend/*) =====

// Токены везде объектом — форма ответа бэкенда (docs/architecture/spend-analytics-api.md)
export interface SpendTokens {
  input: number;
  output: number;
  cacheRead: number;
  cacheCreation: number;
  total: number;
}

// Токены дня по ценовым классам моделей. Класс определяет бэк (S1): фронт
// красит по ключу и НЕ знает, какая модель какого класса.
export interface SpendTierCounts {
  free: number;
  cheap: number;
  expensive: number;
}

// Точка дневного ряда обзора/виджета; aggregated — день свёрнут (за детальным окном)
export interface SpendDayPoint {
  date: string;
  aggregated: boolean;
  total: number;
  bySource: Record<string, number>;
  // Раскладка дня по ценовым классам (бэк кладёт не везде — старые ответы без поля)
  byTier?: SpendTierCounts;
  falGenerations: number;
}

// Строка карточки-топа обзора и узел pivot-дерева (одна форма у бэкенда)
export interface SpendCardRow {
  key: string;               // id значения; "" — узел «без значения»
  name: string | null;       // null — сущность удалена
  meta: string | null;       // у чатов: "chat" | "task"
  tokens: SpendTokens;
  turns: number;
  falGenerations: number;
  // Ценовой класс модели строки (free|cheap|expensive). Заполнен для разрезов
  // «модель» и «провайдер», у прочих — null: класса у них нет
  tier?: string | null;
}

export interface SpendOverviewResponse {
  from: string;
  to: string;
  detailDays: number;
  windowStart: string;
  allUsers: boolean;
  totals: SpendTokens;
  turns: number;
  falGenerations: number;
  // Оценка объёма по тарифам API за период (сумма SpendRecord.CostUsd), $.
  // «Модели и расход» → плитка «По тарифам API»: бэк отдаёт не всегда —
  // без поля плитка просто не рисуется.
  costUsd?: number | null;
  // Расход на сервисы Яндекса за период (озвучка SpeechKit): рубли и число оплаченных
  // запросов. null — таких трат не было; с долларами и токенами НЕ складывается — другая валюта
  rub?: { total: number; requests: number } | null;
  byDay: SpendDayPoint[];
  cards: {
    // Разрез по пользователям бэк кладёт только в ответ scope=all — в scope=mine поля нет вовсе
    users?: SpendCardRow[];
    projects: SpendCardRow[];
    models: SpendCardRow[];
    chats: SpendCardRow[];
    personas: SpendCardRow[];
    sources: SpendCardRow[];
    providers: SpendCardRow[];
  };
  topTurns: SpendTurnDto[];
}

export interface SpendPivotNode extends SpendCardRow {
  hasDetail: boolean;        // false — узел целиком из дневных агрегатов (🔒 ходы недоступны)
}

export interface SpendPivotResponse {
  nodes: SpendPivotNode[];
}

// Замер размера постановки задачи по секциям (один запуск исполнителя).
// Только размеры в символах — содержимого постановки и заметок здесь нет by design.
export interface SpendTaskPromptRun {
  at: string;
  taskId: string;
  ownerId: string;
  projectId: string | null;
  sessionId: string | null;
  personaId: string | null;
  totalChars: number;
  totalTokensEst: number;
  task: number;
  expected: number;
  tools: number;
  rules: number;
  restrictions: number;
  delegation: number;
  omo: number;
  context: number;
  notes: number;
}

export interface SpendTaskPromptResponse {
  runs: SpendTaskPromptRun[];
}

export interface SpendTurnDto {
  id: string;
  timestamp: string;
  ownerId: string;
  userName: string | null;
  sessionId: string | null;
  chatName: string | null;
  projectId: string | null;
  projectName: string | null;
  taskId: string | null;
  taskTitle: string | null;
  personaId: string | null;
  personaName: string | null;
  provider: string | null;
  model: string | null;
  source: string;            // chat-turn | one-shot | fal | free
  label: string | null;      // подпись one-shot действия или endpoint fal
  tier?: string | null;      // ценовой класс модели хода (free|cheap|expensive)
  tokens: SpendTokens;
  generations: number;
  durationMs: number | null;
  own: boolean;              // ход текущего пользователя → можно «Открыть чат»
}

export interface SpendTurnsResponse {
  total: number;
  windowClamped: boolean;    // в периоде были свёрнутые дни — их ходы недоступны
  items: SpendTurnDto[];
}

export interface SpendTurnDetailResponse {
  turn: SpendTurnDto;
  neighbors: { id: string; timestamp: string; total: number }[];
}

export interface SpendWidgetResponse {
  today: SpendTokens;
  week: SpendTokens;
  todayTurns: number;
  weekTurns: number;
  weekFalGenerations: number;
  byDay: SpendDayPoint[];
}

export interface SpendBadgeResponse {
  sessionId: string;
  total: SpendTokens;
  turns: number;
  lastTurn: SpendTurnDto | null;
}

// ===== Лимиты, квоты, баланс, подписки, снимки использования (P11) =====

// Вариант продолжения чата при исчерпании лимита (из события provider_limit):
// kind='subscription' — другой аккаунт того же пула подписок Claude (та же модель,
// tierLabel — тариф «Max 5×», utilization 0..1); kind='provider' (дефолт) — сторонний
// провайдер (tierLabel/utilization не заполняются)
export interface ProviderFallbackOption {
  key: string;
  displayName: string;
  model: string;
  kind?: 'subscription' | 'provider';
  tierLabel?: string | null;
  utilization?: number | null;
}

// Одно окно квоты CLI-провайдера подписки (GET /api/providers/{key}/balance|usage):
// unit 'percent' — value остаток в процентах ("97.3"); 'count' — "120/300", занято сейчас
// из лимита (моментальный снимок); 'consumed' — "101/4000", израсходовано из лимита (растёт
// монотонно, сбрасывается по resetsAt); 'alive' — "6/6", живых платформ из всех (FreeLLM),
// чем больше, тем лучше — не расход и не исчерпание. count/consumed/alive — уже
// отформатированная строка «N/M»
export type ProviderQuotaWindowUnit = 'percent' | 'count' | 'consumed' | 'alive';
export interface ProviderQuotaWindow {
  label: string;
  value: string;
  resetsAt: string | null;
  unit: ProviderQuotaWindowUnit;
}

// Баланс CLI-провайдера. windows — ВСЕ окна квоты (включая основное), только у квотных
// провайдеров (GLM/Kimi/MiniMax); у денежных (deepseek/moonshot/openrouter) отсутствует/пусто —
// totalBalance/resetsAt остаются единственным источником (обратная совместимость + график)
export interface ProviderBalanceInfo {
  available: boolean;
  currency: string;
  totalBalance: string;
  asOf?: string;
  resetsAt?: string | null;
  windows?: ProviderQuotaWindow[] | null;
  // Доп. сведения в раскрытую карточку (напр. «В том числе подарочных: $5», расход по периодам,
  // уровень подписки, сводка за 24ч у FreeLLM) — null/отсутствует: показать нечего
  note?: string | null;
  // === Типизированные поля «Модели и расход» (models-spend-data-spec) ===
  // Все опциональны: «не разобралось — поля нет», вёрстка обязана жить без них.
  // Пишет ли бэк историю баланса (для sparkline в раскрытии)
  trackHistory?: boolean;
  // Подарочный (бонусный) остаток в валюте баланса — показывается «из них $N подарочных»
  grantedBalance?: number | null;
  // Тариф подписки провайдера без приставки «Подписка:» (напр. "Standard") — пилюля в шапке
  planLabel?: string | null;
  // Предел расхода ключа (OpenRouter): шкала «осталось из $N лимита ключа», только в «Деньгах»
  keyLimit?: { remaining: number; total: number } | null;
  // Расход по данным самого провайдера (НЕ наш SpendStore) — подпись «по данным OpenRouter»
  spend?: { daily: number; weekly: number; monthly: number } | null;
  // Здоровье агрегатора (FreeLLM): запросы за 24ч, успешность, живые платформы
  health?: {
    requests24h?: number | null;
    successRate?: number | null;
    platformsAlive?: number | null;
    platformsTotal?: number | null;
  } | null;
}

// Снимок использования окна во времени (история с бэка, data/usage.json) — для экрана usage и тренда
export interface UsageSnapshot {
  timestamp: string;
  limitType: string;
  // Чей снимок: ключ подписки пула ("claude", …) или CLI-провайдера ("glm", "deepseek")
  subscriptionKey?: string;
  utilization?: number;
  status?: string;
  isUsingOverage?: boolean;
  resetsAt?: string;
  overageStatus?: string;
  overageResetsAt?: string;
  // Кто записал снимок: живой ход чата, идл-пинг простаивающего аккаунта или OAuth-опрос
  // лимитов; null/отсутствует — записи до фичи идл-пинга (обратная совместимость)
  source?: 'turn' | 'probe' | 'oauth' | null;
}

// Тариф подписки (с бэка, из credentials)
export interface PlanInfo {
  subscriptionType?: string;
  rateLimitTier?: string;
  label: string;
}

// Ответ /api/usage: история снимков + тариф
export interface UsageResponse {
  snapshots: UsageSnapshot[];
  plan?: PlanInfo;
  subscriptions?: Record<string, SubscriptionUsage>;
  // Порог утилизации 5h-окна, выше которого аккаунт выведен из ротации новых чатов
  rotationThreshold?: number;
  // Порог утилизации недельного окна (дефолт бэка 0.95) — вторая причина вывода из ротации:
  // без него бейдж объяснял бы недельную перегрузку фразой «5ч 35% ≥ порога 80%»
  weeklyThreshold?: number;
  // Ключ аккаунта, куда фактически ушёл бы новый чат сейчас (детерминированный выбор):
  // при отсутствии свободных пул спиллит на перегруженный — бейдж показывает это честно
  routingTarget?: string;
  // Снимки окон лимитов сторонних CLI-провайдеров (glm/deepseek) — их Anthropic-совместимые
  // эндпоинты тоже шлют rate_limit_event, снимки пишутся под ключ провайдера
  providers?: Record<string, UsageSnapshot[]>;
  // Статус опроса api/oauth/usage по ключам аккаунтов: "ok" | "unauthorized" (токен
  // не подходит — setup-токен вместо полноценного входа) | "rate_limited" (429: опрос
  // упёрся в лимит частоты, ждёт backoff) | "refresh_rate_limited" (429 на продлении
  // access-токена профиля: вход живой, повтор сам) | "error"
  pollStatuses?: Record<string, string>;
  // Локальная модель (Ollama или llama-server): какая модель и на какие фоновые действия
  // она заведена. Имя поля контрактное и не переименовывается вместе с движком.
  ollama?: OllamaUsageInfo;
}

// Блок «Локальная модель» на экране использования. У локального движка нет лимитов/баланса
// (бесплатно), показываем модель и маршрут каждого фонового действия (локаль/claude).
export interface OllamaUsageInfo {
  enabled: boolean;
  model?: string | null;
  baseUrl?: string | null;
  // Какой движок поднят на бэкенде: 'ollama' | 'llama-server' (конфиг LocalLlm:Provider).
  // Поля может не быть — старый бэкенд его не шлёт, подпись тогда прежняя, «Ollama».
  provider?: string;
  actions: OllamaActionInfo[];
}

// Раскрытие пресета в ответе места каталога: заполнено, когда назначение места —
// общий пресет (route при этом развёрнут в первый шаг — для «Сейчас пойдёт»);
// name null — битая ссылка (пресет удалён)
export interface PlacePresetRef {
  id: string;
  name: string | null;
  steps: string[];
}

export interface OllamaActionInfo {
  key: string;
  title: string;
  group: string;
  // Начинается ли действие с локальной модели прямо сейчас (с учётом доступности Ollama)
  routedToOllama: boolean;
  // Откуда взято действующее значение: дефолт каталога, конфиг Ollama:Actions или выбор админа
  source?: 'default' | 'config' | 'admin';
  // Исполнитель первого шага: 'local' | 'tier:strong|medium|weak' (слот) | id модели
  // провайдера; легаси 'claude'/'default' ≙ tier:medium. Если назначение — пресет,
  // здесь развёрнутый первый шаг цепочки, а сам пресет — в поле preset
  route?: string;
  // Пресет назначения (разворачивается в route на бэке); null — назначение не пресет
  preset?: PlacePresetRef | null;
  // Подпись именованного локального движка (например «Gemma 4»), когда route='local' ведёт на него,
  // а не на основную локальную модель; null/нет — основной движок
  engine?: string | null;
  // Действию нужна сильная модель (лицо продукта, генерация артефактов) — локаль ему не
  // годится; пресеты подбирают Claude/облачную модель. В UI помечается бейджем.
  requiresStrong?: boolean;
  // Агентное место (группа «Чаты и персоны»): запускает сессию claude CLI, поэтому
  // локальная модель и direct:-модели ему недоступны
  agentic?: boolean;
  // Что место получает на вход (ADR-027 §2): 'text' — только текст, 'images' — текст плюс
  // картинки (разметка фото персонажа). Для мест с картинками локаль, direct:-модели,
  // пресеты и модели без зрения недоступны. Старый бэк поле не шлёт — дефолт 'text'.
  input?: 'text' | 'images';
  // Место умеет считать ракурс лица локально (InsightFace, маршрут face-pose) — только
  // место ракурса. Пикер на нём показывает пункт «Локально, InsightFace».
  facePose?: boolean;
}

export interface SubscriptionUsage {
  snapshots: UsageSnapshot[];
  name?: string;
  // Берёт ли пул этот аккаунт для новых чатов + эффективная утилизация 5h-окна (0..1)
  inRotation?: boolean;
  utilization?: number;
  // Жёсткое исчерпание (rejected/100%) — выведен независимо от числа utilization
  exhausted?: boolean;
  // Ярлык тарифа ("Max 20×", "Pro", …) — по нему пул приоритизирует аккаунты
  tier?: string;
  // Готовая PowerShell-команда входа в профиль аккаунта (для плашки «нужен claude login»);
  // null — команда неприменима (токен приходит из env, логин в файл не поможет)
  loginCommand?: string | null;
  // Способности аккаунта по тарифу: пул отсекает его раньше сравнения утилизаций
  // (SupportsModel в ClaudeSubscriptionPool), карточка рисует пилюлю «Без Opus» / «Без 1M»
  // рядом с тарифом. false → не принимает соответствующие ходы (но всё ещё в ротации
  // для доступных моделей); null → поле не пришло со старого бэка снимков (обратная
  // совместимость data/usage.json), пилюлю не рисуем, дефолт true неинформативен.
  supportsOpus?: boolean | null;
  supports1M?: boolean | null;
  // Эффективная утилизация недельного окна (0..1) — вторая ось вывода из ротации наравне
  // с utilization: аккаунт с 5ч 35% и 7д 99% пул уже не берёт (ClaudeSubscriptionPool.IsOverloaded)
  weeklyUtilization?: number;
  // Живые пометки «модель недоступна на ЭТОЙ подписке» (пара подписка × модель). Истёкшие
  // бэкенд не отдаёт; пустой список = пометок нет. Без них подписка выглядит полностью
  // здоровой («В ротации», лимит не исчерпан), а ходы конкретной модели на неё не идут
  unavailableModels?: ModelUnavailableMark[];
}

// Пометка «модель недоступна на подписке». reason — «model_no_access» (нет доступа по
// тарифу) | «model_out_of_credits» (кончились usage credits модели); until — момент, до
// которого пара не пробуется. model — нормализованный id (нижний регистр), в этом же виде
// его ждёт эндпоинт досрочного сброса
export interface ModelUnavailableMark {
  model: string;
  reason: string;
  until: string;
}

