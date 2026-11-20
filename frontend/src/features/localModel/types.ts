// === «Локальные модели» (фича local-model-stats) ===
//
// DTO REST-ручек: LocalModelStatsController (снимок панели и список движков, только
// админ — настройка глобальная) и SessionLocalModelController (итог последнего хода
// чата, его владелец). Время — unix-мс; kv — доли 0..1 (не проценты и не ГиБ);
// phase — строка; чужие чаты анонимны (own=false, без sessionId/projectId).

// Снимок панели: GET /api/local-model/stats?window=1m|5m|15m|1h&since=<unix ms>&engine=main|<имя движка>.
// configured=false — локальный провайдер не настроен (панель и значок скрыты);
// status: working | busy | down | notConnected | yielded.
// 'yielded' — только у движка с StateUrl: метрики молчат, но арбитр сказал, что модель
// уступила карту по плану, а не упала (разрез v2, §1): DownSince при этом не ставится.
// Без engine — основной стенд, как до фичи: ни 'yielded', ни блока duty в ответе нет.
export interface LocalModelStats {
  configured: boolean;
  engine?: string;
  model?: string;
  status: 'working' | 'busy' | 'down' | 'notConnected' | 'yielded';
  // unix-мс
  downSince?: number | null;
  running?: number;
  waiting?: number;
  othersCount?: number;
  // genPerStream/prefillPerStream — сейчас, на поток (~60 с, как чувствует отдельный чат);
  // speculativeSpeedup — «ускорение угадыванием» (ADR-029, v2.3): Δgeneration_tokens/Δitl_count
  // за окно ~60 с, во сколько раз DFlash ускоряет генерацию; null — нет данных/рестарт или шагов не было
  speed?: { genPerStream?: number; prefillPerStream?: number; speculativeSpeedup?: number | null };
  // cleanStepMs — база шага (мс): длительность прохода модели без помех, ОДНА на весь
  // стенд и НЕ зависящая от окна графика. Нужна полосе «Куда уходит время агентов»:
  // «генерирует» = ΣitlCount × база, «ждёт соседа» = ΣitlSeconds − «генерирует».
  // Раньше база считалась на фронте по окну — величина прыгала при сдвиге окна.
  // null/отсутствует — базы нет: «ждёт соседа» не выделяется (доля «—», не ноль).
  cleanStepMs?: number | null;
  // «Скорость ответа» (фича llm-answer-speed, спека docs/features/llm-answer-speed-spec.md):
  // сквозное определение — Σ выходных токенов ÷ Σ полного времени запросов, завершённых
  // в окне; окна — строки '5m' и '1h' (их порядок в ответе не значим, фронт берёт по ключу).
  // endToEndTps — сквозная скорость, ток/с; medianTps — медиана по отдельным запросам,
  // в неё входят только ответы не короче minTokensForMedian (короткие сильно занижают её);
  // medianTtftSec / p90TtftSec — время до первого токена, секунды; requests — число
  // завершённых запросов в окне. Нет данных — null, не 0.
  // Нет поля или null — блок не рисуется (старый бэкенд, учёт ещё не набрал запросов):
  // карточка говорит «нет данных», а не нули.
  answerSpeed?: {
    windows: {
      window: string;
      endToEndTps?: number | null;
      medianTps?: number | null;
      medianTtftSec?: number | null;
      p90TtftSec?: number | null;
      requests?: number;
    }[];
    minTokensForMedian?: number;
  } | null;
  // kv: ВСЕ доли 0..1 (не проценты и не ГиБ): usagePerc — занято блоками KV-кэша
  // (это НЕ «память заполнена»); ramTierPerc — занятость тира в RAM;
  // prefixHitPerc — попадания в кэш; ramTierHitPerc — попадания в RAM-тир;
  // preemptions15m — вытеснения за 15 мин
  kv?: { usagePerc?: number; ramTierPerc?: number; prefixHitPerc?: number; ramTierHitPerc?: number; preemptions15m?: number };
  gpus?: { index: number; name: string; utilPerc?: number; memUsedMb?: number; memTotalMb?: number; tempC?: number; powerW?: number; powerLimitW?: number }[] | null;
  // Точка кольца истории (шаг 5 с, глубина 1 ч). genThroughput/waiting/running — как раньше;
  // серии кэша v2.1 (карточка «Нагрузка и кэш»): kvUsage — доля 0..1 (максимум по опросам с
  // прошлой точки); prefixHits/prefixQueries/ramHits/ramQueries/preemptions — приращения
  // счётчиков с прошлой точки (фронт сводит в интервалы и считает долю из сумм).
  // Все `number | null`: null — нет прошлой точки, метрика не отдана или счётчик упал
  // (рестарт); отрицательных значений нет. Старый бэкенд — поля отсутствуют (undefined).
  history?: {
    t: number;
    genThroughput?: number | null;
    waiting?: number | null;
    running?: number | null;
    kvUsage?: number | null;
    prefixHits?: number | null;
    prefixQueries?: number | null;
    ramHits?: number | null;
    ramQueries?: number | null;
    preemptions?: number | null;
    // скорость на поток (вторая линия графика, ADR-029): genTokens — приращение
    // vllm:generation_tokens_total, itlSeconds — приращение
    // vllm:inter_token_latency_seconds_sum (сек); фронт: ΣgenTokens/ΣitlSeconds =
    // скорость генерации на поток (ток/с, главная линия графика).
    // itlCount — приращение vllm:inter_token_latency_seconds_count (число шагов
    // декодирования); фронт: ΣitlSeconds/ΣitlCount = время шага (мс),
    // ΣgenTokens/ΣitlCount = токенов за шаг, ΣitlSeconds/длина интервала = агентов
    // в генерации (график «Шаг модели»).
    // null — нет прошлой точки/рестарт (см. api.md)
    genTokens?: number | null;
    itlSeconds?: number | null;
    itlCount?: number | null;
    // База шага (мс) для этой точки: по числу потоков в ней у батч-движка, иначе общая база стенда.
    cleanStepMs?: number | null;
    // Полоса «Куда уходит время агентов» (ADR-029, v2.3): prefillSeconds/queueSeconds —
    // приращения гистограмм (сек), promptTokens — прочитанный промпт (токены).
    // null — нет прошлой точки/рестарт (см. api.md)
    prefillSeconds?: number | null;
    queueSeconds?: number | null;
    promptTokens?: number | null;
  }[];
  // tensorParallel — необязательное: число GPU в tensor-parallel (Денис добавляет
  // следующим); нет поля — TP = число карт (gpus.length)
  tensorParallel?: number;
  // Подписи основного стенда из секции Stats (только показ): engineLabel — движок вместо
  // engine (engine — формат метрик, у Strata за адаптером это «vllm»), topology — раскладка
  // по картам вместо «TP=N». Нет поля — прежние подписи.
  engineLabel?: string;
  topology?: string;
  // chats: «кто сейчас на модели». own — чат текущего пользователя (клик открывает его);
  // чужие — строка «Чат другого пользователя» без названия и перехода. Бэк отдаёт
  // только projectId (и только для своих чатов) — имя проекта резолвит фронт из
  // стора проектов; для чужого чата подстроки нет вовсе.
  chats?: { own: boolean; sessionId?: string; projectId?: string; title?: string; phase: string; genTps?: number }[];
  // Сводка «Железо» (только админ): для строки-ссылки «Видеокарты» и точки на вкладке.
  hardware?: import('./hardwareTypes').HardwareBrief;
  unsupported?: string[];
  // Блок «Дежурство» (типы — ниже): только у движка с StateUrl (второй движок). Нет поля
  // или null — блока нет (основной стенд); arbiter:null — StateUrl у движка нет.
  duty?: LocalModelDuty | null;
}

