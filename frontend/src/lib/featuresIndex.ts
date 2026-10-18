// Индекс фич (фасет «Фичи» и плашка в чате): источник данных.
//
// Источник правды — БЭК: REST `GET /api/tasks/features/index?projectId=…`
// (контракт «Контракт индекса фич — утверждён (Александр, 18.10)») возвращает
// готовый индекс: строки фич (bucket, rail, chips, now0, ключевой чат) и связи
// «чат × фича» (chats). Фронт НЕ считает порядок, статусы, членство и ключевой
// чат — только показывает то, что отдал бэк.
//
// Стор РАЗДЕЛЁН по проекту (Map<projectId, FeaturesIndex>, '' — область вне
// проектов). Каждый проект держит свой срез: fetch одного проекта не затирает
// другой. Срез — стабильная ссылка (тот же объект, пока по этому проекту не
// пришёл новый fetch), иначе useSyncExternalStore зациклит ререндер.
//
// Живой режим: подписка на `task_changed` (тот же канал, что в lib/tasks.ts):
// событие (задача с featureId или role=feature) → перезапрос затронутого проекта
// с дебаунсом 1,5 с. Событие игнорируем, если флаг features-overview выключен
// (режим «флаг выключен»: стор ничего не грузит и не подписывается) или проект
// не «активен» (никто его не смотрит).
//
// Лукап featureOf(sessionId): фичи, к которым принадлежит чат (по chats всех
// загруженных срезов), с ролью чата в каждой; дубли по featureId сняты.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage } from './signalr';
import { getTaskById } from './tasks';
import { getFlag, FLAGS } from './featureFlags';
import type { FeaturesIndex, FeatureIndexFeature, FeatureChatLink } from '../types/featureIndex';
import type { ServerMessage } from '../types';

// Пустой индекс — стабильная ссылка, чтобы snapshotFor не плодил новые объекты.
const EMPTY: FeaturesIndex = { features: [], chats: {} };

// Срезы по проекту: ключ — projectId, '' — область вне проектов (без projectId).
let _indexByProject = new Map<string, FeaturesIndex>();
// Загрузка: проекты, по которым fetch в полёте (индикатор в UI).
const _loading = new Set<string>();
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

// «Активные» проекты — у них смонтирован хотя бы один потребитель
// useFeaturesIndex(enabled=true). Только их task_changed перезапрашивает; чужие
// (неактивные) не трогаем. Счёт — по потребителям (R3, минор 5): выключенный
// потребитель (enabled=false) срез не трогает и чужих не отписывает; срез чистится,
// а проект снимается с «активных», только когда уходит ПОСЛЕДНИЙ включённый.
const _activeCount = new Map<string, number>();
// Дебаунс-таймеры по проекту: серия task_changed → один перезапрос.
const _pending = new Map<string, ReturnType<typeof setTimeout>>();
let _realtimeWired = false;

function setIndex(projectKey: string, index: FeaturesIndex) {
  _indexByProject = new Map(_indexByProject);
  _indexByProject.set(projectKey, index);
  emit();
}

// Сброс всех срезов (Map.clear).
function clearIndex() {
  _indexByProject = new Map();
  emit();
}

