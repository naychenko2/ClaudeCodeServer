// Состояние «История · 7 дней» (ADR-030): фильтры, страницы с курсором,
// тихое перечитывание первой страницы. Чистый контроллер + useSyncExternalStore
// (паттерн useLocalModelMedia): тест проверяет контроллер без React.
//
// Правила перечитывания (макет §3):
//  - первая страница — при открытии вкладки (со скелетоном), по кнопке «Обновить»
//    и при смене любого фильтра (тихо: старый список держится до ответа);
//  - «Показать ещё» — следующая страница по cursor, дописывается к списку;
//  - live-данные: вкладка передаёт маркер живых завершений (liveCompletionMarker);
//    изменился — тихое перечитывание первой страницы без скелетона.

import { useCallback, useEffect, useState, useSyncExternalStore } from 'react';
import { fetchMediaHistory, type LocalModelMedia, type MediaHistoryPage, type MediaHistoryQuery, type MediaHistoryRecord } from './api';
import { historyPeriodFrom, liveCompletionMarker, type HistoryPeriod } from './mediaFormat';

// Вид в IconSegmented: «Все / Картинки / Звук / Видео»
export type HistoryKind = 'all' | 'image' | 'video' | 'audio';

export interface HistoryFilters {
  kind: HistoryKind;
  period: HistoryPeriod;
  // completed | cancelled | failed | interrupted
  outcome: string | null;
  model: string | null;
  login: string | null;
}

export const DEFAULT_HISTORY_FILTERS: HistoryFilters = { kind: 'all', period: '7d', outcome: null, model: null, login: null };

export interface HistorySnapshot {
  filters: HistoryFilters;
  records: MediaHistoryRecord[];
  // cursor следующей страницы; null — лента до конца
  cursor: string | null;
  // первая страница (скелетон)
  loading: boolean;
  // идёт подгрузка следующей страницы
  loadingMore: boolean;
  // 'list' — ошибка первой страницы (список заменён блоком ошибки);
  // 'more' — ошибка подгрузки (строка «Не удалось подгрузить · Повторить»)
  error: 'list' | 'more' | null;
  counters: MediaHistoryPage['counters'];
}

export interface HistoryController {
  getSnapshot(): HistorySnapshot;
  subscribe(fn: () => void): () => void;
  setFilters(patch: Partial<HistoryFilters>): void;
  resetFilters(): void;
  loadMore(): void;
  // Тихое перечитывание первой страницы (кнопка «Обновить», live-завершения)
  refresh(): void;
  // Повтор после ошибки списка: с состоянием (сбой снова покажет блок ошибки)
  retry(): void;
  // Маркер живых данных: изменился относительно прошлого — refresh()
  onLiveMarker(media: LocalModelMedia | null): void;
  activate(): void;
  deactivate(): void;
  dispose(): void;
}