// === Стенд второй модели (второй движок): GET /api/local-model/engines, stats?engine= (разрез v2, §5) ===
//
// Ключ словаря движков: 'main' — основной стенд (то, что было до фичи), имя движка LocalEngines (например 'gemma') —
// llama-server на Video. Ключ — только ключ: URL из него не строится никогда.
// Обе ручки: админ (иначе 403), флаг local-model-stats на сервере (иначе 404);
// неизвестный движок или выключенная Llm → configured:false, 200; 500 — нигде.

// 'main' — основной стенд; любое другое — имя движка LocalEngines из конфига сервера (например 'gemma').
export type LocalModelEngineName = string;

// Ответ GET /api/local-model/engines: список для переключателя вкладок моделей.
// configured=false — движок не настроен: его сегмента нет; с одним движком переключателя нет.
export interface LocalModelEngine {
  name: LocalModelEngineName;
  // подпись сегмента («Flash-Next», «Gemma 4»): имена моделей в сегмент не ставим (макет §2)
  title: string;
  configured: boolean;
}

// Состояние дежурства из /state арбитра (gpu-arbiter.py): пять значений, всё неизвестное
// сервер сводит к 'unknown'. null — удачного снимка арбитра ещё не было (до первого успеха).
export type LocalModelArbiterState = 'unknown' | 'ready' | 'yielded' | 'starting' | 'waiting-vram';
// Режим карты: 'exclusive' — карта целиком у модели, 'shared' — рядом с ComfyUI. Всё прочее — null.
export type LocalModelArbiterMode = 'exclusive' | 'shared';
// Звено цепочки «модель → страховка → облако» (EngineCallLedger.Step, разрез v2, §4).
// Третье звено — финальный облачный шаг (модель места или слота, обычно claude);
// на фронте подписано «облако» (макет §5).
export type LocalModelChainLink = 'engine' | 'insurance' | 'claude';
// Вид задания ComfyUI по шву IComfyStats.DescribePromptKind (image|video|audio|train→lora);
// не опознано — null. Таблица соответствия живёт на бэкенде, фронт её не повторяет (разрез §1).
export type LocalModelJobKind = 'image' | 'video' | 'audio' | 'lora';

