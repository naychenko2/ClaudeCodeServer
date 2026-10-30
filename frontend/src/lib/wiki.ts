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

import { useSyncExternalStore } from 'react';
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

export type WikiErrorCode =
  | 'wiki_foreign'
  | 'wiki_section_key'
  | 'wiki_page_key'
  | 'wiki_project_missing'
  | 'wiki_section_off'
  | 'wiki_sphere_reserved'
  | 'wiki_refused'
  | 'wiki_drift'
  | 'wiki_not_orphan';

const WIKI_ERROR_CODES: ReadonlySet<string> = new Set<string>([
  'wiki_foreign', 'wiki_section_key', 'wiki_page_key', 'wiki_project_missing', 'wiki_section_off',
  'wiki_sphere_reserved', 'wiki_refused', 'wiki_drift', 'wiki_not_orphan',
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

async function wikiHttp<T>(url: string, init?: RequestInit): Promise<T> {
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

// ─── Чтение ───

export async function loadWikiTree(): Promise<WikiTreeDto> {
  const gen = ++_treeGen;
  const owner = readStoredUserId();
  const tree = await wikiHttp<WikiTreeDto>('/api/wiki/tree');
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
  const catalog = await wikiHttp<WikiCatalogDto>(`/api/wiki/section?${sectionQuery(sectionKey)}`);
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
    `/api/wiki/page?${sectionQuery(sectionKey)}&page=${encodeURIComponent(pageKey)}`,
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
  const res = await wikiHttp<WikiInitResult>('/api/wiki/initialize', {
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
  const res = await wikiHttp<WikiAdoptResult>('/api/wiki/adopt', {
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
  const res = await wikiHttp<WikiDeleteResult>(`/api/wiki/section?${sectionQuery(sectionKey)}`, {
    method: 'DELETE',
  });
  // Поднимаем поколение ключа: идущий ответ каталога удалённого раздела его не вернёт (M1)
  _catalogGen.set(sectionKey, (_catalogGen.get(sectionKey) ?? 0) + 1);
  _catalogs.delete(sectionKey);
  if (_page && _page.cacheKey.startsWith(`${sectionKey}\n`)) _page = null;
  await refreshWiki();
  return res;
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
  return useSyncExternalStore(
    subscribe,
    () => (_page && _page.cacheKey === key ? _page.value : null),
    () => (_page && _page.cacheKey === key ? _page.value : null),
  );
}

// Дерево загружено (отличить «ещё не грузили» от «пустой каталог»)
export function useWikiLoaded(): boolean {
  return useSyncExternalStore(subscribe, () => _loaded, () => _loaded);
}

// Тестовый шов: сброс модульного состояния (каждый тест начинает с чистого стора,
// realtime подписывается заново). Швы такого рода в проекте — объектом __test
// (образец — featureFlags.ts).
export const __test = {
  reset() {
    _tree = null;
    _catalogs.clear();
    _page = null;
    _loaded = false;
    _treeLoading = null;
    _realtimeWired = false;
    _ownerId = null;
    _treeGen = 0;
    _pageGen = 0;
    _catalogGen.clear();
  },
};
