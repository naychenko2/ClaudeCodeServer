// Типы домена «MCP-реестр и каталог серверов» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// --- MCP-серверы: личный реестр владельца (фича mcp-registry) ---

// Пара «имя — значение» из env/headers. Секретное значение наружу не выходит:
// value = null при hasValue = true (в форме вместо него отметка «задано»).
export interface McpValue {
  name: string;
  value?: string | null;
  hasValue: boolean;
  secret: boolean;
}

export interface McpAuth {
  kind: string;                 // none | apikey | bearer | oauth2
  headerName?: string | null;
  hasSecret: boolean;
  authorizationServer?: string | null;
  clientId?: string | null;
  expiresAt?: string | null;
  hasTokens: boolean;
}

// Последнее известное состояние сервера: из system/init хода или из пробы по кнопке.
// status — строка, набор значений живёт на бэке (connected/failed/needs-auth/unknown).
export interface McpServerStatus {
  status: string;
  observedAt: string;
  source: string;               // init | probe
  sessionId?: string | null;
  error?: string | null;
}

export interface McpServer {
  id: string;
  key: string;
  toolKey: string;              // «mcp:<ключ>» — ключ привязки инструмента у персоны
  label: string;
  description?: string | null;
  transport: string;            // stdio | http | sse
  command?: string | null;
  args: string[];
  env: McpValue[];
  url?: string | null;
  headers: McpValue[];
  auth: McpAuth;
  enabled: boolean;
  alwaysLoad: boolean;
  allowReadOnlyPersonas: boolean;
  // Доезжать ли в чаты вне проектов при allow-модели (флаг mcp-allowlist); deny-ветка не читает
  allowOutsideProjects: boolean;
  // «Для участников» (shared-projects, S2a-7/S2a-F6): в узкой рамке общего проекта это единственный
  // путь реестровой записи в ход (McpDelivery). Пометку меняет только владелец записи
  // (PUT {id}/shared); без неё сервер в ходах общего проекта недоступен и не показывается.
  sharedWithMembers: boolean;
  source: string;               // manual | legacymcpconfig | legacyuserscope
  authVersion: number;
  createdAt: string;
  updatedAt: string;
  status?: McpServerStatus | null;
  // Указатель на источник из каталога (волна 1, McpCatalogRef): «реестровое» имя/версия
  // и дата появления в реестре. Сервер приехал из каталога, если поле задано; без
  // CatalogRef запись заведена руками. Сама строка запуска лежит в command/args/url —
  // CatalogRef её НЕ дублирует, только помечает происхождение
  catalogRef?: McpCatalogRef | null;
  // Группа встроенного сервера продукта: задаётся только для ключей из
  // McpRegistry.ReservedKeys / IntegrationKeys / pmem_*. У интеграций (dify,
  // fal-ai, glif, higgsfield) каскад «включить в проекте / выдать персоне»
  // снят — запись доставляется во все чаты по факту OAuth-входа. Значение
  // присылает бэкенд через McpRegistry.BuiltinGroupOf, фронту — источник
  // правды для фильтрации «Доступа». Не-null для встроенных, null/undefined
  // для обычных записей реестра.
  group?: McpBuiltinGroup | null;
}

// Указатель на каталожную запись в McpServerDto и McpServerUpsert. Поля стабильны
// и не разрастаются: реестровое имя, точная semver-версия и дата публикации. Этого
// хватает для дедупа и показа «в реестре с …», полный слепок server.json намеренно
// не хранится — иначе дрейф каталога и записи расходились бы молча
export interface McpCatalogRef {
  name: string;
  version: string;
  publishedAt?: string | null;
}

// Наблюдаемый сервер вне личного реестра: записи в реестре нет, есть только наблюдение
// статуса. group — кто подключил сервер и кто им управляет: product — сервисы AI Home
// (tasks, notes, wsp…), integration — интеграции с внешними системами (dify/fal-ai/glif),
// persona-memory — выделенная память персоны-консультанта (pmem_<handle>),
// external — наследство CLI из глобального .mcp.json/~/.claude.json и плагинов.
// Незнакомое значение фронт показывает как external — сервер подключён, скрывать его нельзя
export type McpBuiltinGroup = 'product' | 'integration' | 'persona-memory' | 'external';