// Причина уступки из /state арбитра. kind — вид строки арбитра: 'comfy' (задание ComfyUI,
// по префиксу prompt_id), 'lease' (аренда из POST /lease — текст присылает кто угодно из
// сети), 'gpu' (чужой процесс на карте). raw — текст строки без префикса вида: сервер режет
// строку целиком на входе — «lease:» до 64 символов, прочие причины — общим потолком 80
// (для gpu — «pid:командная строка», для comfy — префикс prompt_id (8 символов)).
// Значение вне списка — неизвестный вид: показывать raw как текст, без перехода.
export interface LocalModelDutyReason {
  kind: string;
  raw: string;
  // только для kind='comfy': вид задания по шву Images; null — не опознан
  jobKind?: LocalModelJobKind | null;
  // только для kind='comfy': есть ли prompt_id в реестре ЭТОГО инстанса ComfyUI.
  // false — «задание ComfyUI не с этого сервера» (ComfyUI общий у прода и дева); null — неизвестно
  known?: boolean | null;
}

// Активная аренда карты. Идентификатор аренды наружу не отдаётся (разрез §2): только
// причина (недоверенный текст, ≤64 символа) и остаток времени.
export interface LocalModelDutyLease {
  reason: string;
  leftS?: number | null;
}

// Ответ арбитра о дежурстве. Всё из недоверенного /state: неверный тип поля обнуляет
// только это поле; причин и аренд — не больше 32 каждой. При reachable:false — поля
// последнего удачного снимка; до первого успеха — null.
export interface LocalModelDutyArbiter {
  reachable: boolean;
  // unix-мс последнего удачного ответа арбитра; null — удачного ответа не было
  lastSeenAt?: number | null;
  state?: LocalModelArbiterState | null;
  mode?: LocalModelArbiterMode | null;
  // секунды до возврата модели; null — есть причины или модель уже активна
  quietLeftS?: number | null;
  // unix-мс начала периода счётчиков: сервер пересчитывает по часам Video (sinceAt = now − (ts − since)),
  // часы Video и CCS расходятся. null — снимка не было. Период фронт подписывает всегда
  // «с запуска арбитра»: HH:MM из чужих часов — ложная точность (решение Софьи, макет html:512)
  sinceAt?: number | null;
  yields?: number | null;
  returns?: number | null;
  // секунды последней уступки / последнего возврата; null — не было
  lastYieldS?: number | null;
  lastReturnS?: number | null;
  reasons?: LocalModelDutyReason[] | null;
  leases?: LocalModelDutyLease[] | null;
}

