// Вики владельца: стор дерева разделов, каталогов, страниц и операций над ними (S5a).
// С Ф2 (шаг F1) здесь же: ночная часть настроек (блок «Ночной разбор», оптимистичная правка
// с откатом) и пометки чата «важный»/«не разбирать».
// Модель — вики ВЛАДЕЛЬЦА с разделами projects/{id} | spheres/{id} | common | personal
// (ADR-036 §1), а не «вики проекта / личная вики». Человек в UI видит всю свою вики:
// видимости для моделей (WikiVisibility) здесь нет — ADR-036 §2, последний пункт.
// Паттерн стора — как lib/notes.ts: модульное состояние + подписки + useSyncExternalStore.
//
// Realtime — существующее событие notes_changed. Что по коду сегодня: поля `wiki` у него
// нет (бэк: NotesChangedMessage(Action, NoteId) — Core/Protocol/ServerMessage.cs:485), и
// на запись страниц вики бэк его не шлёт (в Notes/Controllers/WikiController.cs и
// Notes/Services/Wiki/WikiKeeper.cs нотификатора нет — страницы живут под
// data/wiki/{ownerId}/pages/, вне хранилища заметок). Реагируем на сам notes_changed:
// правку заметок и правку вики мог сделать один прогон Хранителя по пакету. Если бэк
// добавит поле `wiki` следующим — оно принимается как есть, реакция не изменится.

import { useCallback, useSyncExternalStore } from 'react';
import { readStoredUserId, request } from './offline';
import { joinUser, onMessage, onReconnected } from './signalr';

// ─── Типы ответов REST (контракт S2f, таблица в итоге задачи 945a98f2) ───
// Ключ раздела — строка бэкового WikiSections.Parse: projects/{id} | spheres/{id} |
// common | personal. Имена разделов бэк отдаёт живыми (по проекту/сфере); у сироты
// name = null — даты удаления проекта негде хранить.

export type WikiSectionKind = 'project' | 'sphere' | 'common' | 'personal';

export interface WikiSectionRef { kind: WikiSectionKind; id: string | null }

export interface WikiSectionDto {
  key: string;
  name: string | null;
  orphan: boolean;
  local: boolean;
  sphereReserved: boolean;
  pageCount: number;
}

// initialized — есть pages/_schema.md: «Вики не заведена», когда false
export interface WikiTreeDto { initialized: boolean; sections: WikiSectionDto[] }

// Состояние манифеста раздела (контракт доработки S5b): не-'ok' — тихая подсказка
// «манифест раздела не прочитан», НЕ плашка дрейфа (UI для него — не этот шаг)
export type WikiManifestState = 'ok' | 'missing' | 'invalid';

// summary/type/status — из шапки страницы (null — шапка не разобрана, страница остаётся видимой).
// drift/manifestState — их считает БЭК сверкой файла с манифестом, а не шапка status (шапку
// пишет модель — она не сигнал, ревью S5b M1). Поля опциональны: до появления в ответе
// отсутствие читаем как drift=false.
export interface WikiPageInfoDto {
  pageKey: string;
  title: string;
  summary: string | null;
  type: string | null;
  status: string | null;
  sourcesCount: number;
  updated: string | null;
  drift?: boolean;
  manifestState?: WikiManifestState;
}

// Оглавление раздела строится отсюда: проекции _index/_log через /api/wiki/page не читаются (400 wiki_page_key)
export interface WikiCatalogDto { key: string; name: string | null; orphan: boolean; pages: WikiPageInfoDto[] }

// drift — тот же сигнал, что у каталога: сверка файла с манифестом на бэке (доработка S5b)
export interface WikiPageDto { pageKey: string; content: string; drift?: boolean; manifestState?: WikiManifestState }

export interface WikiInitResult { initialized: boolean }
export interface WikiAdoptResult { status: 'applied' }
export interface WikiDeleteResult { deleted: boolean }

// ─── Ключ раздела ───

const PROJECTS_PREFIX = 'projects/';
const SPHERES_PREFIX = 'spheres/';

function idSegment(id: string, kind: 'project' | 'sphere'): WikiSectionRef | null {
  // id — ровно один сегмент: «projects/a/b» — не ключ
  return id && !id.includes('/') ? { kind, id } : null;
}

// Разбор ключа — зеркало бэкового WikiSections.Parse (lowercase-префиксы). null — не ключ
// раздела: физическое имя папки («Проекты/p1») ключом не считается, чужую строку в модель
// не пускаем (fail-closed, ADR-036 §2).
export function parseSectionKey(key: string): WikiSectionRef | null {
  if (key === 'common') return { kind: 'common', id: null };
  if (key === 'personal') return { kind: 'personal', id: null };
  if (key.startsWith(PROJECTS_PREFIX)) return idSegment(key.slice(PROJECTS_PREFIX.length), 'project');
  if (key.startsWith(SPHERES_PREFIX)) return idSegment(key.slice(SPHERES_PREFIX.length), 'sphere');
  return null;
}

export type WikiSectionsByKind = Record<WikiSectionKind, WikiSectionDto[]>;

// Разложить разделы дерева по четырём видам — группы экрана «Вики» («Проекты», «Сферы»,
// «Общее», «Личное»). Сирота остаётся в своей группе и помечается своим orphan из DTO:
// автоочистки нет, она удаляется по кнопке (ADR-036 §1).
export function groupSectionsByKind(sections: readonly WikiSectionDto[]): WikiSectionsByKind {
  const out: WikiSectionsByKind = { project: [], sphere: [], common: [], personal: [] };
  for (const s of sections) {
    const ref = parseSectionKey(s.key);
    if (ref) out[ref.kind].push(s);
  }
  return out;
}

// ─── Ошибки контракта ───

// pkg_* — отказы «Входящих» (WikiPackagesController + WikiPackageStore, S4a/S4g);
// читаются в экране пакета и списка (S7a). Коды применения (wiki_apply_*) — не отказы HTTP:
// они живут в поле apply ответа decide/accept-all и показываются как итог применения.
export type WikiErrorCode =
  | 'wiki_foreign'
  | 'wiki_section_key'
  | 'wiki_page_key'
  | 'wiki_project_missing'
  | 'wiki_section_off'
  | 'wiki_sphere_reserved'
  | 'wiki_refused'
  | 'wiki_drift'
  | 'wiki_not_orphan'
  | 'wiki_pkg_request'
  | 'wiki_pkg_not_found'
  | 'wiki_pkg_invalid'
  | 'wiki_pkg_not_decidable'
  | 'wiki_pkg_auto'
  | 'wiki_pkg_op_index'
  | 'wiki_pkg_already_decided'
  | 'wiki_pkg_reason_required'
  | 'wiki_pkg_nothing_to_decide'
  // Отказ отката пакета и отката по источнику (S4b-3/S4f) — читаются вызывающими эти операции
  | 'wiki_revert_shape'
  | 'wiki_revert_off'
  | 'wiki_revert_claim'
  | 'wiki_revert_status'
  | 'wiki_revert_manifest'
  | 'wiki_revert_no_preimages'
  | 'wiki_revert_no_applied'
  | 'wiki_revert_changed'
  | 'wiki_revert_internal'
  | 'wiki_retract_shape'
  | 'wiki_retract_off'
  | 'wiki_retract_internal'
  // Источник отката не найден или чужой (404 «такого источника в твоей вики нет»,
  // WikiPackagesController.cs:61) — читается в «Журнале» (S7c-2)
  | 'wiki_retract_source'
  // origin-фильтр журнала вне закрытого перечня (400, WikiController.cs:77) — «Журнал» (S7c-2)
  | 'wiki_origin_key'
  // класс операции вне пары «источник × операция» (400, параметр class журнала и настроек,
  // WikiController.cs:78 / WikiSettingsController.cs:51) — хвост ревью R-S7c-2
  | 'wiki_class_key'
  // Отказы прогона линта (S3-3): функция выключена / прогон владельца уже идёт.
  // Читаются в «Здоровье» (S5f)
  | 'wiki_llm_wiki_off'
  | 'wiki_lint_running'
  // Разбор по требованию (S7b-0): analyze / analyze-status (WikiChatsController) и ingest/note
  // (WikiController). Коды здесь ГОЛЫЕ, без префикса 'wiki_' — как константы бэка
  // (WikiChatIngestService.cs:93-97, WikiController.cs:81-84): контракт по ним, не по нашей косметике.
  | 'not_found'
  | 'task_execution'
  | 'marked_skip'
  | 'personal_section'
  | 'wiki_not_initialized'
  | 'packages_limit'
  | 'wiki_note_id'
  // Отбор чатов (S6e/S6f): файл разбора не читается (409 — WikiChatsController.cs:108,152,
  // код бэка WikiChatTriageStore.CodeInvalid) — читается экраном отбора (loadWikiChats)
  | 'wiki_triage_invalid'
  // Настройки ночного блока (NA-2): merged-блок ночного раздела кривой — 400 с кодом, файл
  // не тронут; отказ записи файла — 500 с кодом, а не 500 без тела (WikiSettingsController.cs:160-170)
  | 'wiki_nightly'
  | 'wiki_settings_write_failed'
  // Ручной запуск ночного разбора (WN-5, POST /api/wiki/settings/nightly/run): ночь выключена,
  // этот владелец уже проходит, настройки нечитаемы (WikiSettingsController.cs:84-87, 237-251);
  // «функция выключена» — уже известный wiki_llm_wiki_off, отдельного кода нет
  | 'wiki_nightly_running'
  | 'wiki_nightly_disabled'
  | 'wiki_settings_invalid'
  // Правка страницы человеком (F4a, ADR-038): файл правили ещё раз / единые «Заметки и Вики»
  // выключены / гейт записи отказал по существу (violations, issues)
  | 'wiki_page_changed'
  | 'wiki_unified_off'
  | 'wiki_edit_invalid'
  // Перенос заметки в вики (F5b, ADR-038): заметка уже перенесена (в ней ссылка) / target
  // задан там, где его задать нельзя (дом страницы — дом заметки)
  | 'wiki_note_transferred'
  | 'wiki_transfer_target'
  // «Решения за ночь» (view=night, J-J5): вид журнала вне перечня / даты окна (нет, не дата,
  // from позже to) — 400 (WikiController.cs:113-114, 621-625)
  | 'wiki_view_key'
  | 'wiki_date_range'
  // Отказы чистки ИИ-страниц (J-P2): манифест затронутого раздела невалиден (409), сбой
  // чтения или архива (503 — транзитный, повторить можно) (WikiKeeper.cs:173-175,
  // WikiController.cs:695-700); прочий отказ Хранителя — уже известный wiki_refused
  | 'wiki_purge_manifest'
  | 'wiki_purge_io'
  | 'wiki_purge_archive';

const WIKI_ERROR_CODES: ReadonlySet<string> = new Set<string>([
  'wiki_foreign', 'wiki_section_key', 'wiki_page_key', 'wiki_project_missing', 'wiki_section_off',
  'wiki_sphere_reserved', 'wiki_refused', 'wiki_drift', 'wiki_not_orphan',
  'wiki_pkg_request', 'wiki_pkg_not_found', 'wiki_pkg_invalid', 'wiki_pkg_not_decidable',
  'wiki_pkg_auto', 'wiki_pkg_op_index', 'wiki_pkg_already_decided', 'wiki_pkg_reason_required',
  'wiki_pkg_nothing_to_decide',
  'wiki_revert_shape', 'wiki_revert_off', 'wiki_revert_claim', 'wiki_revert_status',
  'wiki_revert_manifest', 'wiki_revert_no_preimages', 'wiki_revert_no_applied',
  'wiki_revert_changed', 'wiki_revert_internal',
  'wiki_retract_shape', 'wiki_retract_off', 'wiki_retract_internal', 'wiki_retract_source',
  'wiki_origin_key', 'wiki_class_key',
  'wiki_llm_wiki_off', 'wiki_lint_running',
  'not_found', 'task_execution', 'marked_skip', 'personal_section', 'wiki_not_initialized',
  'packages_limit', 'wiki_note_id', 'wiki_triage_invalid',
  'wiki_nightly', 'wiki_settings_write_failed',
  'wiki_nightly_running', 'wiki_nightly_disabled', 'wiki_settings_invalid',
  'wiki_page_changed', 'wiki_unified_off', 'wiki_edit_invalid',
  'wiki_note_transferred', 'wiki_transfer_target',
  'wiki_view_key', 'wiki_date_range',
  'wiki_purge_manifest', 'wiki_purge_io', 'wiki_purge_archive',
]);

export class WikiError extends Error {
  readonly status: number;
  readonly code: WikiErrorCode | null;
  // Поля тела отказа правки (F4a, аддитивно): свежая страница при 409 wiki_page_changed,
  // причины гейта при 400 wiki_edit_invalid. У прочих отказов — пусто.
  page: WikiConflictPage | null = null;
  violations: string[] = [];
  issues: WikiEditIssue[] = [];