export interface McpBuiltinServer {
  key: string;
  group: McpBuiltinGroup;
  status: McpServerStatus | null;
}

// Пара формы: пустое value у секрета = «оставить как было»
export interface McpValueInput {
  name: string;
  value?: string | null;
  secret?: boolean;
}

export interface McpServerUpsert {
  key?: string;
  label?: string;
  description?: string | null;
  transport?: string;
  command?: string | null;
  args?: string[];
  env?: McpValueInput[];
  url?: string | null;
  headers?: McpValueInput[];
  auth?: { kind?: string; headerName?: string | null; secret?: string | null; clientId?: string | null };
  enabled?: boolean;
  alwaysLoad?: boolean;
  allowReadOnlyPersonas?: boolean;
  allowOutsideProjects?: boolean;
  // Указатель на каталожную запись. В Create идёт от черновика импорта (поле задано),
  // в PUT — отсутствует или совпадает с прежним; бэкенд по нему включает SSRF-гейт пробы
  catalogRef?: McpCatalogRef | null;
}

export interface McpProbeResult {
  ok: boolean;
  status: string;
  serverName?: string | null;
  toolCount?: number | null;
  toolNames?: string[] | null;
  error?: string | null;
}

// === Каталог MCP-серверов (волна 1) ===

// Сервер из реестра: всё, что нужно карточке каталога и предзаполнению формы. Форма
// контракта — бэкенд `McpCatalogCardDto`/`McpCatalogPrefillDto` в
// backend/.../Mcp/Catalog/McpCatalogModels.cs. Значения секретов из реестра НЕ
// приходят — их вводит человек на форме, хранит бэкенд в защищённом сторе
export interface McpCatalogField {
  // Куда положить значение: env | header | url | args. По target'у решается, в env
  // черновика идёт поле или в argv строки запуска. Бэкенд гарантирует одно из четырёх
  target: 'env' | 'header' | 'url' | 'args';
  // Технический ключ: имя переменной окружения (Authorization, ROOT_PATH) или аргумента
  name: string;
  // Человеческая подпись («внутренний токен интеграции Notion»). Пусто — рисуем по `name`
  description?: string | null;
  // Обязательное для запуска. Без значения по умолчанию проба падает без объяснения —
  // фронт рисует звёздочку и плашку «обязательное»
  required: boolean;
  // Значение секрета — хранится в защищённом сторе, не в архиве, не в логах
  secret: boolean;
  // Дефолт из реестра. Секреты сюда НЕ кладутся (см. п.3 «Принципов» плана каталога)
  default?: string | null;
}

// Предзаполнение формы заведения из реестра. Описания полей живут только в сессии
// импорта: в McpServerRecord не кладутся. Карточка каталога читает Transport отсюда,
// форма — Command/Url/Fields. Nullable, потому что подключить можно не всё
// (Connectable=false → Prefill=null, см. карточку)
export interface McpCatalogPrefill {
  // Сгенерированный ключ записи в реестре владельца (имя из реестра + slug). Форме
  // нужен как подсказка, но итоговый ключ человек правит руками
  key: string;
  // Подпись из реестра, отличная от title карточки. Не задан — берём title
  label?: string | null;
  // Описание из реестра. Не задано — берём описание карточки
  description?: string | null;
  // Способ подключения из реестра. stdio даёт npm-команду, remote — URL
  transport: McpCatalogTransport;
  // Рантайм stdio-сервера (npx/uvx/…) — фиксированная версия в Args, не здесь. У
  // http-серверов поле остаётся пустым, имя хоста лежит в Url
  command?: string;
  // Полный argv stdio-сервера (рантайм-флаги, имя пакета@версия, позиционные и
  // именованные аргументы). Плейсхолдеры {name} подставляет форма значениями
  // соответствующих полей с target='args' — без этого запись создалась бы без
  // пакета (McpCatalogMapper кладёт `pkg@version` сюда). Совпадает с DTO
  // McpCatalogPrefillDto.Args 1:1
  args: string[];
  // Адрес remote-сервера
  url?: string;
  // Поля формы в порядке реестра. Описание и обязательность едут ТОЛЬКО сюда и в
  // сессии импорта — в McpServerRecord не кладутся
  fields: McpCatalogField[];
}

