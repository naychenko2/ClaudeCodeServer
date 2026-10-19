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
const _listeners = new Set<() => void>();

function emit() { _listeners.forEach(fn => fn()); }

export function subscribeDrafts(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Снимок для useSyncExternalStore: стабильная ссылка, меняется только при замене записи
export function getDraftSnapshot(slug: string): DraftEntry {
  return _entries.get(slug) ?? EMPTY;
}

function put(slug: string, patch: Partial<DraftEntry>): void {
  const cur = _entries.get(slug) ?? EMPTY;
  _entries.set(slug, { ...cur, ...patch });
  emit();
}

// Список черновиков владельца: один запрос на всех карточек (они одного владельца).
// Найденные slug получают сводку; не найденные — null (404).
function loadList(): void {
  if (_listLoaded || _listInflight) return;
  _listInflight = true;
  api.plugins.draftsList()
    .then(list => {
      const bySlug = new Map(list.map(s => [s.slug, s]));
      for (const slug of _entries.keys()) put(slug, { summary: bySlug.get(slug) ?? null, listResolved: true });
      _listLoaded = true;
      emit();
    })
    .catch(() => { _listLoaded = true; emit(); })
    .finally(() => { _listInflight = false; });
}

// Предпросмотр по slug: один запрос на slug (карточки одного slug делят результат).
// force — перечитать (после события), игнорируя уже загруженный предпросмотр.
function loadPreview(slug: string, force = false): void {
  // Гвард: запись уже в мапе и загрузка/предпросмотр уже есть → не дублируем.
  // EMPTY (fallback при отсутствии записи) НЕ считается: его loading:true — «ещё не начинали».
  const cur = _entries.get(slug);
  if (!force && cur !== undefined && (cur.preview || cur.loading)) return;
  put(slug, { loading: true, error: null });
  api.plugins.draftPreview(slug)
    .then(p => put(slug, { preview: p, loading: false }))
    .catch((e: unknown) => put(slug, { error: e instanceof Error ? e.message : 'Не удалось загрузить черновик', loading: false }));
}

// Событие наблюдателя: черновик по slug поменялся → перечитать предпросмотр и список.
function bump(slug: string): void {
  const cur = _entries.get(slug) ?? EMPTY;
  put(slug, { preview: null, loading: true, revision: cur.revision + 1 });
  void loadPreview(slug, true);
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
export function refreshDraft(slug: string): void {
  const cur = _entries.get(slug) ?? EMPTY;
  put(slug, { preview: null, loading: true, revision: cur.revision });
  void loadPreview(slug, true);
  _listLoaded = false;
  void loadList();
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

// Сброс кэша (тесты): записи, флаг списка, вполёте, подписка
export function __resetDraftsStore(): void {
  _entries.clear();
  _listLoaded = false;
  _listInflight = false;
  _subscribed = false;
  emit();
}