  constructor(status: number, code: WikiErrorCode | null, message: string) {
    super(message);
    this.name = 'WikiError';
    this.status = status;
    this.code = code;
  }
}

// 401 и 404 приходят с пустым телом — кода нет, их отличаем по статусу; остальные отказы —
// {code,message} (хвост ревью S2f, п. б). Сетевой сбой, таймаут и отмена вызывающим — не
// ошибка контракта: возвращаем null, вызывающий видит прежний OfflineError/AbortError.
// Ошибку с status и телом прикрепляет сам offline.ts (request → httpErr.status/.body).
function toWikiError(e: unknown): WikiError | null {
  if (e instanceof WikiError) return e;
  const err = e as { status?: unknown; body?: unknown } | null;
  const status = typeof err?.status === 'number' ? err.status : 0;
  if (!status) return null;
  const body = err?.body as { code?: unknown; message?: unknown } | undefined;
  const code = typeof body?.code === 'string' && WIKI_ERROR_CODES.has(body.code)
    ? body.code as WikiErrorCode
    : null;
  const message = typeof body?.message === 'string' && body.message
    ? body.message
    : (e instanceof Error && e.message ? e.message : 'Не удалось прочитать вики');
  const we = new WikiError(status, code, message);
  readEditRefusalBody(we, body);
  return we;
}

// Путь: передаём в request() БЕЗ /api — request() префиксует их сам (offline.ts:31,
// BASE='/api'), на прово это /api/wiki/… — маршрут бэка ([Route("api/wiki")],
// WikiController.cs:30). «GET/POST /api/wiki/…» в комментариях ниже — запрос на прово,
// а не литерал в коде: двойной префикс /api/api давал 404 на всех экранах (дефект B2,
// QA P1); сторож реальных URL — lib/__tests__/wikiUrl.test.ts.
// init.live доходит до request() как есть: офлайн-кэш ВРЕДЕН отдельным чтениям —
// loadWikiChatAnalyzeStatus (доработка H2), по той же причине, что статусы в api.ts.
async function wikiHttp<T>(url: string, init?: RequestInit & { live?: boolean }): Promise<T> {
  try {
    return await request<T>(url, init);
  } catch (e) {
    const we = toWikiError(e);
    throw we ?? e;
  }
}

// ─── Состояние стора ───

// Порядок ответов (ревью S5a M1): на каждый слот — счётчик поколения, ответ кладётся
// в стор, только если его поколение — последнее запрошенное для этого слота; устаревший
// ответ (запрос начался раньше, пришёл позже) отбрасывается. Инвариант: последний
// запрос побеждает. Слот — дерево, каждый ключ каталога и страница (одна «последняя
// прочитанная» — значит, один счётчик: клик по B после A не может быть затёрт ответом A).
// Образец — featureFlags.ts (createFlagsRefresher: generation / mine === generation).
let _treeGen = 0;
const _catalogGen = new Map<string, number>();
let _pageGen = 0;

// Привязка к владельцу (ревью S5a M2): данные стора — вики того, кто вошёл. Стор держит
// владельца последней записанной загрузки; при смене readStoredUserId в этой же вкладке
// снимок прежнего владельца очищается и перечитывается. Ответ запроса помнит владельца
// момента запроса и в чужой снимок не пишется (образец owner-capture — offline.ts,
// ownerAtStart в writeFileContent).
let _ownerId: string | null = null;