// Транспорт сервера из каталога. stdio даёт npm-команду (npx -y …) и параметры; remote
// даёт URL. Поле «почему нельзя подключить» приезжает с сервером, у которого транспорт
// или источник пакета не поддержан первой волной (Docker/oci/mcpb/nuget, чужой
// package registry, помеченный deprecated — отдельные статусы)
export type McpCatalogTransport = 'stdio' | 'http';

export interface McpCatalogServer {
  // Реестровое имя («io.github.modelcontextprotocol/filesystem») — идёт в CatalogRef.name
  name: string;
  // Человекочитаемое имя для карточки («Filesystem», «inference.sh», «Tandem docs»).
  // В DTO помечено как nullable: часть записей реестра имени не даёт, тогда показываем name
  title?: string | null;
  // Описание из реестра. Может быть пустым — рендерим только если есть
  description?: string | null;
  // Точная semver-версия из реестра — фиксируется в CatalogRef.version, чтобы запись
  // не «уплыла» при выходе новой. Может быть пустой — пишем «без версии»
  version?: string | null;
  // Ссылка на репозиторий автора (полный URL). Может быть null — тогда ссылки нет
  repositoryUrl?: string | null;
  // «В реестре с …»: ISO-дата для подписи «сентября 2025». Может быть null
  publishedAt?: string | null;
  // Статус реестра (active/deprecated/deleted). deleted в волне 1 не показываем
  // (фильтр на бэке, см. план §5)
  status?: string | null;
  // Подключить из каталога нельзя (тип пакета/аргументы/версия/окружение/отзыв).
  // Причина — в notice. Подсказки типа «npm» в полях transport больше нет —
  // Transport лежит в prefill (prefill=null у Connectable=false)
  connectable: boolean;
  // Причина отказа в подключении: неподдержанный транспорт/источник, deprecated и пр.
  // Задана → карточка без кнопки «Подключить» и первой строкой выводится эта причина
  notice?: string | null;
  // Признак «в реестре это latest». Фронт не рисует — оставлено на будущее, чтобы
  // не ломать текущий JSON при расширении схемы
  isLatest?: boolean;
  // Предзаполнение формы заведения. Есть у подключаемых записей, null — у отказанных
  prefill?: McpCatalogPrefill | null;
}

// Ответ GET /api/mcp/catalog/search: список и курсор пагинации. Имена полей
// `items`/`nextCursor` зафиксированы на бэке (McpCatalogSearchResult в
// backend/.../McpCatalogModels.cs) и в docs/architecture/mcp-registry.md —
// фронт должен читать их как есть. q поиска — на бэке, пустой запрос
// возвращает свежий срез каталога. error — поле фетча (сеть/500), бэкенд в
// успешном 200 его не возвращает
export interface McpCatalogSearchResult {
  items: McpCatalogServer[];
  nextCursor?: string | null;
  error?: string | null;
}

// Описание поля формы, прочитанное из реестра. Живёт ТОЛЬКО в сессии импорта
// каталога — в McpServerRecord НЕ кладётся (план §7): в записи секреты хранятся в
// защищённом сторе, а подписи полей к ней не нужны, они нужны ТОЛЬКО предзаполнению
// формы. Когда человек открывает существующую запись на правку, источник описаний —
// бэкенд (для каталожной записи он их отдаёт), а не реестр. McpServerForm принимает
// такой черновик от McpCatalogPanel и кладёт в локальный стейт формы.
// Поля, у которых target='args', идут в строку запуска как argv; target='header' —
// в HTTP-заголовки; target='env' — в переменные окружения; target='url' — плейсхолдер
// в URL remote-сервера (UI первой волны не правит, рендерим как env с пометкой)
export type McpCatalogFieldDraft = McpCatalogField;