// Ответы мест за окно (EngineCallLedger, разрез v2, §4): корзины — звено, которое
// ответило; missed — цепочка вернула пусто. sinceAt = max(now − 1 ч, старт процесса):
// честный знаменатель доли. Нули — не «нет данных»: нет данных — весь блок null.
export interface LocalModelDutyAnswers {
  engine: number;
  insurance: number;
  claude: number;
  missed: number;
  sinceAt: number;
}

// Вызов места прямо сейчас («Кто сейчас на модели» у второй движок). Это журнал вызовов мест,
// а не чаты: строка не кликабельна, владельца и промпта нет (разрез v2, §4).
// place — название места из каталога; link — текущее звено цепочки; null — «ждёт в
// очереди»: вызов уже в журнале, но ещё не вошёл ни в одно звено (макет §4, строка
// «ждёт в очереди»); durationS — секунды с начала вызова; null — ещё не начался.
export interface LocalModelDutyCall {
  place: string;
  // null — вызов ждёт в очереди, звена ещё нет (так отдаст бэк)
  link: LocalModelChainLink | null;
  durationS?: number | null;
}

// Блок «Дежурство» в ответе stats?engine= (макет §5). Форма поля chain контрактом
// не зафиксирована (Б3c ещё в работе) — здесь прочитано как звенья типичного пути;
// цепочка ТИПИЧНЫЙ путь, а не гарантированный, текущее звено — из callsNow и состояния
// арбитра, не отсюда. На Ф5 (интеграция на живом ответе) поле сверяется заново.
export interface LocalModelDuty {
  // null — у движка нет StateUrl: «Дежурство» показывает «арбитр не подключён»
  arbiter: LocalModelDutyArbiter | null;
  chain?: LocalModelChainLink[] | null;
  answersLastHour?: LocalModelDutyAnswers | null;
  callsNow?: LocalModelDutyCall[] | null;
}

// === Вкладка «Медиа» (ComfyUI): GET /api/local-model/stats/media?since=<unix ms> ===
//
// Снимок второй видеокарты и ComfyUI: статус стенда, слоты очереди, идущие/ждущие
// прогоны и завершённые за последний час. Время — unix-мс; чужие прогоны анонимны
// (own=false, без sessionId/projectId, title «Чат другого пользователя»).
//
// configured=false — подсистема images/ComfyUI выключена (status:disabled, 200, не 500).
// gpus=null — данные карты медиа ещё не приходят с узла ComfyUI (шаг 31).
export interface LocalModelMedia {
  configured: boolean;
  // 'all' — все пользователи (админ); 'self' — только свои (обычный пользователь).
  // Старые ответы без поля трактуем как 'all'
  scope?: 'all' | 'self';
  // Сколько задач стоит перед текущим пользователем в очереди; null — сервер не сообщает
  queueAhead?: number | null;
  comfy: {
    // working | busy | down | disabled
    status: 'working' | 'busy' | 'down' | 'disabled';
    // unix-мс; null — стенд не мёртв
    downSince?: number | null;
    version?: string | null;
  };
  // Слоты очереди ComfyUI: «N из M мест»
  slots: { used: number; limit: number };
  // Видеокарта медиа; null до шага 31 (данные карты — с узла ComfyUI, не с хоста)
  gpus?: MediaGpu[] | null;
  // Сводка «Железо» (только админ): строка-ссылка «Видеокарты» по картам с ролью media.
  hardware?: import('./hardwareTypes').HardwareBrief;
  // «Загружено сейчас»: имена моделей в памяти узла (basename, ≤ 96 символов);
  // null/отсутствует — узел не сообщает (маршрут /ccs/stats не установлен)
  loadedModels?: string[] | null;
  running: MediaRunning[];
  queue: MediaQueue[];
  lastHour: MediaDone[];
  unsupported?: string[];
}

// Видеокарта медиа (ComfyUI): числовые поля nullable — [N/A] → null.
export interface MediaGpu {
  uuid: string;
  index: number;
  name: string;
  // Роли карты: в DTO бэкенда поле всегда присутствует (default []), не optional
  roles: string[];
  utilPerc?: number | null;
  memUsedMb?: number | null;
  memTotalMb?: number | null;
  tempC?: number | null;
  powerW?: number | null;
  powerLimitW?: number | null;
  mediaMemMb?: number | null;
}

