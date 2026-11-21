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
// Последний ответ по ушедшему проекту живёт в кэше: возврат в проект сразу
// рисует его (без EMPTY-кадра), а свежий запрос тихо заменяет его в фоне.
//
// Живой режим: подписка на `task_changed` (тот же канал, что в lib/tasks.ts):
// событие (задача с featureId или role=feature) → перезапрос затронутого проекта
// с дебаунсом 1,5 с. Событие игнорируем, если проект не «активен» (никто его не смотрит).
//
// Лукап featureOf(sessionId): фичи, к которым принадлежит чат (по chats всех
// загруженных срезов), с ролью чата в каждой; дубли по featureId сняты.
//
// Ребёнок (getMeSnapshot().isChild) не зовёт api.tasks.featuresIndex ни одним
// путём: индекс фич — взрослая ручка без [ChildAllowed] (дефект cc43582b: на ленте
// урока ребёнку был 403). Гейт в точках запроса — scheduleRefetch и обе ветки
// 0→1 (К9c); у ребёнка индекс пустой, featureOf → [], индикатор не взводится.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage } from './signalr';
import { getTaskById } from './tasks';
import { getMeSnapshot } from './defaultPersona';
import type { FeaturesIndex, FeatureIndexFeature, FeatureChatLink } from '../types/featureIndex';
import type { ServerMessage } from '../types';

// Пустой индекс — стабильная ссылка, чтобы snapshotFor не плодил новые объекты.
const EMPTY: FeaturesIndex = { features: [], chats: {} };

// Срезы по проекту: ключ — projectId, '' — область вне проектов (без projectId).
// Только АКТИВНЫЕ проекты (есть потребитель) держат живой срез: их читает
// snapshotFor (приоритетом) и featureOf — плашка фичи не появляется в чатах
// проектов, которых сейчас не смотрят. Ушедший последний потребитель оставляет
// в Map метку EMPTY на СВОЁМ месте (FI-3): ключ не пересоздаётся — порядок
// обхода featureOf не зависит от повторных входов; сам срез живёт в кэше.
let _indexByProject = new Map<string, FeaturesIndex>();
// Кэш последнего полученного среза по проекту: переживает уход потребителей.
// Возврат в уже открытый проект сразу рисует прошлый срез (без EMPTY-кадра),
// свежий ответ тихо заменяет его. snapshotFor читает его как фолбэк — ссылка
// та же, что была живой. Гасится при смене владельца (resetFeaturesIndexOwner).
let _cache = new Map<string, FeaturesIndex>();
// Поколение запросов ПО КЛЮЧУ ПРОЕКТА: каждый запрос (первичный, фоновый,
// live-перезапрос) берёт следующий номер; ответ применяется, только если его
// номер ещё последний для этого проекта. Иначе старый ответ, пришедший
// последним, затёр бы свежий.
const _seq = new Map<string, number>();
// Моноотонный счётчик поколений (НЕ обнуляется при смене владельца): после
// сброса _seq новый запрос любого проекта получает номер больше любого
// уже отпущенного, поэтому поздний ответ прежнего владельца отбивается даже
// когда новый владелец уже начал свой запрос по тому же проекту.
let _seqGen = 0;
// Загрузка: проекты, по которым fetch в полёте (индикатор в UI).
const _loading = new Set<string>();
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

// «Активные» проекты — у них смонтирован хотя бы один потребитель
// useFeaturesIndex. Только их task_changed перезапрашивает; чужие (неактивные) не
// трогаем. Счёт — по потребителям (R3, минор 5): срез уходит в кэш, а проект
// снимается с «активных», только когда уходит ПОСЛЕДНИЙ потребитель.
const _activeCount = new Map<string, number>();
// Дебаунс-таймеры по проекту: серия task_changed → один перезапрос.
const _pending = new Map<string, ReturnType<typeof setTimeout>>();
let _realtimeWired = false;

// Живой срез проекта: тот же объект пишем и в кэш — последний полученный ответ
// должен пережить уход потребителей (возврат без EMPTY-кадра).
function setIndex(projectKey: string, index: FeaturesIndex) {
  _indexByProject = new Map(_indexByProject);
  _indexByProject.set(projectKey, index);
  _cache.set(projectKey, index);
  emit();
}

// Сброс всех срезов (Map.clear) — живых и кэшированных: после сброса ни живой,
// ни кэш-фолбэк не должен держать старый ответ.
function clearIndex() {
  _indexByProject = new Map();
  _cache = new Map();
  emit();
}

