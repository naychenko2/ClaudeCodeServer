// Вики владельца: стор дерева разделов, каталогов, страниц и операций над ними (S5a).
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
  | 'wiki_note_id';

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
  'packages_limit', 'wiki_note_id',
]);

export class WikiError extends Error {
  readonly status: number;
  readonly code: WikiErrorCode | null;

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
  return new WikiError(status, code, message);
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

// Перечитать открытую страницу после мутации, меняющей содержимое страниц (откат
// пакета, откат по источнику): витрина под открытым экраном не должна показывать
// состояние до отката. Только пока страница остаётся открытой (счётчик подписок) —
// сбой перечитки не роняет мутацию (тот же порядок, что в adopt).
async function reReadOpenPage(): Promise<void> {
  const page = _page;
  if (!page || !(_openPages.get(page.cacheKey) ?? 0)) return;
  const parts = splitPageCacheKey(page.cacheKey);
  if (!parts) return;
  await loadWikiPage(parts.sectionKey, parts.pageKey).catch(() => undefined);
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
export async function adoptWikiPage(sectionKey: string, pageKey: string): Promise<WikiAdoptResult> {
  const res = await wikiHttp<WikiAdoptResult>('/wiki/adopt', {
    method: 'POST',
    body: JSON.stringify({ section: sectionKey, page: pageKey }),
  });
  if (_page && _page.cacheKey === pageCacheKey(sectionKey, pageKey)) {
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

export interface WikiJournalDto { entries: WikiJournalEntryDto[]; take: number; linesRead: number; invalidLines: number }

// Фильтры окна: section — ключ из дерева своей вики, origin — значение из закрытого перечня
// (тот же, что фильтр источников экрана), class — пара «origin × op». Пустое — без фильтра.
// take не передаём: экран живёт в окне по умолчанию (200), потолок (1000) — дело бэка.
export interface WikiJournalQuery { section?: string | null; origin?: string | null; class?: string | null }

export async function loadWikiJournal(q: WikiJournalQuery = {}): Promise<WikiJournalDto> {
  const params = new URLSearchParams();
  if (q.section) params.set('section', q.section);
  if (q.origin) params.set('origin', q.origin);
  if (q.class) params.set('class', q.class);
  const qs = params.toString();
  return wikiHttp<WikiJournalDto>(`/wiki/journal${qs ? `?${qs}` : ''}`);
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
// marked_skip | personal_section | wiki_not_initialized — WikiError с кодом.
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
// 409 wiki_not_initialized | packages_limit (до захвата), 404 not_found (заметка не стала
// источником: чужая, нет такой).
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
// Зоны сегодня: 'triage' | 'gray' | 'skipped' | 'nothing_new' | 'excluded'
// (WikiChatScorer.cs:39-42, WikiChatIngestService.cs:100); тип — string: неизвестная зона
// будущего шага — запись списка, а не отказ разбора (как на бэке, ZoneRank ведёт её в конец).
// chroniclePath — формула бэка (WikiChatIngestService.ChroniclePath): для проектного дома
// «Проекты/{id}/Хроники/{sessionId}.md», null для не-проектного дома (личный — с Ф2);
// существования файла не обещает — хронику создаёт применённый разбор.
// counts (числа по показанной зоне) — часть ответа бэка; эта функция их не отдаёт
// (не заявлена задачей; при нужности экрана — отдельным пунктом).

export interface WikiTriageSignalDto { name: string; weight: number }

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
}

interface WikiChatsListDto { items: WikiChatTriage[]; counts: Record<string, number> }

export async function loadWikiChats(): Promise<WikiChatTriage[]> {
  const res = await wikiHttp<WikiChatsListDto>('/wiki/chats');
  return Array.isArray(res?.items) ? res.items : [];
}

// ─── Снимки и подписки ───

export function getWikiTreeSnapshot(): WikiTreeDto | null { return _tree; }
export function getWikiCatalogSnapshot(sectionKey: string): WikiCatalogDto | undefined { return _catalogs.get(sectionKey); }
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
    _openPages.set(key, (_openPages.get(key) ?? 0) + 1);
    return () => {
      const n = (_openPages.get(key) ?? 1) - 1;
      if (n <= 0) _openPages.delete(key); else _openPages.set(key, n);
      off();
    };
  }, [key, open]);
  const snapshot = () => (_page && _page.cacheKey === key ? _page.value : null);
  return useSyncExternalStore(subscribeOpen, snapshot, snapshot);
}

// Дерево загружено (отличить «ещё не грузили» от «пустой каталог»)
export function useWikiLoaded(): boolean {
  return useSyncExternalStore(subscribe, () => _loaded, () => _loaded);
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
    _openPages.clear();
    _openReq = null;
    _loaded = false;
    _treeLoading = null;
    _realtimeWired = false;
    _ownerId = null;
    _treeGen = 0;
    _pageGen = 0;
    _catalogGen.clear();
  },
};