// Прогресс семплера: текущий шаг, всего шагов, текущая стадия (если известна).
export interface MediaSteps {
  step: number;
  total: number;
  stage?: number | null;
}

// Идущий прогон ComfyUI. Own — прогон текущего пользователя (sessionId/projectId
// отдаются, title null — фронт резолвит по sessionId); чужой — «Чат другого
// пользователя», id/проект null. OwnerId наружу не уходит. Foreign — чужой источник.
export interface MediaRunning {
  kind: string;
  op: string;
  heavy: boolean;
  own: boolean;
  sessionId?: string | null;
  projectId?: string | null;
  title?: string | null;
  foreign?: string | null;
  steps?: MediaSteps | null;
  percent?: number | null;
  percentExact?: boolean | null;
  etaSeconds?: number | null;
  remainingSeconds?: number | null;
  // unix-мс момента начала; null — ещё не стартовал
  startedAt?: number | null;
}

// Прогон в очереди ComfyUI. Position — 1-based позиция. Анонимизация как в MediaRunning.
export interface MediaQueue {
  position: number;
  kind: string;
  op: string;
  heavy: boolean;
  own: boolean;
  sessionId?: string | null;
  projectId?: string | null;
  title?: string | null;
  foreign?: string | null;
  etaSeconds?: number | null;
  startInSeconds?: number | null;
}

// Завершённый за последний час прогон. Outcome — completed|failed|cancelled.
export interface MediaDone {
  kind: string;
  op: string;
  own: boolean;
  outcome: string;
  runSeconds?: number | null;
  etaSeconds?: number | null;
  // unix-мс момента завершения
  finishedAt: number;
}

// === История медиа-генераций (ADR-030): GET /api/local-model/history ===
//
// 7 дней на диске, переживает рестарты. Запись — один прогон ComfyUI по prompt_id;
// события журнала (enqueued/started/finished) бэкенд склеивает. В отличие от прочих
// ручек фичи: времена — ISO-8601 UTC (не unix-мс), и обе ручки проверяют и роль
// admin (403), и серверный флаг local-model-stats (без него — 404).
// owner — логин владельца; название чата/проекта и sessionId/projectId НЕ отдаются
// ни у чужих, ни у своих записей (строка и карточка одинаковы для всех).
export interface MediaHistoryRecord {
  // prompt_id
  id: string;
  // image | video | audio | train | other
  kind?: string | null;
  // операция: generate_image, text_to_video, …
  op?: string | null;
  model?: string | null;
  // промпт, ≤ 4 КБ; null — не записан
  prompt?: string | null;
  // параметры (белый список: width, height, durationSec, frames, steps, seed,
  // count, engine, fast, lora); null — не записаны
  params?: Record<string, unknown> | null;
  // логин владельца; null — пользователь не найден
  owner?: string | null;
  // completed | failed | cancelled | interrupted | running | queued | unknown
  status: string;
  // исход; null, пока запись не завершена
  outcome?: string | null;
  // недоверенный текст ComfyUI, ≤ 500 символов
  error?: string | null;
  runSeconds?: number | null;
  enqueuedAt?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  // local-media | image-editor | audio-editor | video-editor | lora
  source?: string | null;
}

// Страница истории: записи newest-first, курсор следующей страницы (null — конец
// ленты), счётчики для меню фильтров (с учётом остальных активных фильтров).
export interface MediaHistoryPage {
  records: MediaHistoryRecord[];
  // 'all' — все пользователи (админ); 'self' — только свои (обычный пользователь).
  // Старые ответы без поля трактуем как 'all'
  scope?: 'all' | 'self';
  nextCursor: string | null;
  counters: { kinds: Record<string, number>; outcomes: Record<string, number> };
}

// Запрос к истории. Все фильтры необязательны; from — ISO-дата начала периода
// (24 ч / 7 дней / сегодня), to бэкенд не требует. cursor — непрозрачная строка
// предыдущей страницы; limit 1..50 (по умолчанию 50).
export interface MediaHistoryQuery {
  kind?: string | null;
  model?: string | null;
  outcome?: string | null;
  login?: string | null;
  from?: string | null;
  to?: string | null;
  cursor?: string | null;
  limit?: number;
}