// Превью черновика, который каталог передаёт форме. Поля формы (fieldsDraft) едут
// вместе с каталожными метаданными (CatalogRef, имя), чтобы бэкенд увидел полный
// слепок происхождения уже на POST /api/mcp/servers
export interface McpServerCatalogDraft {
  source: McpCatalogServer;
  // Указатель, который ляжет в McpServerDto.catalogRef и в McpServerUpsert.catalogRef
  catalogRef: McpCatalogRef;
  // Поля черновика: форма раскидает их по env/headers по target'у из реестра
  fieldsDraft: McpCatalogField[];
}

// Ревизия каталожной записи (волна 2). Бэк возвращает по батчу имён (POST /mcp/catalog/revisions).
// Один заход ревизии — одна карточка в McpServerList. status: явно deprecated/deleted — отзыв
// в реестре, рисуем плашкой; deprecated=true поднимается из явного status или как самостоятельный
// сигнал бэка (для записей, где реестр отдаёт deprecated флагом без status); checkFailed: реестр
// лежит/неизвестная ошибка, НЕ отзыв, тон нейтральный (ТЗ «проверить не удалось» — не пугать);
// latestVersion: что в реестре сейчас, сверяем semver с версией записи и при новизне показываем
// пометку «в реестре новее». Совпадает с DTO McpCatalogRevisionItem на бэке
export interface McpCatalogRevision {
  name: string;
  // Статус реестра. Не задан — реестр ответил, но статуса у записи нет (нормально для active)
  status?: 'active' | 'deprecated' | 'deleted' | null;
  // Сервер явно помечен устаревшим (отдельный флаг бэка помимо status). Дублирует status='deprecated'
  // для записей, у которых реестр отдаёт deprecated флагом, а не строкой
  deprecated?: boolean | null;
  // Последняя версия из реестра (semver-строка). Не задан — реестр не отдал её
  latestVersion?: string | null;
  // Реестр временно/постоянно недоступен. ОТЛИЧНО от deprecated — рисуем нейтрально
  checkFailed?: boolean | null;
  // Сырой текст ошибки проверки (сеть/5xx/таймаут). Показывается только под плашкой
  // «проверить не удалось» — нейтральный тон, НЕ «отозван»
  error?: string | null;
  // true — latestVersion выше, чем версия в McpServerCatalogDraft.version. Совпадает с
  // DTO McpCatalogRevisionItem.HasNewerVersion 1:1
  hasNewerVersion?: boolean | null;
}

// Ответ батч-эндпоинта. items: всё, что реестр отдал по запрошенным именам —
// совпадает с бэковым { items = await catalog.ReviseAsync(queries, ct) }.
// missing: бэк НЕ возвращает; вычисляем фронтом как «запрашивали имя, а его нет в items».
// checkFailed: батч целиком не дошёл (сеть/5xx) — никаких индивидуальных пометок ставить
// нельзя, фронт раскладывает только то, что пришло. error: общая ошибка запроса
export interface McpCatalogRevisionResult {
  items: McpCatalogRevision[];
  checkFailed?: boolean | null;
  error?: string | null;
}

// Вход по OAuth (волна 7) — ответы POST .../oauth/start и .../oauth/complete
export interface McpOAuthStartResult {
  authorizeUrl: string;
  state: string;
  redirectUri: string;
}
export interface McpOAuthCompleteResult {
  ok: boolean;
  key: string;
}

// Диагностика вызовов MCP-инструментов (GET /api/mcp/calls, только админ)
export interface McpToolStat {
  tool: string;
  calls: number;
  failures: number;
  avgMs: number;
}

export interface McpCallFailure {
  at: string;
  tool: string;
  sessionId?: string | null;
  path: string;
  statusCode: number;
  elapsedMs: number;
}

export interface McpCallsResponse {
  tools: McpToolStat[];
  recentFailures: McpCallFailure[];
}

