// Общий кэш черновиков build-it-yourself для ленты чата (шаг 4.5b, ADR-031).
//
// Запись plugin_draft в ленте несёт только slug; данные (сводку и предпросмотр)
// грузим по REST. Кэш общий на slug: N карточек одного slug — один запрос
// предпросмотра, список черновиков владельца — один запрос на всех (карточки
// одного чата/владельца делят его). Обновление — по событию plugin_draft_changed:
// одна глобальная подписка, дедуп по slug, без дублирования запросов.

import { useEffect, useState } from 'react';
import { api, type PluginDraftPreview, type PluginDraftSummary } from '../../../lib/api';
import { onMessage } from '../../../lib/signalr';

export interface DraftEntry {
  // Сводка из списка (GET /drafts): null — slug не в списке = 404 (чужой/удалённый)
  summary: PluginDraftSummary | null;
  // Предпросмотр (GET /drafts/{slug}/preview)
  preview: PluginDraftPreview | null;
  // Идёт загрузка предпросмотра
  loading: boolean;
  // Ошибка предпросмотра (403/404/409) — понятным текстом
  error: string | null;
  // Монотонная ревизия: +1 на каждое plugin_draft_changed по этому slug
  revision: number;
  // Список черновиков владельца прочитан: true — можно отличить 404 (summary null) от «ещё грузим»
  listResolved: boolean;
}

const EMPTY: DraftEntry = { summary: null, preview: null, loading: true, error: null, revision: 0, listResolved: false };

const _entries = new Map<string, DraftEntry>();
// Список черновиков владельца загружен хотя бы раз: true — можно отличить 404 от «ещё грузим»
let _listLoaded = false;
let _listInflight = false;
// «Список грязный»: refreshDraft во время идущей loadList → перечитать после завершения
let _listDirty = false;
// Поколение списка (минор 5): _listGen +1 на каждый старт (пере)загрузки; _listSettledGen —
// поколение, на котором список последний раз стабилизировался (идущей загрузки нет,
// перечитываний не ждёт). waitListSettled(gen) разрешается, когда _listSettledGen >= gen —
// refreshDraft дожидается именно СВЕЖЕГО списка, а не устаревшего снимка.
let _listGen = 0;
let _listSettledGen = 0;
const _listWaiters = new Map<number, Set<() => void>>();
// Дождаться, пока список не стабилизируется на поколении >= fromGen (все (пере)загрузки,
// стартовавшие после fromGen, завершены). Если уже стабилен — сразу.
function waitListSettled(fromGen: number): Promise<void> {
  if (_listSettledGen >= fromGen) return Promise.resolve();
  return new Promise<void>(resolve => {
    let set = _listWaiters.get(fromGen);
    if (!set) { set = new Set(); _listWaiters.set(fromGen, set); }
    set.add(resolve);
  });
}
// Список стабилизировался на поколении gen: будим всех, кто ждёт поколение <= gen.
function settleListAt(gen: number): void {
  _listSettledGen = gen;
  for (const [g, set] of _listWaiters) {
    if (g <= gen) {
      for (const r of set) r();
      _listWaiters.delete(g);
    }
  }
}
const _listeners = new Set<() => void>();

function emit() { _listeners.forEach(fn => fn()); }