// Итог завершённого хода на локальной модели: суммы по запросам хода. Числа
// nullable — движок не отдал поле (на экране «—»). Тот же состав — у hub-события
// local_model_turn_stats (types/index.ts).
export interface LocalModelTurnStats {
  genTps?: number | null;
  prefillTps?: number | null;
  ttftMs?: number | null;
  queueMsApprox?: number | null;
  cacheHitPerc?: number | null;
  outTokens?: number | null;
  // Атрибуция по подсказочным заголовкам CLI (ADR-029): разрез по классам запросов хода.
  // 0 — класс не встречался (не null). agentTypes — null, если сабагентов не было.
  mainRequests?: number;
  subagentRequests?: number;
  compactionRequests?: number;
  auxiliaryRequests?: number;
  agentTypes?: string[] | null;
  compactionKind?: 'auto' | 'manual' | 'reactive' | null;
  // Утечки вызова инструмента в текст (vLLM qwen3_coder): число text-блоков основного
  // агента с маркерами XML-формата вызова. 0 — не было (не null).
  toolCallLeakTail?: number;
  toolCallLeakWholeCall?: number;
}

// Ответ GET /api/sessions/{id}/local-model: lastTurn — null, если ходов на
// локальной модели не было (или после рестарта бэкенда: счётчик ходов в памяти).
export interface LocalModelTurnResponse {
  lastTurn: LocalModelTurnStats | null;
}

// === Вкладка «Встречи» (конвейер транскрибации): GET /api/local-model/transcription ===
//
// Один снимок состояния конвейера: предложение v1 (docs/mockups/
// local-model-transcription-proposal.md, раздел «Черновик данных с бэкенда»), только админ,
// опрос раз в 5 с при открытой вкладке. Время — unix-мс (как в прочих ручках фичи).
//
// Бэкенда ещё нет: имена и форма полей — по таблице предложения и сверяются заново
// при постановке бэкенда (как duty у LocalModelDuty на Ф5). Поэтому всё, чего в
// реальном снимке может не быть («Сейчас» при простое, «Сверка» до первой проверки),
// — optional/nullable: частичный или пустой ответ даёт пустые блоки вкладки, а не
// падение разбора. Неизвестные поля ответа просто не читаются (JSON → T).

// Фаза конвейера: 'batch' — идёт пачка, 'idle' — пачки нет, 'stopped' — сервис остановлен.
export type TranscriptionPhase = 'idle' | 'batch' | 'stopped';
// Текущий этап файла: распознавание → сводка → загрузка в Dify (полоса «Сейчас»).
export type TranscriptionStage = 'asr' | 'summary' | 'dify';
// Уровень тревоги и точки вкладки. Считает БЕКЕНД (порог «Завис?» 5 мин, красное/жёлтое
// условие точки): фронт только красит по level, свои пороги не дублирует. Нет поля или
// null — уровня нет: ни точки на вкладке, ни баннера.
export type TranscriptionLevel = 'ok' | 'warn' | 'danger';
// Состояние зависимости: 'unknown' — ещё не проверялось (не «не отвечает»).
export type TranscriptionDepState = 'ok' | 'slow' | 'down' | 'unknown';

// Файл в очереди распознавания. since — unix-мс, с когда файл ждёт («ждёт N мин»).
export interface TranscriptionQueuedFile {
  file: string;
  folder: string;
  since: number;
}

// Сбой за сутки (journal.tsv, type=fail). comment — строка журнала целиком.
// folder/file — nullable по бэку (TranscriptionSnapshot.cs: TranscriptionFailDto несёт
// string? Folder, string? File): сбой вне файла (сбой всего конвейера, не одного файла) —
// строка журнала есть, имени файла нет. Строку показываем, имя не выдумываем.
export interface TranscriptionFail {
  // unix-мс сбоя
  at: number;
  folder: string | null;
  file: string | null;
  // минуты, которые потеряла запись (для баннера «4 встречи не попали в Dify»)
  minutes?: number | null;
  comment?: string | null;
}