export function createHistoryController(
  fetchPage: (q: MediaHistoryQuery) => Promise<MediaHistoryPage>,
  now: () => number = () => Date.now(),
): HistoryController {
  let filters: HistoryFilters = { ...DEFAULT_HISTORY_FILTERS };
  let records: MediaHistoryRecord[] = [];
  let cursor: string | null = null;
  let loading = true;
  let loadingMore = false;
  let error: 'list' | 'more' | null = null;
  let counters: MediaHistoryPage['counters'] = { kinds: {}, outcomes: {} };
  // порядковый номер запроса: устаревший ответ (после смены фильтров) не
  // перезаписывает свежие данные
  let seq = 0;
  let lastLiveMarker = '';
  let active = false;
  let disposed = false;

  const subs = new Set<() => void>();
  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is, новый объект
  // на каждый вызов getSnapshot — бесконечный ререндер (React #185)
  let snap: HistorySnapshot = { filters, records, cursor, loading, loadingMore, error, counters };
  const emit = () => {
    const next: HistorySnapshot = { filters, records, cursor, loading, loadingMore, error, counters };
    if (snap.filters !== next.filters || snap.records !== next.records || snap.cursor !== next.cursor ||
        snap.loading !== next.loading || snap.loadingMore !== next.loadingMore ||
        snap.error !== next.error || snap.counters !== next.counters) {
      snap = next;
      subs.forEach(fn => fn());
    }
  };

  const toQuery = (cur: string | null): MediaHistoryQuery => ({
    kind: filters.kind === 'all' ? null : filters.kind,
    model: filters.model,
    outcome: filters.outcome,
    login: filters.login,
    from: historyPeriodFrom(filters.period, now()),
    cursor: cur,
  });

  // Первая страница. silent — без скелетона и без состояния (смена фильтров,
  // «Обновить», live): старый список держится до ответа, сбой тихо (данные не
  // старше 2 с от live-опроса). Non-silent — первичная загрузка и «Повторить»:
  // сбой показывает блок ошибки.
  const fetchFirst = (silent: boolean) => {
    const my = ++seq;
    if (!silent) { loading = true; error = null; }
    emit();
    fetchPage(toQuery(null)).then(page => {
      if (my !== seq || disposed) return;
      records = page.records;
      cursor = page.nextCursor;
      counters = page.counters;
      loading = false;
      error = null;
      emit();
    }).catch(() => {
      if (my !== seq || disposed) return;
      loading = false;
      if (!silent) error = 'list';
      emit();
    });
  };

  const sameFilters = (a: HistoryFilters, b: HistoryFilters) =>
    a.kind === b.kind && a.period === b.period && a.outcome === b.outcome && a.model === b.model && a.login === b.login;

  return {
    getSnapshot() { return snap; },
    subscribe(fn) { subs.add(fn); return () => { subs.delete(fn); }; },

    setFilters(patch) {
      const next = { ...filters, ...patch };
      if (sameFilters(next, filters)) return;
      filters = next;
      fetchFirst(true);
    },

    resetFilters() {
      const next = { ...DEFAULT_HISTORY_FILTERS };
      if (sameFilters(next, filters)) return;
      filters = next;
      fetchFirst(true);
    },

    loadMore() {
      if (!cursor || loading || loadingMore) return;
      const my = ++seq;
      loadingMore = true;
      error = null;
      emit();
      fetchPage(toQuery(cursor)).then(page => {
        if (my !== seq || disposed) return;
        records = [...records, ...page.records];
        cursor = page.nextCursor;
        counters = page.counters;
        loadingMore = false;
        emit();
      }).catch(() => {
        if (my !== seq || disposed) return;
        loadingMore = false;
        error = 'more';
        emit();
      });
    },

    refresh() { fetchFirst(true); },
    retry() { fetchFirst(false); },

    onLiveMarker(media) {
      const marker = liveCompletionMarker(media);
      if (marker === lastLiveMarker) return;
      lastLiveMarker = marker;
      // Пока идёт первичная загрузка — не перечитываем: её ответ и есть свежий
      if (snap.loading) return;
      fetchFirst(true);
    },

    activate() {
      if (active) return;
      active = true;
      disposed = false;
      fetchFirst(false);
    },

    deactivate() { active = false; },

    dispose() { disposed = true; seq++; },
  };
}

// Хук: контроллер живёт, пока вкладка «Медиа» активна (active);
// media — живые данные вкладки (маркер завершений), null — пока не пришли.
export function useMediaHistory(active: boolean, media: LocalModelMedia | null): HistorySnapshot & {
  setFilters: (patch: Partial<HistoryFilters>) => void;
  resetFilters: () => void;
  loadMore: () => void;
  refresh: () => void;
  retry: () => void;
} {
  const [controller] = useState(() => createHistoryController(q => fetchMediaHistory(q)));

  const snap = useSyncExternalStore(
    cb => controller.subscribe(cb),
    () => controller.getSnapshot(),
    () => controller.getSnapshot(),
  );

  const setFilters = useCallback((patch: Partial<HistoryFilters>) => controller.setFilters(patch), [controller]);
  const resetFilters = useCallback(() => controller.resetFilters(), [controller]);
  const loadMore = useCallback(() => controller.loadMore(), [controller]);
  const refresh = useCallback(() => controller.refresh(), [controller]);
  const retry = useCallback(() => controller.retry(), [controller]);

  useEffect(() => {
    if (!active) return;
    controller.activate();
    return () => controller.deactivate();
  }, [active, controller]);

  // Маркер живых завершений: считается из данных вкладки; первый маркер после
  // активации не перечитывает (первичная загрузка уже идёт)
  useEffect(() => {
    if (active) controller.onLiveMarker(media);
  }, [active, media, controller]);

  useEffect(() => () => controller.dispose(), [controller]);

  return { ...snap, setFilters, resetFilters, loadMore, refresh, retry };
}