export function subscribeDrafts(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Снимок записи (стабильная ссылка; меняется только при замене в мапе).
// Хук useDraftEntry копирует его в useState + подписка (не useSyncExternalStore —
// чтобы асинхронные обновления попадали в act() в тестах).
export function getDraftSnapshot(slug: string): DraftEntry {
  return _entries.get(slug) ?? EMPTY;
}

function put(slug: string, patch: Partial<DraftEntry>): void {
  const cur = _entries.get(slug) ?? EMPTY;
  _entries.set(slug, { ...cur, ...patch });
  emit();
}

// «Свежесть» списка: _listFresh — промис текущей (пере)загрузки; null — список стабилен
// (идущей загрузки нет и перечитываний не ждёт). refreshDraft дожидается именно его, чтобы
// решить про preview по СВЕЖЕМУ списку, а не по устаревшему снимку (минор 5: refresh во время
// идущей loadList раньше получал неразрешённый промис и запрашивал preview отклонённого).
let _listFresh: Promise<void> | null = null;

// Список черновиков владельца: один запрос на всех карточек (они одного владельца).
// Найденные slug получают сводку; не найденные — null (404).
// Возвращает promise текущей загрузки; refreshDraft ждёт _listFresh (см. выше).
function loadList(): Promise<void> {
  if (_listLoaded || _listInflight) return _listFresh ?? Promise.resolve();
  _listInflight = true;
  _listGen++;
  const myGen = _listGen;
  const p = api.plugins.draftsList()
    .then(list => {
      const bySlug = new Map(list.map(s => [s.slug, s]));
      for (const slug of _entries.keys()) put(slug, { summary: bySlug.get(slug) ?? null, listResolved: true });
      _listLoaded = true;
      emit();
      // refreshDraft/событие во время идущей загрузки: перечитать, чтобы не потерять обновление.
      // _listInflight сбрасываем ДО ре-читы: иначе рекурсивный loadList() упрётся в гард
      // (в .then флаг ещё true). .finally не используем — он сбрасывал бы _listInflight уже
      // после старта ре-читы и затирал бы её флаг.
      if (_listDirty) {
        _listDirty = false; _listLoaded = false; _listInflight = false;
        // Перечитываем: _listFresh теперь ведёт на НОВУЮ загрузку (новое поколение) —
        // refreshDraft, дождавшись её, увидит свежую сводку, а не завершившуюся текущую.
        _listFresh = loadList();
      } else {
        _listInflight = false;
        _listFresh = null; // список стабилен
        settleListAt(myGen);
      }
    })
    // Ошибка списка: _listLoaded = false (а не true) — иначе любая последующая попытка
    // (монтирование/бамп/refreshDraft) блокируется гардом и карточки залипают в «Загружаем…».
    // Ретрай только по новому триггеру, не по таймеру — шторма запросов при постоянной ошибке нет.
    .catch(() => { _listLoaded = false; _listInflight = false; _listFresh = null; settleListAt(myGen); emit(); });
  _listFresh = p;
  return p;
}

// Предпросмотр по slug: один запрос на slug (карточки одного slug делят результат).
// force — перечитать (после события), игнорируя уже загруженный предпросмотр.
// Счётчик поколения: устаревший ответ (от предыдущего force) не затирает свежий.
const _gen = new Map<string, number>();
function loadPreview(slug: string, force = false): void {
  const cur = _entries.get(slug);
  if (!force && cur !== undefined && (cur.preview || cur.loading)) return;
  const gen = (_gen.get(slug) ?? 0) + 1;
  _gen.set(slug, gen);
  put(slug, { loading: true, error: null });
  api.plugins.draftPreview(slug)
    .then(p => { if (_gen.get(slug) === gen) put(slug, { preview: p, loading: false }); })
    .catch((e: unknown) => { if (_gen.get(slug) === gen) put(slug, { error: e instanceof Error ? e.message : 'Не удалось загрузить черновик', loading: false }); });
}

// Событие наблюдателя: черновик по slug поменялся → перечитать предпросмотр и список.
// Как refreshDraft: сбрасываем _listLoaded (иначе void loadList() упрёт в гард, если список
// УЖЕ загружен — обновление по событию потеряется), а если список ещё грузится — ставим
// _listDirty (перечитаем после завершения идущей загрузки).
// Экспортируем для тестов (путь события): в проде bump зовёт только onMessage-подписка.
export function bump(slug: string): void {
  const cur = _entries.get(slug) ?? EMPTY;
  put(slug, { preview: null, loading: true, revision: cur.revision + 1 });
  void loadPreview(slug, true);
  _listLoaded = false;
  if (_listInflight) _listDirty = true;
  void loadList();
}

// Одна глобальная подписка на plugin_draft_changed (не по карточке, а на slug)
let _subscribed = false;
function ensureSubscribed(): void {
  if (_subscribed) return;
  _subscribed = true;
  onMessage(msg => { if (msg.type === 'plugin_draft_changed') bump(msg.slug); });
}

// Перечитать черновик (после действий «Принять/Отклонить/Вернуть»): предпросмотр
// принудительно, список — заново. Ревизию не трогаем — подсветка «обновлён» не мигает.
// Дожидаемся СВЕЖЕГО списка (поколение, см. loadList): если loadList уже идёт, loadList()
// сразу разрешится, но сводка ещё устаревшая — preview отклонённого всё же ушёл бы (404).
// Поэтому фиксируем текущее поколение и ждём, пока список не стабилизируется на нём (все
// перечитывания завершены); только по актуальной сводке решаем про preview.
export async function refreshDraft(slug: string): Promise<void> {
  const cur = _entries.get(slug) ?? EMPTY;
  put(slug, { preview: null, loading: true, revision: cur.revision });
  _listLoaded = false;
  if (_listInflight) _listDirty = true;
  await loadList(); // стартует новую загрузку (или возвращает идущую); после неё _listGen актуален
  // fromGen — ПОСЛЕ loadList: если loadList стартовал новую загрузку, поколение уже увеличено,
  // и waitListSettled(fromGen) дождётся именно её (а не устаревшее поколение).
  const fromGen = _listGen;
  await waitListSettled(fromGen);
  const summary = _entries.get(slug)?.summary;
  if (!summary?.rejected) void loadPreview(slug, true);
}

// Хук: запись черновика по slug. Запускает общую загрузку (список + предпросмотр)
// и подписку на события; данные делят все карточки одного slug. Снимок копируется в
// React state (useState + подписка): так асинхронные обновления попадают в act() в тестах.
export function useDraftEntry(slug: string): DraftEntry {
  const [entry, setEntry] = useState<DraftEntry>(() => getDraftSnapshot(slug));
  useEffect(() => {
    ensureSubscribed();
    void loadList();
    void loadPreview(slug);
    return subscribeDrafts(() => setEntry(getDraftSnapshot(slug)));
  }, [slug]);
  return entry;
}

// Сброс кэша (тесты): записи, флаг списка, вполёте, поколения, «свежесть», подписка
export function __resetDraftsStore(): void {
  _entries.clear();
  _gen.clear();
  _listLoaded = false;
  _listInflight = false;
  _listDirty = false;
  _listGen = 0;
  _listSettledGen = 0;
  _listWaiters.clear();
  _listFresh = null;
  _subscribed = false;
  emit();
}