// Завершённая встреча (journal.tsv, type=ok). mdPath — путь .md во «Встречах»:
// касание строки открывает его (UI следующего шага).
// folder nullable по бэку (TranscriptionMeetingDto: string? Folder); file — string:
// ридер отбрасывает строки журнала без имени (TranscriptionStateReader.cs: recent
// фильтрует r.File is not null), на фронте это единственный путь к имени строки.
export interface TranscriptionMeeting {
  // unix-мс завершения
  at: number;
  folder: string | null;
  file: string;
  minutes?: number | null;
  speakers?: number | null;
  comment?: string | null;
  mdPath?: string | null;
}

// Строка сверки с Dify по папке: local — записей локально, inDify — загружено в Dify.
// «не хватает N» = local − inDify (N > 0); «сходится» — равны.
// Оба числа nullable по бэку (TranscriptionDifyDto: int? Local, int? InDify): local —
// не посчитан, inDify — конвейер не получил список Dify. Дефицита из неизвестного нет:
// строки с null отсекает typeof-фильтр в transcriptionFormat (он остаётся).
export interface TranscriptionDifyRow {
  folder: string;
  local: number | null;
  inDify: number | null;
}

// Зависимость конвейера (ComfyUI/CcsAudioWorker, Strata, Dify, монтирование папки).
// key — идентификатор для подписи и иконки; имя поля состояния будет сверено с бэкендом
// (в таблице предложения оно не названо).
export interface TranscriptionDep {
  key: string;
  state: TranscriptionDepState;
  // unix-мс последней проверки; null — не проверялось
  checkedAt?: number | null;
}

// Снимок вкладки: GET /api/local-model/transcription (admin). Поля сгруппированы по
// блокам макета (конвейер → сейчас → очередь → сбои → встречи → сверка → зависимости),
// порядок в ответе не значим.
export interface TranscriptionSnapshot {
  // === Конвейер === (logs/state.json + systemctl + конфиг конвейера, каждый опрос).
  phase: TranscriptionPhase;
  // unix-мс последней записи state.json («Обновлено N назад», порог «Завис?»)
  updatedAt: number;
  // «Где работает»: узел, где читается снимок — узел CCS (файлы конвейера читаются
  // локально, TranscriptionStateReader.cs:78,520-522); null — локальный узел не настроен.
  // pid процесса из снимка; null — снимок его не несёт
  host?: string | null;
  pid?: number | null;
  // жив ли процесс (systemctl is-active)
  alive?: boolean;
  // «Пишет в»: узел и каталог вывода — прямой ответ на 18.11 (конвейер писал на ai,
  // «Встречи» читали с worker). Жёлтую строку «не туда» фронт считает по прямым полям:
  // outputHost=null при известном outputDir (transcriptionFormat.ts, writesWarn); бэкенд
  // своим outputLevel складывает это в level — для точки вкладки (TranscriptionStateReader.cs:90-103,127)
  outputHost?: string | null;
  outputDir?: string | null;
  // уровень тревоги — только показываем, сами не выводим (см. TranscriptionLevel)
  level?: TranscriptionLevel | null;

  // === Сейчас === (state.json + summary_state.json, каждый опрос).
  // Ничего не обрабатывается — полей нет: вкладка рисует «Ничего не обрабатывается».
  file?: string | null;
  folder?: string | null;
  stage?: TranscriptionStage | null;
  // unix-мс начала текущего этапа; durationMin — сколько идёт файл
  stageStartedAt?: number | null;
  durationMin?: number | null;
  // место в пачке: 1-based индекс и её длина
  batchIndex?: number | null;
  batchSize?: number | null;

  // === Очередь ===: asrQueue — ждут распознавания (список), summaryQueue — ждут сводку (число).
  asrQueue?: TranscriptionQueuedFile[];
  summaryQueue?: number | null;

  // === Сбои за 24 ч ===: хвост journal.tsv (type=fail), не больше 20 строк.
  fails?: TranscriptionFail[];

  // === Последние встречи ===: journal.tsv (type=ok), newest-first, не больше 20 строк.
  recent?: TranscriptionMeeting[];

  // === Сверка с Dify === (журнал systemd, раз в 30 мин). До первой проверки — полей нет.
  dify?: TranscriptionDifyRow[];
  checkedAt?: number | null;

  // === Зависимости ===: пинг ComfyUI/CcsAudioWorker, Strata, Dify, монтирования папки
  // (на сервере раз в 30–60 с).
  deps?: TranscriptionDep[];
}