function subscribeIndex(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Срез по проекту — стабильная ссылка (тот же объект, пока не пришёл новый fetch
// для этого проекта). EMPTY — если проекта ещё нет.
function snapshotFor(projectKey: string): FeaturesIndex {
  return _indexByProject.get(projectKey) ?? EMPTY;
}

function setLoading(projectKey: string, loading: boolean) {
  if (loading) _loading.add(projectKey);
  else _loading.delete(projectKey);
  emit();
}

export function isIndexLoading(projectKey: string): boolean {
  return _loading.has(projectKey);
}

// Лукап: фичи, к которым принадлежит чат (по chats всех загруженных срезов),
// с ролью чата в каждой. Порядок — порядок срезов в сторе; дубли по featureId
// сняты (чат одного проекта может быть виден и в глобальном срезе).
export interface FeatureOfEntry {
  feature: FeatureIndexFeature;
  link: FeatureChatLink;
}

const EMPTY_FEATURES_OF: FeatureOfEntry[] = [];

export function featureOf(sessionId: string | null): FeatureOfEntry[] {
  if (!sessionId) return EMPTY_FEATURES_OF;
  const out: FeatureOfEntry[] = [];
  const seen = new Set<string>();
  for (const index of _indexByProject.values()) {
    const links = index.chats[sessionId];
    if (!links) continue;
    for (const link of links) {
      if (seen.has(link.featureId)) continue;
      seen.add(link.featureId);
      const feature = index.features.find(f => f.featureId === link.featureId);
      if (feature) out.push({ feature, link });
    }
  }
  return out;
}

// Дебаунс 1,5 с на проект (контракт Ф1): всплеск task_changed → один перезапрос.
const REFETCH_DEBOUNCE_MS = 1500;
function scheduleRefetch(projectKey: string) {
  if (_pending.has(projectKey)) return;
  _pending.set(projectKey, setTimeout(() => {
    _pending.delete(projectKey);
    void api.tasks.featuresIndex(projectKey || undefined)
      .then(index => setIndex(projectKey, index))
      // Сбой live-перезапроса не затирает свежий срез (в отличие от первичной загрузки).
      .catch(() => {});
  }, REFETCH_DEBOUNCE_MS));
}

// Роль задачи-эпика (бэк TaskItem.Role): именно она отличает «саму фичу» от дочерней.
const FEATURE_ROLE = 'feature' as const;

// Затронутый проект по task_changed:
//   • задача с role='feature' — это сам эпик → его проект (Task.projectId);
//   • задача с featureId — дочерняя → эпик (по id) → его проект.
// Иначе (обычная задача) → null.
function affectedProjectKey(msg: ServerMessage): string | null {
  if (msg.type !== 'task_changed' || msg.action === 'deleted') return null;
  const t = msg.task;
  if (t.role === FEATURE_ROLE) return t.projectId ?? '';
  if (t.featureId) return getTaskById(t.featureId)?.projectId ?? null;
  return null;
}

// Обработчик task_changed. Флаг выключен — событие проигнорировать (стор ничего
// не грузит и не подписывается); затронутый проект не «активен» (никто его не
// смотрит) — тоже.
function handleTaskChanged(msg: ServerMessage) {
  if (!getFlag(FLAGS.featuresOverview)) return;
  const projectKey = affectedProjectKey(msg);
  if (projectKey === null) return;
  if ((_activeCount.get(projectKey) ?? 0) === 0) return;
  scheduleRefetch(projectKey);
}

// Подписка на task_changed (тот же канал, что в lib/tasks.ts) — один раз.
function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(handleTaskChanged);
}

// Размонтирование потребителя проекта: уменьшаем счёт (R3, минор 5). Последний
// ушедший — снимает проект с «активных», отменяет pending-таймер перезапроса
// (M1, минор по Ф1: иначе дебаунс task_changed, поставленный ещё до
// размонтирования, ушёл бы в REST-запрос за проектом, которого никто уже не
// смотрит) и чистит срез (его больше никто не смотрит). Пока другие потребители
// смотрят проект, срез и живая подписка остаются. Вынесено в функцию — cleanup
// хука её вызывает, а тест проверяет эффект напрямую (окружение node, без jsdom).
function onUnmount(projectKey: string) {
  const n = _activeCount.get(projectKey) ?? 0;
  if (n > 1) {
    _activeCount.set(projectKey, n - 1);
    return;
  }
  _activeCount.delete(projectKey);
  const t = _pending.get(projectKey);
  if (t) { clearTimeout(t); _pending.delete(projectKey); }
  if (_indexByProject.has(projectKey)) setIndex(projectKey, EMPTY);
}

// Хук: индекс фич проекта.
//   `enabled` — гейт фич-флага (dark launch): выключен — источник не зовётся,
//     возвращается EMPTY (фасет/плашка не показываются, «Дни» — как раньше);
//   `projectId` — область ответа; null — все проекты владельца и чаты вне проектов.
// Запрос идемпотентен по (projectId, enabled): повторный fetch — только при смене
// проекта или включении флага; cancel-метка не даёт позднему ответу затереть свежий.
//
// Потребители считаются по проекту (R3, минор 5): выключенный потребитель
// (enabled=false) срез не трогает и чужих не отписывает; срез чистится, а проект
// снимается с «активных», только когда уходит последний включённый потребитель.
export function useFeaturesIndex(projectId: string | null, enabled: boolean): FeaturesIndex {
  const projectKey = projectId ?? '';
  const index = useSyncExternalStore(
    subscribeIndex,
    () => snapshotFor(projectKey),
    () => snapshotFor(projectKey),
  );
  useEffect(() => {
    wireRealtime();
    if (!enabled) {
      // Выключенный потребитель: срез не трогает и чужих не отписывает (учёт
      // по проекту; последний ушедший включённый чистит срез в onUnmount).
      return;
    }
    _activeCount.set(projectKey, (_activeCount.get(projectKey) ?? 0) + 1);
    let cancelled = false;
    setLoading(projectKey, true);
    void api.tasks.featuresIndex(projectKey || undefined)
      .then(index => { if (!cancelled) setIndex(projectKey, index); })
      .catch(() => { if (!cancelled) setIndex(projectKey, EMPTY); })
      .finally(() => { if (!cancelled) setLoading(projectKey, false); });
    return () => {
      cancelled = true;
      onUnmount(projectKey);
    };
  }, [projectKey, enabled]);
  return index;
}

// Тестовые швы (не публичное API): сброс стора + прямой доступ к внутреннему состоянию.
export const __test = {
  reset() {
    _indexByProject = new Map();
    _loading.clear();
    _activeCount.clear();
    _pending.forEach(t => clearTimeout(t));
    _pending.clear();
    _listeners.clear();
    _realtimeWired = false;
  },
  setIndex,
  clearIndex,
  snapshotFor,
  setLoading,
  isIndexLoading,
  featureOf,
  handleTaskChanged,
  affectedProjectKey,
  setActive: (k: string) => { _activeCount.set(k, (_activeCount.get(k) ?? 0) + 1); },
  active: () => new Set(_activeCount.keys()),
  pending: () => new Set(_pending.keys()),
  onUnmount,
};