let _tree: WikiTreeDto | null = null;
// Каталоги, которые кто-то открыл: их перечитываем на realtime и после своих мутаций.
// Не-сирота и сирота держатся одинаково — экран показывает всю вики владельца.
const _catalogs = new Map<string, WikiCatalogDto>();
// Последняя прочитанная страница (экран показывает одну за раз; кэша всех страниц нет)
let _page: { cacheKey: string; value: WikiPageDto } | null = null;
// Факт «стор сам снял эту страницу 404 перечитки» (W4-5, дефект 3f3449e4): отсутствие
// снимка — не различает «страница грузится» и «страницу снес откат», а экрану нужен именно
// этот факт, чтобы вернуться к каталогу. Отметка живёт только пока страница не
// перечитывается заново: начало чтения той же страницы снимает её (до ответа
// состояние — «грузится»).
let _pageGoneKey: string | null = null;
// Открытые сейчас страницы: счётчик подписок useWikiPage по ключу. «Открытая» для
// перечитки после отката — та, чей экран сейчас подписан: _page остаётся и после
// закрытия страницы, перечитывать по нему — тот хвост «перечитка закрытой поднимает
// поколение и выкидывает ответ только что открытой» (хвост перечитки после adopt)
const _openPages = new Map<string, number>();
let _loaded = false;
let _treeLoading: Promise<WikiTreeDto> | null = null;
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() {
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

function joinUserGroup() {
  const uid = readStoredUserId();
  if (uid) joinUser(uid).catch(() => {});
}

const sectionQuery = (key: string) => `section=${encodeURIComponent(key)}`;
const pageCacheKey = (sectionKey: string, pageKey: string) => `${sectionKey}\n${pageKey}`;
// Разбор ключа страницы — обратный pageCacheKey: ключ раздела и ключ страницы '\n' не
// содержат — бэковый разбор пути страницы отвергает управляющие символы (WikiSections.cs)
function splitPageCacheKey(key: string): { sectionKey: string; pageKey: string } | null {
  const i = key.indexOf('\n');
  return i < 0 ? null : { sectionKey: key.slice(0, i), pageKey: key.slice(i + 1) };
}

// Счётчик открытых страниц — одна пара операций для подписки экрана (useWikiPage) и для
// тестового шва (__test.openPage): «открытая» для перечитки — именно этот счётчик, а не
// содержимое стора (_page остаётся и после закрытия страницы).
function retainOpenPage(key: string): void {
  _openPages.set(key, (_openPages.get(key) ?? 0) + 1);
}

function releaseOpenPage(key: string): void {
  const n = (_openPages.get(key) ?? 1) - 1;
  if (n <= 0) _openPages.delete(key); else _openPages.set(key, n);
}

// Перечитать открытую страницу после мутации, меняющей содержимое страниц (откат
// пакета, откат по источнику): витрина под открытым экраном не должна показывать
// состояние до отката. Только пока страница остаётся открытой (счётчик подписок) —
// сбой перечитки не роняет мутацию (тот же порядок, что в adopt).
// 404 — страницу снес откат (retract снял хронику целиком): содержимое до отката
// снимается по ключу (тот же порядок, что в deleteWikiSection) и остаётся фактом в сторе
// (_pageGoneKey): экран по нему возвращается к каталогу раздела (W4-5, дефект 3f3449e4) —
// без этого намерение «страница открыта» живёт в компоненте и центр висит на скелетоне.
// Другие сбои snapshot не трогают и наружу не выходят.
async function reReadOpenPage(): Promise<void> {
  const page = _page;
  if (!page || !(_openPages.get(page.cacheKey) ?? 0)) return;
  const parts = splitPageCacheKey(page.cacheKey);
  if (!parts) return;
  try {
    await loadWikiPage(parts.sectionKey, parts.pageKey);
  } catch (e) {
    if (e instanceof WikiError && e.status === 404 && _page && _page.cacheKey === page.cacheKey) {
      _page = null;
      _pageGoneKey = page.cacheKey;
      emit();
    }
  }
}

// ─── Чтение ───

export async function loadWikiTree(): Promise<WikiTreeDto> {
  const gen = ++_treeGen;
  const owner = readStoredUserId();
  const tree = await wikiHttp<WikiTreeDto>('/wiki/tree');
  // Устаревший ответ или ответ чужого владельца в снимок не пишется (M1/M2);
  // вызывающий свой ответ получает как есть.
  if (gen === _treeGen && owner === readStoredUserId()) {
    _tree = tree;
    _ownerId = owner;
    _loaded = true;
    emit();
  }
  return tree;
}

export async function loadWikiCatalog(sectionKey: string): Promise<WikiCatalogDto> {
  const gen = (_catalogGen.get(sectionKey) ?? 0) + 1;
  _catalogGen.set(sectionKey, gen);
  const owner = readStoredUserId();
  const catalog = await wikiHttp<WikiCatalogDto>(`/wiki/section?${sectionQuery(sectionKey)}`);
  if (gen === _catalogGen.get(sectionKey) && owner === readStoredUserId()) {
    _catalogs.set(sectionKey, catalog);
    emit();
  }
  return catalog;
}

export async function loadWikiPage(sectionKey: string, pageKey: string): Promise<WikiPageDto> {
  const gen = ++_pageGen;
  // Начало чтения этой же страницы снимает отметку её отсутствия: до ответа состояние —
  // «грузится», а не «снесена откатом», и экран не закрывает страницу, которую только
  // что открыл (W4-5). Чужой ключ отметки не трогает.
  const goneKey = pageCacheKey(sectionKey, pageKey);
  if (_pageGoneKey === goneKey) _pageGoneKey = null;
  const owner = readStoredUserId();
  const page = await wikiHttp<WikiPageDto>(
    `/wiki/page?${sectionQuery(sectionKey)}&page=${encodeURIComponent(pageKey)}`,
  );
  if (gen === _pageGen && owner === readStoredUserId()) {
    _page = { cacheKey: pageCacheKey(sectionKey, pageKey), value: page };
    emit();
  }
  return page;
}

// Перечитать дерево и уже открытые каталоги — после своей мутации или realtime.
// Сбой одной перечитки не роняет остальные и не затирает прежние данные.
export async function refreshWiki(): Promise<void> {
  const jobs: Promise<unknown>[] = [loadWikiTree().catch(() => undefined)];
  for (const key of [..._catalogs.keys()]) jobs.push(loadWikiCatalog(key).catch(() => undefined));
  await Promise.all(jobs);
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(msg => {
    if (msg.type !== 'notes_changed') return;
    void refreshWiki();
  });
  onReconnected(() => { joinUserGroup(); void refreshWiki(); });
}

export async function ensureWikiLoaded(): Promise<void> {
  wireRealtime();
  joinUserGroup();
  const uid = readStoredUserId();
  // Смена аккаунта в той же вкладке: вики прежнего владельца не остаётся — снимок
  // очищается и перечитывается для текущего (M2). Идущие запросы прежнего владельца
  // их же сверкой владельца на записи отбрасываются.
  if (_loaded && _ownerId !== uid) {
    _tree = null;
    _catalogs.clear();
    _page = null;
    // Настройки — тоже вики владельца: снимок прежнего не переживает смену (M2)
    _settings = null;
    _loaded = false;
    _ownerId = null;
    // Идущую загрузку прежнего владельца не подхватываем: она отбрасывается своей
    // сверкой владельца на записе — начинаем свою.
    _treeLoading = null;
  }
  if (_loaded) return;
  if (!_treeLoading) _treeLoading = loadWikiTree().finally(() => { _treeLoading = null; });
  await _treeLoading;
}

// ─── Операции (человек — участник, не только читатель) ───

// «Завести вики»: section — необязательный адресат кнопки (ключ раздела проекта), по нему
// бэк проверяет способность (для локального проекта — 409 wiki_section_off). Сам раздел не
// заводится: его заводит первая применённая операция.
export async function initializeWiki(section?: string | null): Promise<WikiInitResult> {
  const res = await wikiHttp<WikiInitResult>('/wiki/initialize', {
    method: 'POST',
    body: JSON.stringify({ section: section ?? null }),
  });
  await refreshWiki();
  return res;
}

// «Принять как есть» для страницы раздела: 409 wiki_refused/wiki_drift — по коду WikiError.
// После принятого перечитываем и саму страницу (ревью S5b M2): файл не меняется, но
// сверка с манифестом уже даёт drift=false — витрина снимает плашку по ответу бэка,
// а не по локальному флагу «принято». Сбой перечитки не отменяет принятое: она
// не роняет ответ и не затирает прежний снимок (тот же порядок, что в refreshWiki).
// Перечитка — только пока страница остаётся открытой у человека (счётчик _openPages,
// не содержимое стора): если за POST он закрыл страницу и открыл другую, безусловная
// перечитка подняла бы поколение слота страниц и отбросила ответ новой страницы как
// устаревшую — она бы висела на спиннере (хвост W4-1).
export async function adoptWikiPage(sectionKey: string, pageKey: string): Promise<WikiAdoptResult> {
  const res = await wikiHttp<WikiAdoptResult>('/wiki/adopt', {
    method: 'POST',
    body: JSON.stringify({ section: sectionKey, page: pageKey }),
  });
  if ((_openPages.get(pageCacheKey(sectionKey, pageKey)) ?? 0) > 0) {
    await loadWikiPage(sectionKey, pageKey).catch(() => undefined);
  }
  await refreshWiki();
  return res;
}

// Удалить раздел-сироту (кнопкой). Не-сирота — 409 wiki_not_orphan; состояние не трогаем,
// пока бэк не подтвердил удаление.
export async function deleteWikiSection(sectionKey: string): Promise<WikiDeleteResult> {
  const res = await wikiHttp<WikiDeleteResult>(`/wiki/section?${sectionQuery(sectionKey)}`, {
    method: 'DELETE',
  });
  // Поднимаем поколение ключа: идущий ответ каталога удалённого раздела его не вернёт (M1)
  _catalogGen.set(sectionKey, (_catalogGen.get(sectionKey) ?? 0) + 1);
  _catalogs.delete(sectionKey);
  if (_page && _page.cacheKey.startsWith(`${sectionKey}\n`)) _page = null;
  await refreshWiki();
  return res;
}

// ─── «Входящие»: пакеты и решение человека (S7a; контракт S4g) ───
// GET /api/wiki/packages?inbox=true → {packages, invalidCount}: только proposed, чужие
// разделы отфильтрованы бэком; нечитаемые файлы — числом, они не притворяются пакетами.
// decide/accept-all → {package, apply}: карточка ПОСЛЕ решения и, если решение оставило
// ноль нерешённых спорных и хотя бы одну принятую, — исход применения этого же запроса.
// apply = null ровно когда применять нечего (пакет остался proposed или rejected).
// Отдельной ручки «применить» нет: повторного применения человеком не существует (S4g).

export interface WikiPackageSourceDto { origin: string; ref: string }

// Операции — в порядке индексов, неизменяемы после создания: index и есть адрес операции.
// decision: auto | inbox; reason — почему спорная (для inbox всегда есть, форма пакета);
// humanDecision: accepted | rejected | null (не решено). text — НАМЕРЕНИЕ (что добавить/
// заместить), target — что замещается/оспаривается или цель ссылки; baseHashes — хэши
// базового состояния, не тексты. Отрендеренного результата в DTO нет (S4g).
export interface WikiPackageOperationDto {
  index: number;
  op: string;
  page: string;
  target: string | null;
  text: string | null;
  class: string;
  decision: string;
  reason: string | null;
  baseHashes: Record<string, string>;
  inputTokens: number | null;
  outputTokens: number | null;
  humanDecision: string | null;
  humanReason: string | null;
  // Доказательства операции (null у старых): утверждение своими словами, 1–3 дословные
  // цитаты, вид страницы (decision/rule/term/state) и «на дату» для state — строкой как есть
  claim: string | null;
  quotes: string[] | null;
  kind: string | null;
  asOf: string | null;
  // ─── Облачный судья (J-J2a/J-J3a): вердикт по спорной операции ───
  // Машинный код причины спора: допуск к судье решает код, а не текст reason (И4′).
  // Пакеты без поля — null: к судье не допускаются (WikiPackage.cs:250).
  reasonCode?: string | null;
  // Вердикт судьи — те же значения, что у решения человека; null/нет — решения нет.
  // Хранится ОТДЕЛЬНО от humanDecision: у спорной операции ровно одно решение — человека
  // или судьи (И1); на humanDecision стоят обучение классов и журнал «правка человека»,
  // решение модели туда не пишется (WikiPackage.cs:261-267).
  judgeDecision?: 'accepted' | 'rejected' | null;
  // Почему судья отказал (обязателен для rejected) (WikiPackage.cs:270)
  judgeReason?: string | null;
  // Что вынесло вердикт: имя модели или 'code' — «отклонено кодом, без вызова модели»
  // (И4′); ставит код, не модель (WikiPackage.cs:273)
  judgeModel?: string | null;
  // Время вердикта — время сервера в момент записи, ISO 8601 (WikiPackage.cs:276)
  judgeAt?: string | null;
  // Стоимость вердикта в долларах; null — вердикта нет (WikiPackage.cs:278)
  judgeCostUsd?: number | null;
  // Сколько раз судья пробовал по этой операции без вердикта (счётчик попыток, J-J3a);
  // вердикт его не двигает (WikiPackage.cs:281)
  judgeAttempts?: number;
}

export interface WikiPackageDto {
  id: string;
  section: string;
  status: string;
  statusReason: string | null;
  createdAt: string;
  updatedAt: string;
  sources: WikiPackageSourceDto[];
  operations: WikiPackageOperationDto[];
}

// applied=true — страницы записаны; отказ — код (wiki_apply_*) и причина человеческим
// текстом; violations — полный перечень для провала гейта. Решения человека при отказе
// сохранены в package.operations — их не надо вводить заново.
export interface WikiApplyOutcomeDto {
  applied: boolean;
  code: string | null;
  reason: string | null;
  violations: string[];
}

export interface WikiDecisionDto { package: WikiPackageDto; apply: WikiApplyOutcomeDto | null }

export interface WikiPackagesDto { packages: WikiPackageDto[]; invalidCount: number }

export async function listWikiPackages(): Promise<WikiPackagesDto> {
  return wikiHttp<WikiPackagesDto>('/wiki/packages?inbox=true');
}

// Решение по одной спорной операции. accept=false требует reason (409 wiki_pkg_reason_required).
// Успех — {package, apply}; после применённого перечитываем дерево и каталог: страницы
// изменились, витрина не должна показывать прежний каталог (тот же порядок, что у adopt).
export async function decideWikiPackage(
  id: string, index: number, accept: boolean, reason: string | null,
): Promise<WikiDecisionDto> {
  const res = await wikiHttp<WikiDecisionDto>(`/wiki/packages/${encodeURIComponent(id)}/decide`, {
    method: 'POST',
    body: JSON.stringify({ index, accept, reason: reason ?? null }),
  });
  if (res.apply?.applied) await refreshWiki();
  return res;
}

export async function acceptAllWikiPackage(id: string): Promise<WikiDecisionDto> {
  const res = await wikiHttp<WikiDecisionDto>(`/wiki/packages/${encodeURIComponent(id)}/accept-all`, {
    method: 'POST',
  });
  if (res.apply?.applied) await refreshWiki();
  return res;
}

// ─── «Журнал»: чтение log.jsonl со склейкой по пакетам (S7c-2; GET /api/wiki/journal, S7c-0) ───
// Записи — события журнала (apply/revert/retract/relink/adopt), «новые сверху»: строки log — единственные
// носители событий, их порядок и есть хронология. status/policy/classes — из склейки с пакетом: status
// null, когда пакетов несколько (их id в packages), classes пуст, когда пакета нет (relink, adopt).
// sourceName — имя чата этого владельца или заголовок заметки; чужой или несуществующий чат — без имени
// (по ответу нельзя отличить «нет» от «не твой»). Окно — последние `take` строк (по умолчанию 200,
// потолок 1000; фактическое окно — в ответе); фильтры сужают прочитанный хвост, а не ищут за его краем.
// Нечитаемые строки отдаются числом (invalidLines): их видно как сломанные, они не притворяются записями.

export interface WikiJournalEntryDto {
  ts: string;
  op: string;
  section: string;
  actor: string;
  source: string | null;
  origin: string | null;
  sourceName: string | null;
  pages: string[];
  packages: string[];
  status: string | null;
  policy: string | null;
  classes: string[];
}

// Записи несут признак «ИИ поменял моё» (F3b, ADR-038 предохранитель 4): overrodeHuman и
// вид действия overrodeKind — строка журнала их читает, бэк отдаёт как есть, фронт не
// выводит. Пары statement/previous здесь нет: она только в overrides[] страницы.
export interface WikiJournalDto { entries: WikiJournalEntryOverrideDto[]; take: number; linesRead: number; invalidLines: number }

// Фильтры окна: section — ключ из дерева своей вики, origin — значение из закрытого перечня
// (тот же, что фильтр источников экрана), class — пара «origin × op». Пустое — без фильтра.
// overrodeHuman — «ИИ поменял моё» (F6b): бэк сужает окно до ответа (WikiJournalReader.cs),
// фронт по списку не фильтрует. take не передаём: экран живёт в окне по умолчанию (200),
// потолок (1000) — дело бэка.
export interface WikiJournalQuery {
  section?: string | null; origin?: string | null; class?: string | null;
  overrodeHuman?: boolean;
}

export async function loadWikiJournal(q: WikiJournalQuery = {}): Promise<WikiJournalDto> {
  const params = new URLSearchParams();
  if (q.section) params.set('section', q.section);
  if (q.origin) params.set('origin', q.origin);
  if (q.class) params.set('class', q.class);
  // 'true' — единственное значение фильтра: false не шлём вовсе (бэк читает отсутствие как «нет»)
  if (q.overrodeHuman) params.set('overrodeHuman', 'true');
  const qs = params.toString();
  return wikiHttp<WikiJournalDto>(`/wiki/journal${qs ? `?${qs}` : ''}`);
}

// ─── «Решения за ночь»: вердикты судьи по окну (J-F0; GET /api/wiki/journal?view=night, J-J5) ───
// Другой вид той же журнальной ручки: не строки log, а операции пакетов с вердиктом облачного
// судья по окну [from, to) — плюс очередь того, что ещё не решил ни человек, ни судья (без
// фильтра по времени: она про «сейчас», а не про «за ночь»). Судейские группы — хронологические,
// waiting — по давности пакета; пустая группа — пустой список, пустое окно — не отказ
// (WikiController.cs:597-632). from/to — обязательные даты ISO-8601 (тот же формат, что ts
// строк журнала): нет даты / не дата / from позже to — 400 wiki_date_range, чужой вид —
// wiki_view_key; прочие параметры вида бэк игнорирует (сводка — по всей вике, не по разделу).
// Чистый транспорт (тот же порядок, что loadWikiJournal): состояния стора не трогает.

// Одна операция с вердиктом (WikiController.cs:923-926): что и где предлагалось (packageId/
// page/claim), исходная причина спора (reason — текст; reasonCode — машинный код, по нему
// решался допуск к судье) и сам вердикт. У waiting вердикта нет вовсе — judge*-поля null;
// judgeModel === 'code' — отказ вынесен кодом, без вызова модели (И4′).
export interface WikiNightDecisionDto {
  packageId: string;
  page: string;
  claim: string | null;
  reasonCode: string | null;
  judgeDecision: 'accepted' | 'rejected' | null;
  judgeReason: string | null;
  // Время вердикта, ISO 8601
  judgeAt: string | null;
  judgeCostUsd: number | null;
  judgeModel: string | null;
  reason: string | null;
  // Текущее состояние пакета (WikiPackage.StatusValues — обязательное, не nullable): вердикт
  // судьи и судьба пакета — разные вещи, принятый судьёй пакет мог не примениться
  // (apply_failed) или быть откатан человеком (reverted); «Откатить пакет» — только у applied
  // (RowOf, WikiJournalReader.cs:328-330).
  packageStatus: string;
}

export interface WikiNightDecisionsDto {
  judgeAccepted: WikiNightDecisionDto[];
  judgeRejected: WikiNightDecisionDto[];
  // Отклонено кодом без вызова модели — не вердикт судьи (И4′)
  codeRejected: WikiNightDecisionDto[];
  // Ещё не решил ни человек, ни судья
  waiting: WikiNightDecisionDto[];
}

// from/to — даты окна [from, to) в ISO-8601 (YYYY-MM-DD): их валидирует бэк.
export async function loadWikiNightDecisions(from: string, to: string): Promise<WikiNightDecisionsDto> {
  return wikiHttp<WikiNightDecisionsDto>(
    `/wiki/journal?view=night&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
}

export interface WikiRevertDto { restoredPages: string[] }

// Исход отката по источнику (мера 5): revertedPackages — откатанные по прообразам;
// unlinkedPackages — обработанные снятием сносок; remainingPackages — «остались» (есть другой
// источник); removedChronicles — снятые целиком хроники; failed — названные проблемы
// частичного исхода; markedSkip — чату поставлена пометка «не разбирать».
export interface WikiRetractDto {
  revertedPackages: string[];
  unlinkedPackages: string[];
  remainingPackages: string[];
  removedChronicles: string[];
  failed: string[];
  markedSkip: boolean;
}

// Отказ отката пакета несёт в теле перечень страниц: wiki_revert_changed — страницы,
// изменённые после применения, то, на что человеку смотреть перед повторной попыткой
// (контракт S4b-3). У обычного WikiError этого перечня нет — он здесь.
export class WikiRevertError extends WikiError {
  readonly pages: string[];

  constructor(status: number, code: WikiErrorCode | null, message: string, pages: string[]) {
    super(status, code, message);
    this.name = 'WikiRevertError';
    this.pages = pages;
  }
}

// «Откатить пакет» (мера 4): только действие человека. Отказ — WikiRevertError с перечнем
// изменённых страниц. Успех — перечитываем дерево и открытые каталоги и открытую страницу
// (порядок adopt): страницы возвращены к состоянию до применения, витрина под открытым
// экраном — после отката, а не до него.
export async function revertWikiPackage(id: string): Promise<WikiRevertDto> {
  try {
    const res = await request<WikiRevertDto>(`/wiki/packages/${encodeURIComponent(id)}/revert`, {
      method: 'POST',
    });
    await reReadOpenPage();
    await refreshWiki();
    return res;
  } catch (e) {
    const we = toWikiError(e);
    if (!we) throw e;
    const body = (e as { body?: { pages?: unknown } }).body;
    const pages = Array.isArray(body?.pages)
      ? (body.pages as unknown[]).filter((p): p is string => typeof p === 'string')
      : [];
    throw new WikiRevertError(we.status, we.code, we.message, pages);
  }
}

// «Откатить всё из этого источника» (мера 5): sourceRef — форма «{origin}:{id}» (тот же
// разбор, что у бэкового OriginOf/IdPartOf): карточка без диапазона окна — бэк по ней
// накрывает все пакеты источника в любой части диапазона.
// Успех — перечитываем открытую страницу, страницы и каталоги: они изменились снятием/откатом.
export async function retractWikiSource(sourceRef: string): Promise<WikiRetractDto> {
  const res = await wikiHttp<WikiRetractDto>('/wiki/packages/retract', {
    method: 'POST',
    body: JSON.stringify({ sourceRef }),
  });
  await reReadOpenPage();
  await refreshWiki();
  return res;
}

// ─── «Здоровье»: отчёт последней проверки и запуск линта (S5f; контракт S3-3) ───
// GET /api/wiki/health/{section} отдаёт _health.md раздела КАК ЕСТЬ: своего DTO у бэка нет
// (WikiController.cs:261-288), строки находок пишет бэковый RenderHealth (WikiLintService.cs:434),
// и разбор на фронте — зеркало той же формы (WikiHealth.tsx). Придумывать за бэка структурированный
// ответ нельзя: content — строка файла, недоверенный текст.
// 404 — отчёта нет вовсе: раздел ещё не проверялся, его нет на диске или при выключенном llm-wiki
// свежих отчётов не пишется. Это «нет данных», а не «здорово».
// POST /api/wiki/lint — прогон по ВСЕМ разделам своей вики: своей ручки на один раздел нет, а
// параллельный прогон того же владельца бэк отказывает 409 — чтобы не было двух записей
// _health.md и метрик подряд (WikiController.cs:292-307). Метрики прогона (keeper/metrics.jsonl)
// отдельной ручкой не читаются — их здесь нет, и выдумывать их из health-файла нельзя.

export interface WikiHealthDto { section: string; content: string }
export interface WikiLintRunDto { ran: boolean; sectionsOk: number; sectionsFailed: number }

// Ключ раздела в health — путь URL (health/{**section}, catch-all): «/» остаётся разделителем,
// кодируются сегменты — «projects/p1» не должен превратиться в %2F
const healthPath = (key: string) => key.split('/').map(encodeURIComponent).join('/');

export async function getWikiHealth(sectionKey: string): Promise<WikiHealthDto> {
  return wikiHttp<WikiHealthDto>(`/wiki/health/${healthPath(sectionKey)}`);
}

// Запуск проверки. 409 wiki_llm_wiki_off / wiki_lint_running — WikiError по коду;
// состояния экрана (текст отказа, «повторить») — за вызывающим.
export async function runWikiLint(): Promise<WikiLintRunDto> {
  return wikiHttp<WikiLintRunDto>('/wiki/lint', { method: 'POST' });
}

// ─── Разбор по требованию: чат и заметка (S7b-0; контракт S7b0a/S7b0b) ───
// Эти функции — чистый транспорт: состояния стора не трогают, ничего не перечитывают —
// опрос, гард поколений и состояния UI за вызывающим (S7b). Бэкенд: analyze ждёт исход
// сам (≤10 с, WikiChatsController.cs:172); не дождался — 202 {running:true}, разбор
// продолжается на стороне сервиса.

// Исходы разбора чата (WikiChatIngestService.cs:81-85). 'rejected' на 200 — отказ без
// машинного кода: кодовые отказы уходят 404/409 и в этот результат не попадают
// (MapAnalysis, WikiChatsController.cs:195-204). Неизвестный исход — будущее значение
// контракта: проходит как есть, ветку default держит вызывающий — придумывать значение
// здесь нечем.
export type WikiChatAnalyzeOutcome = 'applied' | 'awaiting_human' | 'nothing_new' | 'up_to_date' | 'rejected';

export interface WikiChatAnalyzeOk {
  outcome: WikiChatAnalyzeOutcome;
  reason: string | null;
  packageIds: string[];
}

// 202 {running:true}: заявка не отменена, её исход — через loadWikiChatAnalyzeStatus.
// Отдельный результат, а не Ok с пустыми полями.
export interface WikiChatAnalyzeRunning { running: true }

export type WikiChatAnalyzeResult = WikiChatAnalyzeOk | WikiChatAnalyzeRunning;

// Последний исход разбора чата (analyze-status → GetLast, WikiChatsController.cs:206-233).
// finishedAt бэкенд ставит всегда; null — только оборонительный разбор сломанного поля.
export interface WikiAnalyzeLastDto {
  outcome: WikiChatAnalyzeOutcome;
  reason: string | null;
  packageIds: string[];
  finishedAt: string | null;
}

export interface WikiChatAnalyzeStatusDto { running: boolean; last: WikiAnalyzeLastDto | null }

// Заявленная форма результата должна дойти до вызывающего как объявлена: null вместо
// отсутствующего поля, [] вместо undefined. null — когда тело не подходит под форму 200
// вовсе (нет строкового outcome).
function analyzeOk(body: unknown): WikiChatAnalyzeOk | null {
  const b = body as { outcome?: unknown; reason?: unknown; packageIds?: unknown } | null;
  if (typeof b?.outcome !== 'string') return null;
  return {
    outcome: b.outcome as WikiChatAnalyzeOutcome,
    reason: typeof b.reason === 'string' ? b.reason : null,
    packageIds: Array.isArray(b.packageIds)
      ? b.packageIds.filter((p): p is string => typeof p === 'string')
      : [],
  };
}

// «Разобрать этот чат сейчас» (S7b0a): единственный запрос, доходящий до модели. 200 и 202 —
// для request() оба успешные тела (offline.ts:1649), различаем по форме: у 200 всегда есть
// строковый outcome, у 202 — только running:true. Отказы: 404 not_found, 409 task_execution |
// marked_skip | personal_section — WikiError с кодом. С LI-2 analyze сам заводит вики владельца
// (WikiChatsController.cs:276): 409 wiki_not_initialized из этой ручки больше не приходит;
// код остаётся в типах — это ответ сервисного слоя (гейт WikiChatIngestService.cs:346, MCP).
export async function analyzeWikiChat(sessionId: string): Promise<WikiChatAnalyzeResult> {
  const body = await wikiHttp<unknown>(
    `/wiki/chats/${encodeURIComponent(sessionId)}/analyze`, { method: 'POST' });
  if ((body as { running?: unknown } | null)?.running === true) return { running: true };
  const ok = analyzeOk(body);
  // Ни одна из двух форм 200: это не HTTP-отказ (status 0 — не реальный статус, wikiErrorText
  // даст общий «Не удалось выполнить действие») — ответ не подошёл под контракт, вызывающий
  // видит ошибку, а не тихий пустой результат.
  if (!ok) throw new WikiError(0, null, 'неожиданный ответ разбора чата');
  return ok;
}

// Опрос после 202: идёт ли разбор и каков его последний исход. Чужой или несуществующий
// чат — 404 not_found (per-owner, без утечки существования чужого).
export async function loadWikiChatAnalyzeStatus(sessionId: string): Promise<WikiChatAnalyzeStatusDto> {
  // live: true (доработка H2): значение этого ответа — не только содержимое, но и сам факт
  // «сервер сейчас отвечает». Подставленный из IndexedDB прошлый running:false при обрыве
  // сети превращает идущий разбор в завершившийся (тот же смысл, что у статусов в api.ts).
  const res = await wikiHttp<{ running?: unknown; last?: unknown }>(
    `/wiki/chats/${encodeURIComponent(sessionId)}/analyze-status`, { live: true });
  const raw = res?.last as { finishedAt?: unknown } | null;
  const ok = analyzeOk(res?.last);
  return {
    running: res?.running === true,
    last: ok ? { ...ok, finishedAt: typeof raw?.finishedAt === 'string' ? raw.finishedAt : null } : null,
  };
}

// Исходы заявки заметки (WikiIngestService.cs:64-67): их решает очередь разбора, ручка
// только ставит заявку (WikiController.cs:283-314). packageId — id созданного пакета
// ('applied'/'awaiting_human', WikiIngestService.cs:358,371); когда пакета нет — null.
export type WikiNoteIngestOutcome = 'applied' | 'awaiting_human' | 'nothing_new' | 'rejected';

export interface WikiNoteIngestDto {
  outcome: WikiNoteIngestOutcome;
  packageId: string | null;
  reason: string | null;
}

// «Разобрать заметку в базу» (S7b0b): target — явный «common» для источника вне проекта
// (ADR-036, решение 3), его проверяет захват. Отказы: 400 wiki_note_id (нет id),
// 409 packages_limit (до захвата), 404 not_found (заметка не стала источником: чужая, нет
// такой). С LI-2 ручка сама заводит вики владельца (WikiController.cs:474): 409
// wiki_not_initialized из неё больше не приходит; код остаётся в типах — ответ сервисного
// слоя (гейт WikiIngestService.cs:214, MCP).
export async function ingestWikiNote(noteId: string, target?: string | null): Promise<WikiNoteIngestDto> {
  const res = await wikiHttp<{ outcome?: unknown; packageId?: unknown; reason?: unknown }>(
    '/wiki/ingest/note',
    { method: 'POST', body: JSON.stringify({ noteId, target: target ?? null }) },
  );
  if (typeof res?.outcome !== 'string') throw new WikiError(0, null, 'неожиданный ответ заявки заметки');
  return {
    outcome: res.outcome as WikiNoteIngestOutcome,
    packageId: typeof res.packageId === 'string' ? res.packageId : null,
    reason: typeof res.reason === 'string' ? res.reason : null,
  };
}

// ─── Список отбора чатов (S6e/S6f; GET /api/wiki/chats, WikiChatsController.List) ───
// Запись на каждый существующий свой чат: ПОКАЗАННАЯ зона (исход разбора nothing_new
// приоритетен, иначе зона ступени 1 — ShownZone на бэке) с причиной и сигналами. Нужен
// экрану отбора (S7b): кнопка «открыть хронику» — по chroniclePath, статус «идёт» —
// по running. Порядок — бэковый (по зонам, внутри зоны — по баллу), фронт его не меняет.
// Зоны сегодня: 'triage' | 'gray' | 'skipped' | 'excluded' (WikiChatScorer.cs:39-42),
// 'nothing_new' (исход разбора, WikiChatIngestService.cs:114), 'rejected_skipped' (окно,
// пропущенное после 3 отказов, C2R-Sh2 — WikiChatIngestService.cs:118) и 'awaiting_human'
// (пакет во «Входящих», ждущий решения человека, C2R-Sh2b — WikiChatIngestService.cs:122);
// тип — string: неизвестная зона
// будущего шага — запись списка, а не отказ разбора (как на бэке, ZoneRank ведёт её в конец).
// chroniclePath — формула бэка (WikiChatIngestService.ChroniclePath): для проектного дома
// «Проекты/{id}/Хроники/{sessionId}.md», null для не-проектного дома (личный — с Ф2);
// существования файла не обещает — хронику создаёт применённый разбор.
// counts (числа по показанной зоне) — часть ответа бэка; эта функция их не отдаёт
// (не заявлена задачей; при нужности экрана — отдельным пунктом).

export interface WikiTriageSignalDto { name: string; weight: number }

// Пометка человека с чата (N1b, ADR-035 §12а): 'important' — разбирается всегда, минуя ступени
// отбора; 'skip' — никогда (ту же пометку ставит откат по источнику, §12г мера 5). Живёт на
// чате (Session.WikiMark), не в сторе отбора: её ставит setWikiChatMark или откат.
export type WikiChatMark = 'important' | 'skip';

export interface WikiChatTriage {
  sessionId: string;
  name: string | null;
  zone: string;
  score: number | null;
  signals: WikiTriageSignalDto[] | null;
  reason: string | null;
  nothingNewStreak: number;
  triagedAt: string;
  running: boolean;
  cursor: number;
  chroniclePath: string | null;
  // Пометка по записи списка (поле Mark — N1b, WikiChatsController.cs:140-142): отсутствие
  // и null — одно: пометки нет (ответ старого бэка без поля читается как «нет пометки»)
  mark?: WikiChatMark | null;
}

interface WikiChatsListDto { items: WikiChatTriage[]; counts: Record<string, number> }

export async function loadWikiChats(): Promise<WikiChatTriage[]> {
  const res = await wikiHttp<WikiChatsListDto>('/wiki/chats');
  // Неформный 200 — не пустой список: пустой бэк говорит items:[]. Тело, не подходящее
  // под форму, — ответ вне контракта (тот же порядок, что в analyzeWikiChat): вызывающий
  // видит ошибку, а не тихую пустоту экрана отбора. Отказ 409 «файл разбора не читается»
  // доходит сюда как WikiError с кодом wiki_triage_invalid.
  if (!Array.isArray(res?.items)) throw new WikiError(0, null, 'неожиданный ответ списка отбора чатов');
  return res.items;
}

// ─── «Ночной разбор»: настройки, итог ночи и пометки чата (Ф2; контракты NA-2 и N1b) ───
// GET/PUT /api/wiki/settings — та же ручка, что у блока «Применение» (S4c): здесь читается и
// меняется только ночная её часть. Бэк отдаёт ночной блок целым всегда, с подставленными
// умолчаниями: «блока нет» для фронта — то же, что «ночь выключена» (WikiSettingsController.cs:
// 212-223). PUT принимает частичный nightly (мердж с состоянием на момент записи, :134-156) и
// возвращает настройки целиком; lastNight появится в GET с N7c — до него поля нет вовсе, и это
// то же «итога не было» (форма — по плану N7b: файл keeper/nightly-last.json; финальную сверку
// с N7c сделаем после его сдачи).
//
// Конкурентность (план Ф1): слот настроек один, со своим счётчиком поколения — тот же инвариант
// «последний запрос побеждает», что у дерева и каталогов (ревью S5a M1), и та же привязка к
// владельцу (M2). Правка ставится в стор оптимистично (тумблер не ждёт ответа), ответ подтверждает
// её серверным целым; отказ PUT откатывает оптимистичное. Отказ контракта — WikiError с кодом —
// возвращается результатом, а не исключением: состояние стора к тому моменту уже согласован
// (откатан), и вызывающему остаётся назвать причину. Сетевой сбой/таймаут — не отказ контракта
// (та же позиция, что у toWikiError): откат тот же, исключение идёт наружу как есть.

export interface WikiNightlySettings {
  enabled: boolean;
  windowStartHour: number;
  windowEndHour: number;
  maxPackagesPerNight: number;
  maxPackagesPerSection: number;
  maxChatsPerSection: number;
  inputTokenBudget: number;
}

// Исходы прохода ночи (план N7b, поле outcome файла). Неизвестный исход — будущее значение
// контракта: проходит как есть (тот же приём, что у WikiChatAnalyzeOutcome).
export type WikiNightlyOutcome = 'completed' | 'budget' | 'open_limit' | 'window_closed' | 'disabled_midway' | 'error';

export interface WikiLastNight {
  date: string;
  selected: number;
  analyzed: number;
  applied: number;
  toInbox: number;
  estTokens: number;
  outcome: WikiNightlyOutcome;
  // Отвергнутые пакеты и отметка ручного прохода (WN-2/WN-5: Rejected/Manual в итоге ночи,
  // сериализация — WikiSettingsController.cs:319-321, 358-362). Поля опциональны: старый бэк
  // без них — не ошибка, отсутствие читается как есть (тот же приём, что отсутствующий lastNight)
  rejected?: number;
  manual?: boolean;
  // Итог облачного судья этой ночи (J-J4): счётчики — по операциям, не по пакетам;
  // judgeCostUsd — стоимость его вызовов (единственные деньги в итоге). codeRejected —
  // отказано кодом без вызова модели (И4′), не вердикт судьи. В ответе бэка поля всегда:
  // старый keeper/nightly-last.json без них читается нулями на бэке (аддитивность,
  // WikiSettingsController.cs:377-385) — «поле отсутствует → 0» обеспечивает бэк, поэтому
  // здесь поля обязательны, как прочие числа итога.
  judgeCalls: number;
  judgeCostUsd: number;
  judgeApplied: number;
  judgeRejected: number;
  judgeDeferred: number;
  codeRejected: number;
}

// Класс правок «origin×op» (ключ — форма бэкового WikiPackage.ClassKey, без пробелов) с
// состоянием политики и замером оценки: Applied{N}/Retracted{N} — числа, Share{N} — уже
// процент (WikiClassHealth.ClassStat: «доля, процент»). Замер несёт только GET-оценка;
// PUT возвращает классы без него (нулями). reason — текст выключения человеческим языком
// (автовыключение пишет оценка, ручное — «класс выключен человеком», WikiSettingsController.cs:139-143).
export interface WikiApplyClassDto {
  class: string;
  disabled: boolean;
  reason: string | null;
  disabledAt: string | null;
  resumedAt: string | null;
  applied7: number;
  retracted7: number;
  share7: number;
  applied30: number;
  retracted30: number;
  share30: number;
}

// Снимок настроек целиком: блок «Применение» (S7d-1) и блок «Ночной разбор» (Ф2) одной ручки.
export interface WikiSettingsSnapshot {
  autoApplyEnabled: boolean;
  classes: WikiApplyClassDto[];
  nightly: WikiNightlySettings;
  lastNight: WikiLastNight | null;
  // Идёт ли сейчас ночной проход этого владельца (WN-5: NightlyRunning в GET,
  // WikiSettingsController.cs:267-269, 339). Состояние процесса, не файла: PUT его не несёт
  // («никакой записи настроек действие не делает», :235) и сохраняет прочитанное, как lastNight;
  // ручной запуск ставит его оптимистично и снимает по ответу опроса (WN-6a)
  nightlyRunning: boolean;
}

let _settings: WikiSettingsSnapshot | null = null;
let _settingsGen = 0;

// Ночной блок ответа — как есть (бэк сериализует его целым всегда): неформный 200 — не тихие
// умолчания, а ответ вне контракта (тот же порядок, что у loadWikiChats). lastNight: null и
// отсутствие поля — одно «итога не было»; на бэке битый файл уже null (N7c).
function readNightly(body: unknown): WikiNightlySettings | null {
  const b = body as { nightly?: unknown } | null;
  return b && typeof b.nightly === 'object' && b.nightly !== null
    ? b.nightly as WikiNightlySettings
    : null;
}

function readLastNight(body: unknown): WikiLastNight | null {
  const b = body as { lastNight?: unknown } | null;
  return typeof b?.lastNight === 'object' && b.lastNight !== null
    ? b.lastNight as WikiLastNight
    : null;
}

// NightlyRunning (WN-5): бэк несёт его в GET всегда; старый бэк без поля — «не идёт»
// (отсутствие поля — отрицательное значение, не ошибка: тот же приём, что отсутствующий lastNight)
const readNightlyRunning = (body: unknown): boolean =>
  (body as { nightlyRunning?: unknown } | null)?.nightlyRunning === true;

const numOr0 = (v: unknown): number => (typeof v === 'number' && Number.isFinite(v) ? v : 0);
const strOrNull = (v: unknown): string | null => (typeof v === 'string' ? v : null);

function readApplyClass(raw: unknown): WikiApplyClassDto | null {
  const c = raw as Record<string, unknown> | null;
  if (typeof c?.class !== 'string' || !c.class) return null;
  return {
    class: c.class,
    disabled: c.disabled === true,
    reason: strOrNull(c.reason),
    disabledAt: strOrNull(c.disabledAt),
    resumedAt: strOrNull(c.resumedAt),
    applied7: numOr0(c.applied7), retracted7: numOr0(c.retracted7), share7: numOr0(c.share7),
    applied30: numOr0(c.applied30), retracted30: numOr0(c.retracted30), share30: numOr0(c.share30),
  };
}

// Часть «Применение» ответа. Текущий бэк сериализует поля всегда (WikiSettingsDto), но
// отсутствующее поле читается как «выключено»/«нет классов» — тот же приём, что отсутствующий
// lastNight в Ф1 («итога не было»): старый бэк без полей не должен становиться ошибкой
// всех настроек, а выключенное направление — безопасное (ничего само не применяется).
function readApplyPart(body: unknown): Pick<WikiSettingsSnapshot, 'autoApplyEnabled' | 'classes'> {
  const b = body as { autoApplyEnabled?: unknown; classes?: unknown } | null;
  const classes = Array.isArray(b?.classes)
    ? b.classes.map(readApplyClass).filter((c): c is WikiApplyClassDto => c !== null)
    : [];
  return { autoApplyEnabled: b?.autoApplyEnabled === true, classes };
}

// Ответ PUT несёт состояния классов БЕЗ замера («Замер не возвращается: он читается GET после
// действия», WikiSettingsController.cs:94) и НЕ все классы: его класс-часть строится из
// settings.Classes, где запись есть только у когда-либо выключавшегося класса
// (BuildDto(next, [], null), :185, :206–214), тогда как GET отдаёт union записей и замера
// оценки (:215–222). Поэтому множество классов снимка — union ответа и последнего чтения:
// строки ответа приоритетны для классов, которых PUT касается (без class классы не трогает,
// :130–145), строки прежнего чтения — активные без записи — сохраняются как есть. Иначе правка,
// не касающаяся классов (смена окна ночи), оставила бы блок «Применение» без активных классов
// до следующей перечитки. Экран не должен терять и измеренное: доли того же класса сохраняются
// от последнего чтения — следующие их обновит перечитка GET. Порядок — по ключу класса, как его
// отдаёт бэк (OrderBy Ordinal, :226): между ответом и перечиткой список не переставляется.
function mergeClassStats(
  next: WikiApplyClassDto[], prev: readonly WikiApplyClassDto[] | undefined,
): WikiApplyClassDto[] {
  if (!prev?.length) return next;
  const byKey = new Map<string, WikiApplyClassDto>(prev.map(c => [c.class, c]));
  for (const c of next) {
    const p = byKey.get(c.class);
    byKey.set(c.class, p
      ? { ...c, applied7: p.applied7, retracted7: p.retracted7, share7: p.share7, applied30: p.applied30, retracted30: p.retracted30, share30: p.share30 }
      : c);
  }
  return [...byKey.values()].sort((a, b) => (a.class < b.class ? -1 : a.class > b.class ? 1 : 0));
}

// Висящий GET настроек: два блока вкладки зовут загрузку при монтировании — второй вызов
// делит запрос первого (иначе отказ более нового GET отбрасывал бы ответ первого как устаревший
// и блок оставался на скелетоне без ошибки). PUT поднимает поколение — висящий слот после него
// не переиспользуется: чтение, начатое до правки, не должно затереть её.
let _settingsReq: { gen: number; owner: string | null; promise: Promise<WikiSettingsSnapshot> } | null = null;

export function loadWikiSettings(): Promise<WikiSettingsSnapshot> {
  const owner = readStoredUserId();
  if (_settingsReq && _settingsReq.gen === _settingsGen && _settingsReq.owner === owner) {
    return _settingsReq.promise;
  }
  const gen = ++_settingsGen;
  const req = {
    gen, owner,
    promise: fetchWikiSettings(gen, owner).finally(() => {
      if (_settingsReq === req) _settingsReq = null;
    }),
  };
  _settingsReq = req;
  return req.promise;
}

async function fetchWikiSettings(gen: number, owner: string | null): Promise<WikiSettingsSnapshot> {
  const body = await wikiHttp<unknown>('/wiki/settings');
  const nightly = readNightly(body);
  if (!nightly) throw new WikiError(0, null, 'неожиданный ответ настроек вики');
  const snap: WikiSettingsSnapshot = { ...readApplyPart(body), nightly, lastNight: readLastNight(body), nightlyRunning: readNightlyRunning(body) };
  // Устаревший ответ или ответ чужого владельца в снимок не пишется (M1/M2)
  if (gen === _settingsGen && owner === readStoredUserId()) {
    _settings = snap;
    emit();
  }
  return snap;
}

export type WikiNightlyPatch = Partial<WikiNightlySettings>;

export type WikiPutNightlyResult =
  | { ok: true; snapshot: WikiSettingsSnapshot }
  | { ok: false; error: WikiError };

// «Изменить настройки ночи»: PUT с частичным блоком — только изменённые поля, без null-полей
// («не менять» на бэке — отсутствие поля; null здесь значил бы «ставить null»).
export async function putWikiNightly(patch: WikiNightlyPatch): Promise<WikiPutNightlyResult> {
  const gen = ++_settingsGen;
  const owner = readStoredUserId();
  const prev = _settings;
  // Оптимистично: новое значение видно сразу, ответ подтвердит или откатит. Снимка ещё нет
  // (экран не прочитал настройки) — оптимистичного нет, ждём ответ.
  if (prev) {
    _settings = { ...prev, nightly: { ...prev.nightly, ...patch } };
    emit();
  }
  let body: unknown;
  try {
    body = await wikiHttp<unknown>('/wiki/settings', {
      method: 'PUT',
      body: JSON.stringify({ nightly: patch }),
    });
  } catch (e) {
    // Сервер не принял изменение — оптимистичное откатывается при любом отказе; чужая
    // правка, начатая позже этой, не затирается (её поколение — последнее). Откат прежнего
    // владельца в чужой стор не возвращается: при смене аккаунта снимок уже очищен (M2).
    if (gen === _settingsGen && owner === readStoredUserId()) {
      _settings = prev;
      emit();
    }
    const we = toWikiError(e);
    if (!we) throw e; // сетевой сбой/таймаут — не отказ контракта: наружу как есть
    return { ok: false, error: we };
  }
  const nightly = readNightly(body);
  if (!nightly) {
    if (gen === _settingsGen && owner === readStoredUserId()) {
      _settings = prev;
      emit();
    }
    return { ok: false, error: new WikiError(0, null, 'неожиданный ответ настроек вики') };
  }
  const apply = readApplyPart(body);
  const snap: WikiSettingsSnapshot = {
    ...apply,
    // PUT не несёт замера классов (шапка mergeClassStats): ночная правка не должна обнулять
    // доли блока «Применение» — они доживают до следующей перечитки GET
    classes: mergeClassStats(apply.classes, prev?.classes),
    nightly,
    // lastNight — только из GET (N7c): ответ PUT его не несёт, и молча обнулять прочитанное
    // нечем — поле сохраняется от последнего чтения
    lastNight: readLastNight(body) ?? prev?.lastNight ?? null,
    // то же с состоянием прогона (WN-5): PUT его не несёт — сохраняется от последнего чтения
    // или оптимистичной записи ручного запуска, а не обнуляется
    nightlyRunning: prev?.nightlyRunning ?? false,
  };
  // Устаревший ответ или ответ прежнего владельца в снимок не пишется (M1/M2);
  // вызывающий свой ответ получает как есть.
  if (gen === _settingsGen && owner === readStoredUserId()) {
    _settings = snap;
    emit();
  }
  return { ok: true, snapshot: snap };
}

// ─── «Разобрать сейчас»: ручной запуск прохода и опрос до его конца (WN-6a) ───
// POST /api/wiki/settings/nightly/run (WikiSettingsController.cs:237-251): 202 {started:true} —
// проход начат; 409 — по причине отказа (wiki_llm_wiki_off / wiki_nightly_disabled /
// wiki_nightly_running / wiki_settings_invalid). Результат, а не исключение (образец
// putWikiNightly): вызывающий называет причину по `code`; сетевой сбой — {ok:false, code:null}
// (это не отказ контракта, но и кнопки не обёртываются в try/catch — позиция та же, что у
// toWikiError). 409 «уже идёт» — то же состояние, что 202: проход идёт, его и опрашиваем.
//
// О конце прохода бэк не сообщает (202 — только начало, работа в фоне службы): фронт опрашивает
// GET /wiki/settings раз в 5 с, пока тот отвечает nightlyRunning=true; последний ответ — он и
// финальный снимок со свежим lastNight. До первого ответа «идёт» показывает оптимистичная
// запись в существующий снимок; снимка ещё нет — состояние принесёт первый опрос.

export type WikiRunNightlyResult =
  | { ok: true }
  | { ok: false; code: WikiErrorCode | null };

const NIGHTLY_POLL_MS = 5000;

// Полёт запуска: второй вызов, пока первый в сети, делит его результат — ровно один POST
// (тот же слот-приём, что у loadWikiSettings; замок бэка второй раз не проверяется).
// Опрос — один на стор: цепочка одного таймера, привязанная к владельцу, начавшему запуск;
// повторный старт (в т.ч. по 409 «уже идёт») второй цепочки не заводит.
let _nightlyRunReq: Promise<WikiRunNightlyResult> | null = null;
let _nightlyPoll: { owner: string | null; timer: ReturnType<typeof setTimeout> | null } | null = null;

export function runWikiNightlyNow(): Promise<WikiRunNightlyResult> {
  if (_nightlyRunReq) return _nightlyRunReq;
  const req = doRunWikiNightly().finally(() => { if (_nightlyRunReq === req) _nightlyRunReq = null; });
  _nightlyRunReq = req;
  return req;
}

async function doRunWikiNightly(): Promise<WikiRunNightlyResult> {
  const owner = readStoredUserId();
  try {
    await wikiHttp<unknown>('/wiki/settings/nightly/run', { method: 'POST' });
  } catch (e) {
    const we = toWikiError(e);
    if (we?.code !== 'wiki_nightly_running') return { ok: false, code: we?.code ?? null };
    // «разбор этой вики уже идёт» — не отказ действия: состояние то же, что после 202
  }
  if (owner !== readStoredUserId()) return { ok: true }; // смена аккаунта в полёте: проход её, стор уже чужой (M2)
  ++_settingsGen; // чтения, начатые до запуска, устарели: их ответы не затерят состояние прохода
  if (_settings) {
    _settings = { ..._settings, nightlyRunning: true }; // оптимистично, как тумблер ночи
    emit();
  }
  startNightlyPolling(owner);
  return { ok: true };
}

function startNightlyPolling(owner: string | null): void {
  if (_nightlyPoll) return;
  _nightlyPoll = { owner, timer: null };
  scheduleNightlyPoll();
}

function scheduleNightlyPoll(): void {
  const poll = _nightlyPoll;
  if (!poll) return;
  poll.timer = setTimeout(() => { void tickNightlyPoll(); }, NIGHTLY_POLL_MS);
}

async function tickNightlyPoll(): Promise<void> {
  const poll = _nightlyPoll;
  if (!poll) return;
  poll.timer = null;
  // Смена владельца: опрос встаёт без нового запроса — читать чужой проход в чужой стор нечем (M2)
  if (poll.owner !== readStoredUserId()) { _nightlyPoll = null; return; }
  let snap: WikiSettingsSnapshot;
  try {
    // Обычная loadWikiSettings: медленный висящий GET делится, а не повторяется
    snap = await loadWikiSettings();
  } catch {
    // Сбой чтения: проход мог и не кончаться — одиночный сбой опрос не останавливает,
    // на следующем интервале спросим снова (состояние поправит успешный ответ)
    if (_nightlyPoll === poll) scheduleNightlyPoll();
    return;
  }
  if (_nightlyPoll !== poll) return; // опрос сняли во время запроса (смена владельца, тестовый шов)
  if (poll.owner !== readStoredUserId()) { _nightlyPoll = null; return; }
  if (snap.nightlyRunning) { scheduleNightlyPoll(); return; }
  // Последний GET сказал «не идёт»: он и есть финальный снимок со свежим lastNight —
  // его уже записал fetchWikiSettings. Опрос встаёт.
  _nightlyPoll = null;
}

// ─── «Применение»: общий выключатель и возврат класса (S7d-1) ───
// Те же ручки и тот же слот, что у ночной правки: оптимистичное изменение, ответ подтверждает,
// отказ откатывает; поколение и привязка к владельцу — как у putWikiNightly. Отличие одно:
// PUT не несёт замера классов, а блок «Применение» обязан показывать текущую долю (шапка
// контроллера) — поэтому после подтверждённой правки настройки перечитываются GET'ом
// («он читается GET после действия», WikiSettingsController.cs:94). Сбой перечитки не отменяет
// действие: в сторе остаётся состояние ответа с долями последнего чтения.

export type WikiPutSettingsResult = WikiPutNightlyResult;

async function putApplySettings(
  body: Record<string, unknown>,
  optimistic: (prev: WikiSettingsSnapshot) => WikiSettingsSnapshot,
): Promise<WikiPutSettingsResult> {
  const gen = ++_settingsGen;
  const owner = readStoredUserId();
  const prev = _settings;
  // Оптимистично, как тумблер ночи: значение видно сразу, ответ подтвердит или откатит
  if (prev) {
    _settings = optimistic(prev);
    emit();
  }
  let resp: unknown;
  try {
    resp = await wikiHttp<unknown>('/wiki/settings', { method: 'PUT', body: JSON.stringify(body) });
  } catch (e) {
    if (gen === _settingsGen && owner === readStoredUserId()) {
      _settings = prev;
      emit();
    }
    const we = toWikiError(e);
    if (!we) throw e; // сетевой сбой/таймаут — не отказ контракта: наружу как есть
    return { ok: false, error: we };
  }
  const nightly = readNightly(resp);
  if (!nightly) {
    if (gen === _settingsGen && owner === readStoredUserId()) {
      _settings = prev;
      emit();
    }
    return { ok: false, error: new WikiError(0, null, 'неожиданный ответ настроек вики') };
  }
  const apply = readApplyPart(resp);
  const snap: WikiSettingsSnapshot = {
    ...apply,
    classes: mergeClassStats(apply.classes, prev?.classes),
    nightly,
    lastNight: prev?.lastNight ?? null, // PUT итога не несёт (N7c) — сохраняется от последнего GET
    nightlyRunning: prev?.nightlyRunning ?? false, // PUT состояния прогона не несёт (WN-5) — той же мерой
  };
  if (gen === _settingsGen && owner === readStoredUserId()) {
    _settings = snap;
    emit();
  }
  // Свежий замер — из GET-оценки; её боковой эффект (оценка может выключить перешагнувший
  // класс) — часть оценки, не это действие. Сбой перечитки молчит: состояние ответа уже записано.
  await loadWikiSettings().catch(() => undefined);
  return { ok: true, snapshot: snap };
}

// Общий выключатель «Применять без ревью» (макет M2): выключенный — всё, включая неспорное,
// идёт во «Входящие» на решение человека.
export function putWikiAutoApply(value: boolean): Promise<WikiPutSettingsResult> {
  return putApplySettings(
    { autoApplyEnabled: value },
    prev => ({ ...prev, autoApplyEnabled: value }),
  );
}

// «Снова применять само» по ключу класса ('origin×op' — как его отдаёт бэк): PUT с
// Enable=true — бэк ставит ResumedAt, окно отсчёта класса начинается заново (мера 7).
// Оптимистичное — то же направление: класс включён, причина снята; ответ подтверждает целым.
export function resumeWikiApplyClass(classKey: string): Promise<WikiPutSettingsResult> {
  return putApplySettings(
    { class: classKey, enable: true },
    prev => ({
      ...prev,
      classes: prev.classes.map(c => (c.class === classKey ? { ...c, disabled: false, reason: null } : c)),
    }),
  );
}

// «Пометить чат» / «снять пометку» (N1b): пометка живёт на чате (Session.WikiMark), не в сторе
// отбора — зон и порядка списка она сама не меняет, их даст следующая перечитка loadWikiChats.
// Чистый транспорт (тот же порядок, что у analyzeWikiChat): состояния стора не трогает.
// Отказы — WikiError с кодом (404 not_found, 409 wiki_llm_wiki_off). 400 бывает и с кодом
// (значение вне important/skip/null — wiki_mark_invalid, WikiChatsController.cs:207-211),
// и без кода (неразбираемое тело — автоматический ValidationProblemDetails ASP.NET):
// на код 400 здесь опираться нельзя, этот отказ различается только по статусу.
export async function setWikiChatMark(sessionId: string, mark: WikiChatMark | null): Promise<WikiChatMark | null> {
  const res = await wikiHttp<{ mark?: unknown }>(
    `/wiki/chats/${encodeURIComponent(sessionId)}/mark`,
    { method: 'PUT', body: JSON.stringify({ mark }) },
  );
  const m = res?.mark;
  return m === 'important' || m === 'skip' ? m : null;
}

// ─── «Начать вики заново»: чистка ИИ-страниц (J-F0; POST /api/wiki/purge, J-P2, ADR-039 реш. 10) ───
// Предпросмотр и запуск — одна ручка, режим параметром dryRun; без явного dryRun бэк делает
// предпросмотр (разрушающее действие требует явного решения). Инициатор — только человек
// (сервисному токену 403); нулевой состав — не отказ: 200 с нулями (WikiController.cs:656-687).
// Отказы — WikiError с кодом Хранителя: wiki_purge_manifest (409 — манифест затронутого
// раздела невалиден, человек чинит сам), wiki_purge_io | wiki_purge_archive (503 — транзитный
// сбой диска: повторить можно, на полпути ничего не осталось), wiki_refused (409, прочий
// отказ). 500 нет ни на одном коде (WikiKeeper.cs:173-175, WikiController.cs:695-700).

// Одна страница состава предпросмотра (WikiController.cs:942): class — предикат включения
// ('ai' | 'together' | 'chronicle' | 'source', таблица решения ADR-039): по классам —
// aggregate-сводка, по путям человек решает.
export interface WikiPurgePageDto { pageKey: string; section: string; class: string }

// Ответ обоих режимов (WikiController.cs:951-957). Предпросмотр: countsBySection (числа по
// разделам и классам) + pages (состав путей); на диске не тронуто ничего, deleted 0,
// archivePath null. Запуск: deleted — число удалённых, archivePath — путь архива относительно
// корня вики (null, когда архивировать было нечего), staledPackages — число погашенных
// открытых ИИ-пакетов; pages пуст — этих файлов уже нет на диске.
// warning — предупреждение исхода: чистка прошла, но перезапуск разбора не доведён; текст
// формулирует бэк, пути хоста в нём нет (WikiController.cs, доработка ed105a07 — идёт
// параллельно). Отсутствие поля (бэк без доработки) читаем как null (тот же приём, что
// отсутствующий lastNight).
export interface WikiPurgeDto {
  dryRun: boolean;
  countsBySection: Record<string, Record<string, number>>;
  pages: WikiPurgePageDto[];
  deleted: number;
  archivePath: string | null;
  staledPackages: number;
  warning?: string | null;
}

// «Что будет удалено»: на диске не тронуто ничего, состояния стора не трогает.
export function previewWikiPurge(): Promise<WikiPurgeDto> {
  return wikiHttp<WikiPurgeDto>('/wiki/purge?dryRun=true', { method: 'POST' });
}

// «Начать заново»: удалить ИИ-страницы. После — перечитываем дерево и открытые каталоги
// (тот же порядок, что у deleteWikiSection): страницы удалены, витрина не должна показывать
// прежний состав; сбой перечитки чистку не отменяет.
export async function runWikiPurge(): Promise<WikiPurgeDto> {
  const res = await wikiHttp<WikiPurgeDto>('/wiki/purge?dryRun=false', { method: 'POST' });
  await refreshWiki();
  return res;
}

// ─── Снимки и подписки ───

export function getWikiTreeSnapshot(): WikiTreeDto | null { return _tree; }
export function getWikiCatalogSnapshot(sectionKey: string): WikiCatalogDto | undefined { return _catalogs.get(sectionKey); }
export function getWikiSettingsSnapshot(): WikiSettingsSnapshot | null { return _settings; }
export function getWikiPageSnapshot(sectionKey: string, pageKey: string): WikiPageDto | null {
  const key = pageCacheKey(sectionKey, pageKey);
  return _page && _page.cacheKey === key ? _page.value : null;
}

export function useWikiTree(): WikiTreeDto | null {
  return useSyncExternalStore(subscribe, () => _tree, () => _tree);
}

export function useWikiCatalog(sectionKey: string): WikiCatalogDto | null {
  return useSyncExternalStore(
    subscribe,
    () => _catalogs.get(sectionKey) ?? null,
    () => _catalogs.get(sectionKey) ?? null,
  );
}

export function useWikiPage(sectionKey: string, pageKey: string): WikiPageDto | null {
  const key = pageCacheKey(sectionKey, pageKey);
  const open = sectionKey !== '' && pageKey !== '';
  // Счётчик открытых страниц — по подписке экрана: стабильна на всё время открытой
  // страницы (идентичность useCallback по ключу), при закрытии/смене страницы —
  // снятие подписки снимает и счётчик
  const subscribeOpen = useCallback((fn: () => void) => {
    const off = subscribe(fn);
    if (!open) return off;
    retainOpenPage(key);
    return () => { releaseOpenPage(key); off(); };
  }, [key, open]);
  const snapshot = () => (_page && _page.cacheKey === key ? _page.value : null);
  return useSyncExternalStore(subscribeOpen, snapshot, snapshot);
}

// Факт «стор снял именно эту страницу 404 перечитки после мутации» (W4-5, дефект 3f3449e4).
// Отдельно от отсутствия снимка: отсутствие — и «страница грузится», и «страницу снес откат»;
// экран возвращается к каталогу только по этому факту. Подписка — обычная (без счётчика
// открытых страниц: открытой страницу делает useWikiPage, здесь только чтение факта).
export function useWikiPageGone(sectionKey: string, pageKey: string): boolean {
  const key = pageCacheKey(sectionKey, pageKey);
  const snapshot = () => _pageGoneKey === key;
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

// Дерево загружено (отличить «ещё не грузили» от «пустой каталог»)
export function useWikiLoaded(): boolean {
  return useSyncExternalStore(subscribe, () => _loaded, () => _loaded);
}

// Снимок ночной части настроек для блока «Ночной разбор»: null — ещё не читали (загрузка —
// за вызывающим экраном, как ensureWikiLoaded для дерева).
export function useWikiSettings(): WikiSettingsSnapshot | null {
  return useSyncExternalStore(subscribe, () => _settings, () => _settings);
}

// ─── Запрос открыть страницу извне (OPEN-1) ───
// Одноразовое намерение: вызывающий (кнопка «Открыть хронику» в шапке чата) кладёт его
// сюда, смонтированный экран вики забирает. Событие 'cc-open-wiki-page' слушателя не имело:
// из шапки чата раздел «Заметки» не смонтирован — запрос уходил в пустоту. Здесь запрос
// живёт до того, как его возьмут, — в том числе пока дерево ещё грузится — и второй запрос
// замещает первый (последний клик побеждает, дублей нет). Единственное место этого состояния.
// section — ключ раздела из дерева ('projects/{id}' — тот же формат, что parseSectionKey),
// path — ключ страницы из каталога (полный путь относительно pages/, как его отдаёт бэк:
// 'Проекты/{id}/Хроники/{sessionId}.md' — та же форма, что chroniclePath из GET /api/wiki/chats).

export interface WikiOpenRequest { section: string; path: string }

let _openReq: WikiOpenRequest | null = null;

export function requestOpenWikiPage(req: WikiOpenRequest): void {
  _openReq = req;
  emit();
}

// Забирает запрос и очищает его: null — когда запроса нет. Берёт тот, кто открывает
// страницу (WikiTab); экран, которого интересует только появление, подписан на
// useWikiOpenRequest.
export function consumeOpenWikiPage(): WikiOpenRequest | null {
  const r = _openReq;
  if (!r) return null;
  _openReq = null;
  emit();
  return r;
}

export function useWikiOpenRequest(): WikiOpenRequest | null {
  return useSyncExternalStore(subscribe, () => _openReq, () => _openReq);
}

// Тестовый шов: сброс модульного состояния (каждый тест начинает с чистого стора,
// realtime подписывается заново). Швы такого рода в проекте — объектом __test
// (образец — featureFlags.ts).
export const __test = {
  reset() {
    _tree = null;
    _catalogs.clear();
    _page = null;
    _pageGoneKey = null;
    _openPages.clear();
    _openReq = null;
    _settings = null;
    _loaded = false;
    _treeLoading = null;
    _realtimeWired = false;
    _ownerId = null;
    _treeGen = 0;
    _pageGen = 0;
    _settingsGen = 0;
    _settingsReq = null;
    _catalogGen.clear();
    // Полёт ручного запуска и цепочка опроса — тоже состояние стора: тест не наследует их
    if (_nightlyPoll?.timer) clearTimeout(_nightlyPoll.timer);
    _nightlyPoll = null;
    _nightlyRunReq = null;
  },
  // Тестовый шов: подписка экрана на страницу. Открытой страницу делает счётчик, который
  // экран поднимает через useWikiPage, — те же операции retainOpenPage/releaseOpenPage;
  // тесту не нужен React-рендер, а перечитка после мутации — ровно про этот открытый ключ.
  openPage(sectionKey: string, pageKey: string): () => void {
    const key = pageCacheKey(sectionKey, pageKey);
    retainOpenPage(key);
    return () => { releaseOpenPage(key); };
  },
  // Тестовый шов факта снятия (тот же разбор ключа, что у useWikiPageGone): store-тестам
  // он нужен без React-подписки — проверить, что 404 перечитки оставил факт и что
  // перечитка той же страницы его снимает.
  pageGone(sectionKey: string, pageKey: string): boolean {
    return _pageGoneKey === pageCacheKey(sectionKey, pageKey);
  },
};

// ─── Авторство страницы (ADR-038 «Контракт для фронта», SV1-В; единое дерево F2) ───
// Бэк отдаёт authorship в строке GET /api/wiki/section; до его слияния поля нет —
// отсутствие (и любое неизвестное значение) читаем как 'ai'. Значок «заметка» сюда не
// входит: его фронт ставит сам по источнику строки.

export type WikiAuthorship = 'ai' | 'together' | 'human';

export interface WikiPageInfoAuthoredDto extends WikiPageInfoDto { authorship?: WikiAuthorship }

export function wikiAuthorshipOf(page: { authorship?: unknown }): WikiAuthorship {
  const a = page.authorship;
  return a === 'together' || a === 'human' ? a : 'ai';
}

// ─── Сноски страницы с авторством (ADR-038 «Контракт для фронта», SV1-В; F3a) ───
// Бэк (B7) добавит в GET /api/wiki/page поля authorship и footnotes[]; до слияния их нет —
// страница читается по-прежнему (нет footnotes = старый вид, нет authorship = 'ai').

export interface WikiFootnoteDto {
  // Метка сноски в тексте: 's3' (допускается и форма '[^s3]' — снимаем обёртку)
  label: string;
  // Заголовок карточки источника (для показа)
  card: string;
  // Ключ страницы карточки для чтения; нет — источник не прочитать
  cardPage?: string | null;
  // Происхождение источника: 'human' — правка человека, прочее (session, note…) — ИИ
  origin: string;
  // Время записи, ISO 8601
  captured?: string | null;
  // Имя автора правки человека; бэк пока не отдаёт — тогда подпись «вы»
  author?: string | null;
}

export interface WikiPageAuthoredDto extends WikiPageDto {
  authorship?: WikiAuthorship;
  footnotes?: WikiFootnoteDto[];
}

export function wikiFootnoteLabel(label: string): string {
  const m = /^\[\^(.+)\]$/.exec(label.trim());
  return m ? m[1] : label.trim();
}

// Карточка источника, прочитанная мимо слота страницы стора: loadWikiPage перезаписал бы
// открытую страницу карточкой, а поповеру нужны лишь имя и ref шапки.
export interface WikiSourceCardInfo { name: string | null; ref: string | null }

export async function loadWikiSourceCard(sectionKey: string, cardKey: string): Promise<WikiSourceCardInfo> {
  const page = await wikiHttp<WikiPageDto>(
    `/wiki/page?${sectionQuery(sectionKey)}&page=${encodeURIComponent(cardKey)}`,
  );
  let ref: string | null = null;
  let name: string | null = null;
  for (const line of page.content.replace(/\r\n/g, '\n').split('\n')) {
    if (ref === null && line.startsWith('ref: ')) ref = line.slice(5).trim() || null;
    else if (name === null && line.startsWith('# ')) name = line.slice(2).trim() || null;
  }
  return { name, ref };
}

// ─── Правка страницы человеком (ADR-038 «Контракт для фронта», POST /api/wiki/pages/edit; F4a) ───

// Свежая версия страницы из 409 wiki_page_changed; null в теле — страницы больше нет
export interface WikiConflictPage { pageKey: string; content: string; hash: string }

// Причина отказа гейта (SV1-Г): пока бэк поля не отдаёт, массив пуст и экран идёт по violations
export interface WikiEditIssue { code: string; page?: string | null; link?: string | null; message: string }

// GET page с хэшем: baseHash правки — хэш того текста, с которым открыт редактор
export interface WikiPageEditableDto extends WikiPageAuthoredDto { hash?: string }

export type WikiEditResult =
  | { status: 'applied'; hash: string; content: string }
  | { status: 'unchanged'; hash: string };

function readEditRefusalBody(we: WikiError, body: unknown): void {
  const b = body as { page?: unknown; violations?: unknown; issues?: unknown } | null | undefined;
  if (!b || typeof b !== 'object') return;
  const pg = b.page as { pageKey?: unknown; content?: unknown; hash?: unknown } | null | undefined;
  if (pg && typeof pg.pageKey === 'string' && typeof pg.content === 'string' && typeof pg.hash === 'string') {
    we.page = { pageKey: pg.pageKey, content: pg.content, hash: pg.hash };
  }
  if (Array.isArray(b.violations)) we.violations = b.violations.filter((v): v is string => typeof v === 'string');
  if (Array.isArray(b.issues)) {
    we.issues = b.issues
      .filter((i): i is { code: string; message: string; page?: string | null; link?: string | null } =>
        !!i && typeof (i as { code?: unknown }).code === 'string' && typeof (i as { message?: unknown }).message === 'string')
      .map(i => ({ code: i.code, message: i.message, page: i.page ?? null, link: i.link ?? null }));
  }
}

// Сохранить правку страницы. content — полный новый текст в форме GET page. После applied
// слот страницы перечитывается (хэш, сноски с авторством, секции); сбой перечитки правку
// не отменяет. Отказы — WikiError: 409 wiki_page_changed (в .page свежая версия, ничего не
// записано), 409 wiki_unified_off, 400 wiki_edit_invalid (.violations/.issues).
export async function editWikiPage(
  sectionKey: string, pageKey: string, baseHash: string | null, content: string,
): Promise<WikiEditResult> {
  const res = await wikiHttp<{ status?: unknown; hash?: unknown; content?: unknown }>('/wiki/pages/edit', {
    method: 'POST',
    body: JSON.stringify({ section: sectionKey, page: pageKey, baseHash, content }),
  });
  const hash = typeof res?.hash === 'string' ? res.hash : '';
  if (res?.status === 'unchanged') return { status: 'unchanged', hash };
  if (res?.status !== 'applied') throw new WikiError(0, null, 'неожиданный ответ правки страницы');
  await loadWikiPage(sectionKey, pageKey).catch(() => undefined);
  return { status: 'applied', hash, content: typeof res.content === 'string' ? res.content : content };
}

// ─── Метки «ИИ заменил / оспаривает» и «Вернуть мою» (ADR-038 «Контракт для фронта», решение 4; F3b) ───
// overrides[] в GET page выводит сервер из журнала за 14 дней — фронт окно не считает и
// даты не сравнивает: нет в ответе — нет метки. previous — фактическая строка «Истории»
// (сервер находит её сам); null — пары для «Вернуть мою» нет, кнопки не будет.

export type WikiOverrideOp = 'supersede' | 'conflict';

export interface WikiOverrideDto {
  op: WikiOverrideOp;
  // Текст утверждения ИИ без сносок
  statement: string | null;
  previous: string | null;
  // Только у спора (op=conflict): живая строка человека из активной секции, на которой стоит
  // метка «ИИ оспаривает»; null — строки нет (метки нет, плашка остаётся). У supersede — null
  target?: string | null;
  // Время замены, ISO 8601
  at: string;
  packageId: string;
}

export interface WikiPageOverriddenDto extends WikiPageEditableDto { overrides?: WikiOverrideDto[] }

// Журнал: запись, где ИИ сам поменял утверждение человека (фильтр «ИИ поменял моё»)
export type WikiOverrodeKind = 'replaced' | 'disputed';
export interface WikiJournalEntryOverrideDto extends WikiJournalEntryDto {
  overrodeHuman?: boolean;
  overrodeKind?: WikiOverrodeKind | null;
  // Страница, где ИИ заменил утверждение (null/нет — берётся pages[0]); true — человек уже вернул свою
  overridePageKey?: string | null;
  overrideReverted?: boolean;
}

// Отказы «Вернуть мою» читаются по тем же кодам WikiError, что у правки: 409 wiki_page_changed
// (свежая страница в .page), 409 wiki_unified_off, 400 wiki_edit_invalid (.message — причина).

// Сравнение утверждения из overrides[] со строкой страницы: сервер отдаёт его без сносок и
// маркера, на странице — со сносками. Без маркера списка (он факультативен: RenderApply пишет
// строки без него), без сносок, пробелы схлопнуты, NFC. Единственная копия — витрина берёт её же.
export function normWikiStatement(s: string): string {
  return s.replace(/^[-*]\s+/, '').replace(/\s*\[\^[^\]\r\n]+\]/g, '').replace(/\s+/g, ' ').trim().normalize('NFC');
}

const FN_DEF_LINE_RX = /^\[\^[^\]\r\n]+\]: .+ — «.*»$/;

// Действующие строки страницы — те же, что витрина рисует в секциях (зеркало parseWikiPage):
// содержательные строки секций кроме «Истории», с маркером или без. Пустые, шапка, заголовок,
// определения сносок и строки вне секций не в счёте. Паритет с parseWikiPage держит тест.
export function wikiActiveLines(content: string): string[] {
  const lines = content.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  let i = 0;
  if (lines[0] === '---') {
    const close = lines.indexOf('---', 1);
    if (close > 0) i = close + 1;
  }
  const out: string[] = [];
  let titled = false;
  let section: string | null = null;
  for (; i < lines.length; i++) {
    const line = lines[i];
    if (!line || FN_DEF_LINE_RX.test(line)) continue;
    const t = /^# (.+)$/.exec(line);
    if (t && !titled) { titled = true; continue; }
    const h = /^## (.+)$/.exec(line);
    if (h) { section = h[1]; continue; }
    if (section !== null && section !== 'История') out.push(line);
  }
  return out;
}

// Замены и споры страницы; чужие и неизвестные op фронт не рисует. Если в ответе есть текст
// страницы, остаются только те, чьё утверждение ещё действует: после «Вернуть мою» бэк пакет ИИ
// не убирает, но утверждение ИИ уходит в «Историю» — метка и точка гаснут
export function wikiOverridesOf(page: { overrides?: unknown; content?: unknown } | null | undefined): WikiOverrideDto[] {
  const raw = page?.overrides;
  if (!Array.isArray(raw)) return [];
  const all = raw.filter((o): o is WikiOverrideDto =>
    !!o && ((o as WikiOverrideDto).op === 'supersede' || (o as WikiOverrideDto).op === 'conflict')
    && typeof (o as WikiOverrideDto).at === 'string' && typeof (o as WikiOverrideDto).packageId === 'string');
  if (typeof page?.content !== 'string') return all;
  const active = new Set(wikiActiveLines(page.content).map(normWikiStatement));
  return all.filter(o => {
    // Замена — по утверждению ИИ. Спор — по живой строке человека (target): переписал её —
    // спор снят. Без target (человек переписал утверждение) карточка живёт, пока довод ИИ
    // (statement, лежит в «Противоречиях») среди действующих строк; previous лежит в «Истории»
    // и в проверку не входит — он нужен только «Вернуть мою»
    const text = o.op === 'supersede' ? o.statement : (o.target ?? o.statement);
    return !!text && active.has(normWikiStatement(text));
  });
}

// Состояние «есть замена, которую человек не вернул»: питает точку на «Журнале». Берётся из
// открытой страницы стора; после «Вернуть мою» страница перечитывается, замены в ответе нет — точка гаснет.
export function wikiHasReplacement(page: { overrides?: unknown; content?: unknown } | null | undefined): boolean {
  return wikiOverridesOf(page).some(o => o.op === 'supersede');
}

export function useWikiReplacementDot(): boolean {
  const snap = () => wikiHasReplacement(_page?.value as WikiPageOverriddenDto | undefined);
  return useSyncExternalStore(subscribe, snap, snap);
}

// «Вернуть мою»: current — действующее утверждение ИИ (statement), restore — строка человека
// из «Истории» (previous). Ответы — как у правки. Перечитки здесь нет намеренно: её делает
// вызывающий, и только если страница ещё та же (ответ после смены страницы слот не трогает).
export async function restoreWikiPage(
  sectionKey: string, pageKey: string, baseHash: string | null, current: string, restore: string,
): Promise<WikiEditResult> {
  const res = await wikiHttp<{ status?: unknown; hash?: unknown; content?: unknown }>('/wiki/pages/restore', {
    method: 'POST',
    body: JSON.stringify({ section: sectionKey, page: pageKey, baseHash, current, restore }),
  });
  const hash = typeof res?.hash === 'string' ? res.hash : '';
  if (res?.status === 'unchanged') return { status: 'unchanged', hash };
  if (res?.status !== 'applied') throw new WikiError(0, null, 'неожиданный ответ возврата формулировки');
  return { status: 'applied', hash, content: typeof res.content === 'string' ? res.content : '' };
}

// ─── «Перенести в вики» (F5b; POST /api/wiki/ingest/note с transfer: true, ADR-038 «Контракт для фронта») ───
// Заметка становится страницей human в доме заметки (личный vault → «Личное», notes/ проекта →
// раздел проекта), а сама заметка заменяется ссылкой на неё: ручка делает обе записи сразу,
// подтверждение UI спрашивает ДО вызова. target не передаём — дом задаёт бэк (400
// wiki_transfer_target). section/page ответа — ключи созданной страницы в той же форме, что
// ключ раздела дерева и ключ страницы каталога, — экран открывает их requestOpenWikiPage (OPEN-1).
// Отказы — WikiError с кодом: 404 not_found (заметки нет или чужая), 409 wiki_unified_off |
// wiki_note_transferred (уже перенесена) | wiki_page_changed (имя занято: «переименуйте
// заметку и повторите»), 400 wiki_edit_invalid (.violations/.issues) | wiki_note_id;
// сервисному токену 403 — до ветки переноса. С LI-2 ручка сама заводит вики владельца
// (WikiController.cs:498): 409 wiki_not_initialized из неё больше не приходит; код остаётся
// в типах — ответ второй линии защиты (гейт WikiApplyService.cs:194, за которым страница).
// Повтор переноса доводит несделанную замену: занятая страница этого же переноса узнаётся
// на бэке (WikiNoteTransfer.IsPageOfThisTransfer).

export interface WikiNoteTransferDto {
  section: string;
  page: string;
  packageId: string | null;
}

export async function transferWikiNote(noteId: string): Promise<WikiNoteTransferDto> {
  const res = await wikiHttp<{ outcome?: unknown; section?: unknown; page?: unknown; packageId?: unknown }>(
    '/wiki/ingest/note',
    { method: 'POST', body: JSON.stringify({ noteId, transfer: true }) },
  );
  // 200 — всегда {outcome:"transferred", section, page}; тело вне этой формы — не HTTP-отказ,
  // ответ вне контракта (тот же порядок, что в ingestWikiNote): вызывающий видит ошибку,
  // а не тихую пустоту
  if (res?.outcome !== 'transferred' || typeof res.section !== 'string' || typeof res.page !== 'string') {
    throw new WikiError(0, null, 'неожиданный ответ переноса заметки');
  }
  return {
    section: res.section,
    page: res.page,
    packageId: typeof res.packageId === 'string' ? res.packageId : null,
  };
}

// ─── «Принять как мою правку» (F5c; POST /api/wiki/adopt с mode: "mine", ADR-038 «Контракт для фронта») ───
// Внешняя правка (дрейф) становится правкой человека: бэк собирает дифф «манифест → диск»
// в пакет human (WikiController.Adopt → AdoptMineAsync). baseHash обязателен — «хэш файла,
// каким его видел человек»: «Здоровье» страницу не читает, поэтому хэш берётся из GET page
// непосредственно перед запросом — единственный источник хэша на этом экране. Между чтением
// и применением файл мог быть правлен ещё раз — это ровно 409 wiki_page_changed, бэк ничего
// не пишет. Ответы как у edit: 200 applied/unchanged; 409 wiki_page_changed (файл правили
// ещё раз), 409 wiki_drift (дрейф других страниц раздела), 409 wiki_unified_off (флаг
// llm-wiki выключен — UnifiedOffRefusal до ветки), 400 wiki_edit_invalid (.violations/.issues),
// 400 wiki_adopt_mode. Чистый транспорт (тот же порядок, что analyzeWikiChat/transferWikiNote):
// состояния стора не трогает, ничего не перечитывает — перечитку здоровья и отбивку ответа
// по пути страницы делает вызывающий.

export interface WikiAdoptMineResult {
  status: 'applied' | 'unchanged';
  hash: string;
}

export async function adoptWikiPageMine(sectionKey: string, pageKey: string): Promise<WikiAdoptMineResult> {
  const page = await wikiHttp<WikiPageEditableDto>(
    `/wiki/page?${sectionQuery(sectionKey)}&page=${encodeURIComponent(pageKey)}`,
  );
  const baseHash = typeof page?.hash === 'string' ? page.hash : '';
  const res = await wikiHttp<{ status?: unknown; hash?: unknown }>('/wiki/adopt', {
    method: 'POST',
    body: JSON.stringify({ section: sectionKey, page: pageKey, mode: 'mine', baseHash }),
  });
  if (res?.status !== 'applied' && res?.status !== 'unchanged') {
    // 200 — всегда {status:"applied"|"unchanged", hash}: тело вне формы — ответ вне контракта
    // (тот же порядок, что в transferWikiNote), а не тихая пустота
    throw new WikiError(0, null, 'неожиданный ответ принятия правки');
  }
  return { status: res.status, hash: typeof res.hash === 'string' ? res.hash : '' };
}