function subscribeIndex(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Срез по проекту — стабильная ссылка (тот же объект, пока не пришёл новый fetch
// для этого проекта): живой срез, иначе — кэш последнего ответа (тот же объект,
// что был живым), иначе EMPTY — если по проекту ещё не было успешного ответа.
// Метка неактивного проекта (EMPTY в живом Map, FI-3) не ответ — за ней тот же
// кэш, что был живым; возврат в проект не должен давать EMPTY-кадр.
function snapshotFor(projectKey: string): FeaturesIndex {
  const live = _indexByProject.get(projectKey);
  if (live !== undefined && live !== EMPTY) return live;
  return _cache.get(projectKey) ?? EMPTY;
}

// Есть ли срез проекта: живой или в кэше. Признак готовности для UI:
// «фич нет» (пустой ответ) отличается от «ещё не загружено».
function hasSliceFor(projectKey: string): boolean {
  return _indexByProject.has(projectKey) || _cache.has(projectKey);
}

// Номер поколения для нового запроса проекта: из общего моноотонного счётчика
// (не из текущего числа этого ключа) — после смены владельца и сброса _seq он
// всё равно больше номера любого запроса, ушедшего раньше.
function nextSeq(projectKey: string): number {
  const s = ++_seqGen;
  _seq.set(projectKey, s);
  return s;
}

// Сброс состояния владельца (линия OWN, см. вызовы в App.tsx: logout и
// cc-unauthorized): кэш и срезы прежнего владельца не должны пережить смену —
// следующий вход до своего ответа обязан быть пустым («ещё не загружено»),
// а не срезом чужого человека. Поколения (номера в _seq) гасим, счётчик —
// нет: поздний ответ прежнего владельца отбивается по номеру (см. _seqGen).
// Подписки и wiring realtime не трогаем: это канал соединения, не данные
// владельца; при выходе компоненты отписываются сами.
export function resetFeaturesIndexOwner(): void {
  _indexByProject = new Map();
  _cache = new Map();
  _seq.clear();
  _loading.clear();
  _activeCount.clear();
  _pending.forEach(t => clearTimeout(t));
  _pending.clear();
  emit();
}

// Успешный ответ: в живой срез — только если проект активен (есть потребитель);
// в кэш — всегда (ответ последний — иначе вызывающий не применяет).
function applyFresh(projectKey: string, index: FeaturesIndex) {
  if ((_activeCount.get(projectKey) ?? 0) > 0) {
    setIndex(projectKey, index);
  } else {
    // Потребителей нет: в _indexByProject не пишем, но кэш обновляем и будим
    // подписчиков useFeaturesIndexReady (состояние «не загружено» → «готово»).
    _cache.set(projectKey, index);
    emit();
  }
}

// Возврат в проект: кэшированный срез становится живым — тот же объект, без копий.
function moveCacheToLive(projectKey: string) {
  const cached = _cache.get(projectKey);
  if (!cached) return;
  _indexByProject = new Map(_indexByProject);
  _indexByProject.set(projectKey, cached);
  // snapshotFor отдаёт тот же объект; будим ради featureOf: чаты проекта
  // снова должны давать плашку.
  emit();
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
// с ролью чата в каждой. Порядок — порядок ключей в сторе: он стабилен, потому
// что уход потребителя не удаляет ключ, а оставляет метку на своём месте
// (FI-3); дубли по featureId сняты (чат одного проекта может быть виден и в
// глобальном срезе).
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
  // Ребёнок не опрашивает индекс фич: GET /api/tasks/features/index — взрослая
  // ручка без [ChildAllowed] (дефект cc43582b). Гейт в единственной точке
  // live-перезапроса: к scheduleRefetch ведёт только handleTaskChanged —
  // дальше него ни один перезапрос не уйдёт (К9c).
  if (getMeSnapshot().isChild) return;
  if (_pending.has(projectKey)) return;
  _pending.set(projectKey, setTimeout(() => {
    _pending.delete(projectKey);
    // Тот же счётчик поколений, что у первичного/фонового запроса: ответ
    // применяется, только если он ещё последний для проекта.
    const seq = nextSeq(projectKey);
    void api.tasks.featuresIndex(projectKey || undefined)
      .then(index => { if (_seq.get(projectKey) === seq) applyFresh(projectKey, index); })
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
  if (msg.type !== 'task_changed') return null;
  const t = msg.task;
  if (t.role === FEATURE_ROLE) return t.projectId ?? '';
  if (t.featureId) {
    // Фолбэк на t.projectId: если эпика нет в сторе задач, проект берём
    // из самой дочерней задачи (m1); '' — область вне проектов.
    return getTaskById(t.featureId)?.projectId ?? t.projectId ?? '';
  }
  return null;
}

// Обработчик task_changed. Затронутый проект не «активен» (никто его не
// смотрит) — событие проигнорировать.
function handleTaskChanged(msg: ServerMessage) {
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
// смотрит) и переносит срез из живого в кэш: его больше никто не смотрит
// (featureOf его не видит), но возврат в проект сразу рисует его, без EMPTY-кадра.
// Пока другие потребители смотрят проект, срез и живая подписка остаются.
// Вынесено в функцию — cleanup хука её вызывает, а тест проверяет эффект
// напрямую (окружение node, без jsdom).
function onUnmount(projectKey: string) {
  const n = _activeCount.get(projectKey) ?? 0;
  if (n > 1) {
    _activeCount.set(projectKey, n - 1);
    return;
  }
  _activeCount.delete(projectKey);
  const t = _pending.get(projectKey);
  if (t) { clearTimeout(t); _pending.delete(projectKey); }
  const live = _indexByProject.get(projectKey);
  if (live) {
    // FI-3: не delete, а метка EMPTY на своём месте: срез — в кэш, ключ живого
    // Map остаётся — порядок обхода featureOf не зависит от повторного входа
    // (delete+set переставлял ключ в конец, и плашки одного чата меняли порядок).
    _indexByProject = new Map(_indexByProject);
    _indexByProject.set(projectKey, EMPTY);
    _cache.set(projectKey, live);
    emit();
  }
}

// Хук: индекс фич проекта.
//   `projectId` — область ответа; null — все проекты владельца и чаты вне проектов.
// Запрос идемпотентен по projectId: повторный fetch — только при смене проекта;
// поколение запроса (_seq по ключу проекта) не даёт позднему ответу затереть свежий.
//
// Потребители считаются по проекту (R3, минор 5): срез уходит в кэш, а проект
// снимается с «активных», только когда уходит последний потребитель.
// Возврат в проект с кэшированным срезом: срез сразу живой (тот же объект),
// обновление — в фоне, без индикатора загрузки.
export function useFeaturesIndex(projectId: string | null): FeaturesIndex {
  const projectKey = projectId ?? '';
  const index = useSyncExternalStore(
    subscribeIndex,
    () => snapshotFor(projectKey),
    () => snapshotFor(projectKey),
  );
  useEffect(() => {
    wireRealtime();
    const prev = _activeCount.get(projectKey) ?? 0;
    _activeCount.set(projectKey, prev + 1);
    // Начало загрузки — только когда первый потребитель приходит
    // (0→1): один запрос на проект, а не на каждую карточку.
    // Ребёнок не стучится в индекс (тот же гейт, что в scheduleRefetch, дефект
    // cc43582b): обе ветки 0→1 — фоновый перезапрос поверх кэша и первичная
    // загрузка с индикатором — закрыты до nextSeq/setLoading; индекс ребёнка
    // остаётся пустым, индикатор не взводится (К9c).
    if (prev === 0 && !getMeSnapshot().isChild) {
      const seq = nextSeq(projectKey);
      if (_cache.has(projectKey)) {
        // Срез прошнего визита сразу живой; фоновый перезапрос тихо обновит его.
        // Сбой фонового — срез не трогаем (он и так показан).
        moveCacheToLive(projectKey);
        void api.tasks.featuresIndex(projectKey || undefined)
          .then(index => { if (_seq.get(projectKey) === seq) applyFresh(projectKey, index); })
          .catch(() => {});
      } else {
        // Первичной загрузки нет: с нуля, с индикатором.
        setLoading(projectKey, true);
        void api.tasks.featuresIndex(projectKey || undefined)
          .then(index => {
            // Ответ последний для проекта — применяем: в живой срез, если проект
            // ещё активен, иначе — в кэш (пришёл после ухода, но это свежий ответ).
            if (_seq.get(projectKey) !== seq) return;
            applyFresh(projectKey, index);
          })
          .catch(() => {
            // Сбой первичной: EMPTY и «готово» — показываем «фич нет», а не вечную загрузку.
            if (_seq.get(projectKey) !== seq) return;
            if ((_activeCount.get(projectKey) ?? 0) > 0) setIndex(projectKey, EMPTY);
          })
          .finally(() => {
            if ((_activeCount.get(projectKey) ?? 0) > 0) setLoading(projectKey, false);
          });
      }
    }
    return () => {
      onUnmount(projectKey);
    };
  }, [projectKey]);
  return index;
}

// Хук: готов ли срез проекта — живой или из кэша последнего ответа.
// true и при пустом индексе (фич нет / сбой первичной загрузки): экран отличает
// «фич нет» от «ещё не загружено». Тот же канал подписки, что у useFeaturesIndex.
export function useFeaturesIndexReady(projectId: string | null): boolean {
  const projectKey = projectId ?? '';
  return useSyncExternalStore(
    subscribeIndex,
    () => hasSliceFor(projectKey),
    () => hasSliceFor(projectKey),
  );
}

// Тестовые швы (не публичное API): сброс стора + прямой доступ к внутреннему состоянию.
export const __test = {
  reset() {
    _indexByProject = new Map();
    _cache = new Map();
    _seq.clear();
    _seqGen = 0;
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
  ready: hasSliceFor,
  // Снимок кэша для проверок (копия Map; значения — те же ссылки срезов).
  cache: () => new Map(_cache),
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
