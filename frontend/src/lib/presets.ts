// Пресеты-цепочки и настройки специальностей (ADR-007, итерация 2): модульный стор
// ответа GET /api/specialties/settings (оба слоя + объединённый список пресетов)
// и вся лексика "preset:{id}" на фронте: подписи шагов цепочки, сводка цепочки,
// подписи значений в контролах. Паттерн стора — как у lib/modelTiers.ts.
//
// Писатель один — модалка «Поставщики моделей» (оптимистичные сохранения слоёв);
// после каждой записи она обновляет стор (updateSpecialtySettings), чтобы подписи
// пресетов в остальных экранах (форма персоны, таблица мест) не протухали.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { modelLabel, USAGE } from './models';
import { TIER_TITLE } from './modelTiers';
import type { TierKey } from './modelProvidersShared';
import { TIERS, routeTier, routeLabel } from './modelProvidersShared';
import type { ScopedPreset, SpecialtySettingsResponse, SpecialtySettingsLayer, ModelRoutePreset,
  ModelPreviewResponse, PlacePresetRef, SubagentModelChip, ResetResult } from '../types';

// --- Лексика preset:{id} ---

export const PRESET_PREFIX = 'preset:';

export function isPresetRoute(route: string | null | undefined): boolean {
  return !!route && route.trim().toLowerCase().startsWith(PRESET_PREFIX);
}

// id пресета из ссылки "preset:{id}" либо null (не ссылка/пустой id)
export function presetIdOf(route: string | null | undefined): string | null {
  if (!route) return null;
  const r = route.trim();
  if (!r.toLowerCase().startsWith(PRESET_PREFIX)) return null;
  const id = r.slice(PRESET_PREFIX.length).trim();
  return id || null;
}

export function presetRoute(id: string): string {
  return `${PRESET_PREFIX}${id}`;
}

export function findPreset(presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined,
  id: string | null | undefined): ModelRoutePreset | null {
  if (!id) return null;
  return presets?.find(p => p.id.toLowerCase() === id.toLowerCase()) ?? null;
}

// Дефолт бюджета подмен — фолбэк, пока ответ GET /api/specialties/settings не догружен
// (фактический бюджет — useSubstitutionBudget ниже). Должно совпадать с backend
// FallbackSettingsStore.DefaultMaxSubstitutions.
export const FALLBACK_BUDGET_DEFAULT = 4;

// Контекст подписей: эффективные модели слотов и (опционально) локальная модель
export interface ChainLabelContext {
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
}

// Подпись шага цепочки: модель — её имя; уровень — «Сильная (модели по умолч.)»
// (уровни внутри пресета разворачиваются из общих слотов — ADR-007 §1);
// local — «Локальная · …»
export function chainStepLabel(step: string, ctx: ChainLabelContext): string {
  const s = step.trim();
  if (s === 'local') return `Локальная${ctx.ollamaModel ? ` · ${ctx.ollamaModel}` : ''}`;
  const tier = routeTier(s);
  if (tier) return `${TIERS[tier].title} (модели по умолч.)`;
  return modelLabel(s);
}

// Короткая сводка цепочки для карточек: «Opus 5 → GLM-4.7 → DeepSeek»
export function chainSummary(preset: ModelRoutePreset, ctx: ChainLabelContext): string {
  return preset.steps.map(s => chainStepLabel(s, ctx)).join(' → ');
}

// Подпись значения-пресета в контроле: «Рабочая · 3 шага».
// Битая ссылка — честная пометка (место ведёт себя как пустое — fail-open вниз).
export function presetValueLabel(route: string, presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined): string {
  const id = presetIdOf(route);
  const preset = findPreset(presets, id);
  if (!preset) return 'Цепочка удалена — работает настройка по умолчанию';
  return `${preset.name} · ${stepsWord(preset.steps.length)}`;
}

export function stepsWord(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return `${n} шаг`;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return `${n} шага`;
  return `${n} шагов`;
}

// Склонение «раз/раза» для строки о бюджете подмен
export function substitutionsWord(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return `${n} раз`;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return `${n} раза`;
  return `${n} раз`;
}

// Ссылка на пресет, которого больше нет (битая ссылка): место ведёт себя как пустое
export function isBrokenPresetRoute(route: string | null | undefined,
  presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined): boolean {
  const id = presetIdOf(route);
  return !!id && !findPreset(presets, id);
}

// Единая подпись значения контрола выбора: preset-ссылка → имя и длина цепочки,
// остальное — обычная подпись маршрута (слот с моделью / локальная / модель)
export function routeDisplayLabel(route: string | null | undefined,
  presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined, ctx: ChainLabelContext): string {
  const r = route ?? '';
  if (isPresetRoute(r)) return presetValueLabel(r, presets);
  return routeLabel(r, ctx.ollamaModel, ctx.tierModels);
}

// Подпись значения в узкой ячейке таблицы (спека «Исключения»): для пресета — голова
// цепочки «glm-5.2 → sonnet · +2» (первые 2 шага и счётчик остатка), полный состав и
// имя пресета — в title (тултип). Битая ссылка — короткая пометка без длинного пояснения
// (в table-cell пояснение превратится во вторую строку, а title его сохраняет).
// Для непресетов возвращается обычная подпись маршрута без title — это локальная
// альтернатива routeDisplayLabel: не меняем имя и поведение в SlotsTab/PersonaForm,
// где имя пресета в ячейке уместно само по себе.
export function cellPresetLabel(route: string | null | undefined,
  presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined, ctx: ChainLabelContext):
  { label: string; title: string } {
  const r = route ?? '';
  if (!isPresetRoute(r)) return { label: routeLabel(r, ctx.ollamaModel, ctx.tierModels), title: '' };
  const id = presetIdOf(r);
  const preset = findPreset(presets, id);
  if (!preset) {
    return { label: 'Цепочка удалена', title: 'Цепочка удалена — работает настройка по умолчанию' };
  }
  const steps = preset.steps.map(s => chainStepLabel(s, ctx));
  // 1–2 шага — целиком; 3+ — голова + «+N», где N = steps.length − 2
  const head = steps.slice(0, 2);
  const rest = steps.length - head.length;
  const label = rest > 0 ? `${head.join(' → ')} · +${rest}` : head.join(' → ');
  return { label, title: `${preset.name}: ${steps.join(' → ')}` };
}

// Раскрытие пресета места каталога для триггера (вкладка «Применение»). Поле preset
// ответа сильнее: route в нём уже развёрнут в первый шаг цепочки и имени не несёт.
// Без поля (старый ответ /usage) — разбираем route по-старому.
// settingsLoaded — список пресетов догружен: без него «id не найден» неотличимо от
// «ещё грузится», и здоровый пресет мигал бы пометкой «удалён».
export function resolvePlacePreset(route: string | null | undefined,
  info: PlacePresetRef | null | undefined,
  presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined,
  settingsLoaded: boolean): { preset: ModelRoutePreset | null; broken: boolean; presetId: string | null } {
  const presetId = info?.id ?? presetIdOf(route);
  if (!presetId) return { preset: null, broken: false, presetId: null };
  const listed = findPreset(presets, presetId);
  // Битая ссылка: бэкенд вернул name=null либо id не нашёлся в уже загруженном списке
  const broken = info?.name === null || (settingsLoaded && !listed);
  // Список свежее (переименования); пока он грузится — имя и шаги из ответа
  const preset = broken ? null
    : listed ?? (info?.name ? { id: info.id, name: info.name, steps: info.steps } : null);
  return { preset, broken, presetId };
}

// --- Стор настроек специальностей/пресетов ---

let _settings: SpecialtySettingsResponse | null = null;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

export function ensurePresetSettingsLoaded(): Promise<void> {
  if (_settings || _loading) return _loading ?? Promise.resolve();
  _loading = api.specialties.getSettings()
    .then(s => { _settings = s; })
    .catch(() => { /* сервер недоступен — остаёмся без пресетов, группа просто не рисуется */ })
    .finally(() => { _loading = null; emit(); });
  return _loading;
}

// Перечитать с сервера (после внешних изменений). Неразрушающая: _settings НЕ
// обнуляется — иначе на время GET пикеры показали бы «Цепочка удалена — работает
// настройка по умолчанию» (фикс: до того, как новый снимок доедет, подписи держатся
// прежних пресетов; commit делает updateSpecialtySettings).
export function reloadPresetSettings(): void {
  setSettingsError(null);
  void api.specialties.getSettings()
    .then(s => { updateSpecialtySettings(s); })
    .catch(e => { setSettingsError(e instanceof Error ? e.message : 'Не удалось перечитать'); });
}

// Точка записи для модалки «Поставщики моделей»: после оптимистичного сохранения слоя
// подкладываем свежий снимок, чтобы все подписчики (пикеры, формы) увидели правку сразу.
export function updateSpecialtySettings(s: SpecialtySettingsResponse): void {
  _settings = s;
  invalidateEffectiveLines();
  emit();
}

// === Стор записи (write-стор) ===
//
// Поверх read-сторора (settings + userLayers) живёт write-стор: оптимистичная правка +
// PUT + очередь ответов, ключуемая scope+userId. До этого каждая поверхность (вкладки
// модалки, PresetOptions) держала собственные useState/useRef + mergeSavedLayer и
// следила за собственной saveSeqRef — две правки в разных scope+userId могли гонять
// ответы друг друга. Теперь запись живёт здесь, и баннер ошибки тоже здесь (для UI
// модалки), а шесть точек записи просто зовут saveLayer(scope, reducer, userId?) и
// получают промис, который резолвится после реального PUT.

// Контракт записи. Редьюсер вызывается с ТЕКУЩИМ слоем (для user — из userLayers,
// для global/owner — из settings[scope]); если слой ещё не загружен, ему приходит пустой
// шаблон (защита от PUTа поверх пустого base, тот же инвариант, что в ChainsTab/Pure).
// Редьюсер обязан вернуть СЛЕДУЮЩИЙ слой как чистую функцию; фиксы сортируются тут же —
// никаких «забыли зафиксировать» сценариев.
export type LayerReducer = (current: SpecialtySettingsLayer) => SpecialtySettingsLayer;

type WriteScope = 'global' | 'owner' | 'user';

// Ключ очереди ответов: пара scope+userId защищает user-слой двух разных адресатов от
// перетирания ответов друг друга. global/owner — отдельные ключи.
function writeKey(scope: WriteScope, userId?: string | null): string {
  return scope === 'user' ? `user:${userId ?? ''}` : scope;
}

// Счётчики для защиты от out-of-order ответов (последний выигрывает по данным UI).
const _writeSeq: Record<string, number> = {};
// Цепочка ин-флайт промисов на ключ: параллельные записи ждут друг друга, чтобы
// редьюсеры накладывались по порядку, а не «пропускали» друг друга (фикс 65d8df66).
const _writeInFlight = new Map<string, Promise<void>>();
// Что сейчас сохраняется и какая последняя ошибка — для UI (busy на вкладках, баннер).
let _savingKey: string | null = null;
let _settingsError: string | null = null;
// Идёт ли сейчас серверный POST reset — пока флаг горит, ручные правки ждут.
let _resettingScope: WriteScope | null = null;
const _writeListeners = new Set<() => void>();

function writeEmit(): void {
  _writeListeners.forEach(fn => fn());
}
function subscribeWriteQueue(fn: () => void): () => void {
  _writeListeners.add(fn);
  return () => { _writeListeners.delete(fn); };
}
function setSaving(key: string | null): void {
  if (_savingKey !== key) { _savingKey = key; writeEmit(); }
}
function setSettingsError(e: string | null): void {
  if (_settingsError !== e) { _settingsError = e; writeEmit(); }
}
function setResettingScope(scope: WriteScope | null): void {
  if (_resettingScope !== scope) { _resettingScope = scope; writeEmit(); }
}

// Слить свежий ответ сервера в стейт-стор: волна 4 убрала owner/user-слои, остался
// один global; в нём же живёт и список пресетов. Скоуп записи по-прежнему трекаем
// через preset.scope (теперь всегда 'global'), чтобы UI подписи не сломались.
// Параметр scope сохранён в сигнатуре ради обратной совместимости с write-каналом,
// но не используется — запись теперь всегда идёт в global.
function mergeSavedLayer(base: SpecialtySettingsResponse,
  _scope: WriteScope, saved: SpecialtySettingsLayer): SpecialtySettingsResponse {
  const merged: SpecialtySettingsResponse = { ...base, global: saved };
  merged.presets = [
    ...(merged.global?.presets ?? []).map(p => ({ ...p, scope: 'global' as const })),
  ];
  return merged;
}

// Пустой шаблон — на случай user-слоя, который ещё не загружен (редьюсер всё равно
// может собрать свежий слой с нуля, как это делает PresetOptions.savePreset).
function emptyLayer(): SpecialtySettingsLayer {
  return { specialties: {}, defaultSpecialty: null, presets: [] };
}

// Запись слоя: оптимистично применяем reducer к текущему снимку, шлём PUT, по ответу
// фиксируем сохранённый снимок. Параллельные вызовы на одном ключе выстраиваются
// в очередь — редьюсеры накладываются последовательно.
//
// С переходом на единый глобальный слой (f8e7d0e0) — запись всегда идёт в global,
// независимо от переданного scope. Scope/userId сохранены в сигнатуре ради обратной
// совместимости с другими волнами (ModelsSpend/SpecialRulesTab); когда они перейдут
// на «один слой», аргументы станут неактуальными.
//
// ВАЖНО: коллапс на global фиксируется во ВСЕХ трёх точках — applyLocal, applySaved
// и rollbackLocal. Иначе при входном scope='owner' ответ ложился бы в _settings.owner,
// а mergeSavedLayer удваивал бы список пресетов в UI; при scope='user' откат дёргал бы
// удалённый GET /settings/user/{id}.
async function doSave(key: string, scope: WriteScope, reducer: LayerReducer,
  userId: string | null | undefined): Promise<void> {
  void scope;
  const seq = ++_writeSeq[key];
  const base: SpecialtySettingsLayer = _settings
    ? (_settings.global as SpecialtySettingsLayer)
    : emptyLayer();
  const nextLayer = reducer(base);
  applyLocal('global', nextLayer, userId);
  setSaving(key);
  setSettingsError(null);
  const req = api.specialties.saveGlobalLayer(nextLayer).then(res => res.global);
  try {
    const saved = await req;
    if (_writeSeq[key] !== seq) return; // устаревший ответ
    applySaved('global', saved, userId);
  } catch (e) {
    if (_writeSeq[key] !== seq) return;
    setSettingsError(e instanceof Error ? e.message : 'Не удалось сохранить');
    // Откатить оптимистичное обновление: перечитываем серверный снимок общего слоя.
    // Путь неразрушающий — пикеры не показывают «Цепочка удалена» посреди отката.
    rollbackLocal('global', userId);
  } finally {
    if (_writeSeq[key] === seq) setSaving(null);
  }
}

function applyLocal(scope: WriteScope, layer: SpecialtySettingsLayer,
  userId: string | null | undefined): void {
  if (scope === 'user') {
    if (!userId) return;
    _userLayers[userId] = layer;
    _userLayerErrors.delete(userId);
    userLayerEmit();
    return;
  }
  if (!_settings) return;
  _settings = { ..._settings, [scope]: layer };
  invalidateEffectiveLines();
  emit();
}

function applySaved(scope: WriteScope, saved: SpecialtySettingsLayer,
  userId: string | null | undefined): void {
  if (scope === 'user') {
    if (!userId) return;
    commitUserLayer(userId, saved);
    return;
  }
  if (!_settings) return;
  _settings = mergeSavedLayer(_settings, scope, saved);
  invalidateEffectiveLines();
  emit();
}

function rollbackLocal(scope: WriteScope, userId: string | null | undefined): void {
  // Слой один: дотягиваем серверный снимок без обнуления read-стора —
  // reloadPresetSettings внутри делает то же самое, плюс чистит ошибку.
  void scope; void userId;
  void api.specialties.getSettings()
    .then(s => updateSpecialtySettings(s))
    .catch(() => { /* ошибка уже показана */ });
}

// Публикация ошибки (для бэйджей состояния загрузки в модалке)
export function getSettingsError(): string | null {
  return _settingsError;
}

// Запись слоя: единственная точка PUTа из шести поверхностей. reducer получает
// текущий слой, возвращает СЛЕДУЮЩИЙ. Промис резолвится после фиксации ответа
// (важно для PresetOptions.savePreset — назначение места ждёт, иначе бэкенд
// проверяет preset:{id} по снимку без только что созданного пресета → 400).
export function saveLayer(scope: WriteScope, reducer: LayerReducer,
  userId?: string | null): Promise<void> {
  const key = writeKey(scope, userId);
  const prev = _writeInFlight.get(key) ?? Promise.resolve();
  const next = prev.then(() => doSave(key, scope, reducer, userId));
  _writeInFlight.set(key, next);
  next.finally(() => {
    if (_writeInFlight.get(key) === next) _writeInFlight.delete(key);
  });
  return next;
}

// Серверный сброс слоя (POST /specialties/settings/reset/{scope}). После ADR-012 живут
// только 'global' (общий слой) и 'owner' (персоны вызывающего) — reset/user удалён.
// После успешного ответа перечитываем настройки, чтобы пикеры и подписи пресетов
// обновились свежим снимком. Перечитка неразрушающая.
export function resetLayer(scope: 'owner' | 'global', key?: string):
  Promise<ResetResult> {
  setResettingScope(scope);
  return api.specialties.reset(scope, key)
    .then(res => {
      void api.specialties.getSettings().then(s => updateSpecialtySettings(s))
        .catch(e => { setSettingsError(e instanceof Error ? e.message : 'Не удалось перечитать'); });
      return res;
    })
    .finally(() => { setResettingScope(null); });
}

// Реактивный снимок write-сторора для модалки (saving/resetting/error). getSnapshot
// возвращает один и тот же объект, пока ничего не изменилось — иначе useSyncExternalStore
// уходит в бесконечный цикл (новый объект каждый рендер).
interface SaveStateSnapshot {
  savingScope: WriteScope | null;
  savingUserId: string | null;
  settingsError: string | null;
  resettingScope: WriteScope | null;
}
let _cachedSaveState: SaveStateSnapshot = {
  savingScope: null, savingUserId: null, settingsError: null, resettingScope: null,
};
function getSaveStateSnapshot(): SaveStateSnapshot {
  const savingScope: WriteScope | null = _savingKey
    ? (_savingKey.startsWith('user:') ? 'user' : _savingKey as WriteScope)
    : null;
  const savingUserId = savingScope === 'user'
    ? (_savingKey && _savingKey.startsWith('user:') ? _savingKey.slice(5) || null : null)
    : null;
  const next: SaveStateSnapshot = {
    savingScope, savingUserId,
    settingsError: _settingsError, resettingScope: _resettingScope,
  };
  if (next.savingScope !== _cachedSaveState.savingScope
    || next.savingUserId !== _cachedSaveState.savingUserId
    || next.settingsError !== _cachedSaveState.settingsError
    || next.resettingScope !== _cachedSaveState.resettingScope) {
    _cachedSaveState = next;
  }
  return _cachedSaveState;
}

export function useSaveState(): SaveStateSnapshot {
  return useSyncExternalStore(subscribeWriteQueue, getSaveStateSnapshot, getSaveStateSnapshot);
}

// --- Стор user-слоёв (МЁРТВЫЙ КАНАЛ после ADR-012) ---
//
// Слоёв больше нет: GET /specialties/settings/user/{id} удалён с бэкенда, и ни один экран
// сюда больше не заходит (ChainsTab/SlotsTab/PresetOptions переведены на общий слой).
// Канал оставлен только потому, что на нём висят собственные юнит-тесты
// (__tests__/userLayers, presets, presets-user-gate, presets-queue) — он уезжает хвостовой
// чисткой вместе с ними. НОВЫХ вызовов не добавлять: loadUserLayer сходит в 404.
//
// Историческая мотивация (зачем слой грузился отдельно): база для записи в чужой слой
// бралась ТОЛЬКО отсюда — из settings.user поверх пустого fallback'а PUT затирал
// specialties и presets реального пользователя.

const _userLayers: Record<string, SpecialtySettingsLayer> = {};
const _userLayersInflight = new Map<string, Promise<void>>();
// Текст последней ошибки загрузки по userId — отдельным каналом, чтобы UI мог отличить
// «слой ещё не запрашивался» (null) от «сервер ответил отказом» (строка).
const _userLayerErrors = new Map<string, string>();
const _userLayerListeners = new Set<() => void>();

function userLayerEmit() {
  _userLayerListeners.forEach(fn => fn());
}

function subscribeUserLayer(fn: () => void): () => void {
  _userLayerListeners.add(fn);
  return () => { _userLayerListeners.delete(fn); };
}

// Подтянуть слой пользователя с сервера. Идемпотентно: тот же userId возвращает тот же
// ин-флайт промис, повторный заход после загрузки — no-op. null/пустой userId — мгновенный
// no-op (защита от эффектов с ещё не выставленным контекстом и от PUT /.../user/null).
export function loadUserLayer(userId: string | null | undefined): Promise<void> {
  if (!userId) return Promise.resolve();
  if (_userLayers[userId] !== undefined) return Promise.resolve();
  const existing = _userLayersInflight.get(userId);
  if (existing) return existing;
  const p = api.specialties.getUserLayer(userId)
    .then(r => {
      _userLayers[userId] = r.user;
      _userLayerErrors.delete(userId);
    })
    .catch(e => {
      _userLayerErrors.set(userId, e instanceof Error ? e.message : 'Не удалось загрузить слой пользователя');
    })
    .finally(() => {
      _userLayersInflight.delete(userId);
      userLayerEmit();
    });
  _userLayersInflight.set(userId, p);
  return p;
}

// Снимок user-слоя для синхронного чтения. null — слой ещё не загружен (или не запрашивался).
export function getUserLayer(userId: string | null | undefined): SpecialtySettingsLayer | null {
  if (!userId) return null;
  return _userLayers[userId] !== undefined ? _userLayers[userId] : null;
}

// Загружен ли user-слой? Проверка по наличию ключа — даже пустой ответ сервера считается
// загруженным слоем (это и есть способ отличить «нет ключа → не загружен» от «ключ есть →
// пусто, можно класть пресет»).
export function hasUserLayer(userId: string | null | undefined): boolean {
  return !!userId && _userLayers[userId] !== undefined;
}

// Текст ошибки последней загрузки (или null — ошибки нет / слой ещё не грузился)
export function getUserLayerError(userId: string | null | undefined): string | null {
  return userId ? (_userLayerErrors.get(userId) ?? null) : null;
}

// Реактивный user-слой по userId: на маунте/смене userId сам зовёт loadUserLayer, далее
// перерисовывается на userLayerEmit (commit/rollback/ошибка загрузки). null — слой ещё
// не доехал.
export function useUserLayer(userId: string | null | undefined): SpecialtySettingsLayer | null {
  useEffect(() => { void loadUserLayer(userId); }, [userId]);
  return useSyncExternalStore(
    subscribeUserLayer,
    () => userId ? getUserLayer(userId) : null,
    () => null,
  );
}

// Зафиксировать новый снимок user-слоя (после успешного PUT). userId обязан быть непустым
// — иначе URL превратится в /specialties/settings/user/null, чего быть не должно ни при
// каком состоянии UI.
export function commitUserLayer(userId: string, layer: SpecialtySettingsLayer): void {
  if (!userId) return;
  _userLayers[userId] = layer;
  _userLayerErrors.delete(userId);
  userLayerEmit();
  invalidateEffectiveLines();
}

// Откатить user-слой к прежнему снимку (после отказа PUT). prevLayer === undefined —
// ключ удаляется (на случай, если до PUT ключа и не было).
export function rollbackUserLayer(userId: string, prevLayer: SpecialtySettingsLayer | undefined): void {
  if (!userId) return;
  if (prevLayer !== undefined) _userLayers[userId] = prevLayer;
  else delete _userLayers[userId];
  userLayerEmit();
}

export function getSpecialtySettings(): SpecialtySettingsResponse | null {
  return _settings;
}

// Реактивный ответ настроек (null — ещё грузится или недоступен)
export function useSpecialtySettings(): SpecialtySettingsResponse | null {
  useEffect(() => { void ensurePresetSettingsLoaded(); }, []);
  return useSyncExternalStore(subscribe, getSpecialtySettings, getSpecialtySettings);
}

// Реактивный объединённый список пресетов (личные впереди, затем общие)
const EMPTY_PRESETS: ScopedPreset[] = [];

export function usePresets(): ScopedPreset[] {
  const s = useSpecialtySettings();
  // Константа, а не `?? []`: getSnapshot обязан возвращать кэшированное значение,
  // иначе useSyncExternalStore ловит «новый» снапшот на каждом рендере
  return s?.presets ?? EMPTY_PRESETS;
}

// Эффективный бюджет подмен цепочки (сколько смен модели успевает ход): приходит
// в GET /api/specialties/settings (maxSubstitutions), до загрузки — дефолт бэкенда.
// Работают шаги 1..budget+1 (первый + подмены); дальше — «обычно не используется».
export function useSubstitutionBudget(): number {
  const s = useSpecialtySettings();
  const v = s?.maxSubstitutions;
  return v && v >= 1 && v <= 5 ? v : FALLBACK_BUDGET_DEFAULT;
}

// Приглушение шага цепочки за пределом бюджета подмен (спека, блок 3): шаг 1 —
// базовая модель, подмены budget штук → рабочие шаги 1..budget+1. index — 0-based.
// Совпадает со счётчиком FallbackLlmSessionAdapter (chainIndex=0 — первый шаг,
// подмены ведут на шаги 2..budget+1).
export function isChainStepDimmed(index: number, budget: number): boolean {
  return index > budget;
}

// --- «Сейчас пойдёт» и наследование пустых ячеек ---
// Источник — серверный резолв GET /api/models/preview (та же кодовая дорога, что
// запуск хода, — второй точки истины на фронте нет, ADR-007 §5 п.5). Кэшируем СЫРОЙ
// ответ: проекции (строка-итог, плейсхолдер пустой ячейки) считаются при рендере.

// Место показа: ячейка специальности / персона / место каталога / место с уровнем
// (плейсхолдеры пустых ячеек). Слотов нет сознательно: их строка совпала бы с
// собственной подписью слота, а у админа в общем контексте превью врало бы.
export interface EffectiveLineContext {
  kind: 'specialty' | 'persona' | 'action';
  tier?: TierKey;             // уровень строки (ячейки)
  specialtyKey?: string;      // ключ специальности
  personaId?: string;         // персона (без id — строки нет: нечего резолвить)
  actionKey?: string;         // место каталога («Кто что выполняет»)
}

// Query preview-эндпоинта. sessionId вместе с personaId просит у бэкенда subagentChip —
// готовый чип модели для карточки персоны-сабагента (спека «Чип модели…»).
export interface PreviewQuery {
  place?: string;
  personaId?: string;
  specialty?: string;
  tier?: string;
  sessionId?: string;
}

// Контекст → query preview-эндпоинта. null — резолвить нечего (например, персона
// ещё не создана). Персоне и специальности подставляем место chat-persona: без place
// превью при пустых матрицах/слотах возвращало Empty и строка не рисовалась вовсе
// (дефект приёмки №2) — место добирает ответ своим назначением.
function toQuery(ctx: EffectiveLineContext): PreviewQuery | null {
  switch (ctx.kind) {
    case 'persona':
      return ctx.personaId ? { personaId: ctx.personaId, place: USAGE.chatPersona } : null;
    case 'action':
      return ctx.actionKey ? { place: ctx.actionKey } : null;
    case 'specialty':
      return ctx.specialtyKey
        ? { specialty: ctx.specialtyKey, tier: ctx.tier, place: USAGE.chatPersona }
        : null;
  }
}

function queryKey(q: PreviewQuery): string {
  return [q.place ?? '', q.personaId ?? '', q.specialty ?? '', q.tier ?? '', q.sessionId ?? ''].join('|');
}

// Форматирование строки-итога (спека, блок 8):
//   «Сейчас пойдёт: Sonnet 5 · уровень «средняя» у персоны, модель — от специальности»
// Битая ссылка пресета: «Сейчас пойдёт: модель по умолчанию — пресет «…» удалён».
// null — строку показывать нечего (пустой резолв).
// opts.tierText — готовая подпись уровня вместо разбора tierOrigin: в ячейке
// специальности уровень — это сама строка, а не «задан задачей» (overrideTier
// эндпоинта), поэтому там tierOrigin не показываем.
// opts.prefix — префикс перед строкой. По умолчанию «Сейчас пойдёт: ». Для случаев,
// когда префикс должен отличаться (например, «Наследуется: » при пустой ячейке),
// передают явно. Битый пресет всегда идёт под «Сейчас пойдёт: », это его семантика.
export function formatEffectiveLine(d: ModelPreviewResponse, opts?: { tierText?: string; prefix?: string }): string | null {
  const prefix = opts?.prefix ?? 'Сейчас пойдёт: ';
  if (d.preset?.broken) {
    return `Сейчас пойдёт: модель по умолчанию — цепочка${d.preset.name ? ` «${d.preset.name}»` : ''} удалена`;
  }
  if (!d.model) return null;
  const parts: string[] = [];
  if (opts?.tierText) {
    parts.push(opts.tierText);
  } else if (d.tier && d.tierOrigin) {
    const origin: Record<string, string> = {
      task: 'задан задачей', persona: 'у персоны', specialty: 'у специальности', place: 'у места',
    };
    parts.push(`уровень «${TIER_TITLE[d.tier]}»${origin[d.tierOrigin] ? ` ${origin[d.tierOrigin]}` : ''}`);
  }
  const source: Record<string, string> = {
    'persona-model': 'модель — из персоны',
    'persona-cell': 'модель — из ячейки персоны',
    'specialty-cell': 'модель — от специальности',
    'owner-slot': 'модель — из ваших «Моделей по умолчанию»',
    'instance-slot': 'модель — из общих «Моделей по умолчанию»',
    'place-assignment': 'модель — из назначения места',
    'explicit': 'модель задана явно',
  };
  if (d.source && source[d.source]) parts.push(source[d.source]);
  const suffix = parts.length > 0 ? ` · ${parts.join(', ')}` : '';
  return `${prefix}${modelLabel(d.model)}${suffix}`;
}

// Кэш сырых ответов превью: значение null — запрос завершился ошибкой
const _previewCache = new Map<string, ModelPreviewResponse | null>();
const _previewInflight = new Set<string>();
const _previewListeners = new Set<() => void>();
// Монотонный счётчик тиков превью — для usePreviewTick: getSnapshot обязан
// возвращать меняющееся значение, иначе useSyncExternalStore не пошлёт обновление
// подписчикам. Источник — единственный (previewEmit), других каналов нет.
let _previewTick = 0;

function previewEmit() {
  _previewTick++;
  _previewListeners.forEach(fn => fn());
}

function ensurePreview(q: PreviewQuery): void {
  const key = queryKey(q);
  if (_previewCache.has(key) || _previewInflight.has(key)) return;
  _previewInflight.add(key);
  api.models.preview(q)
    .then(d => { _previewCache.set(key, d); })
    .catch(e => {
      _previewCache.set(key, null);
      // Один warn на ключ (ответ закэширован) — иначе «строка не рисуется» не отличить
      // от «резолв пуст», а приёмка уже ловила это вслепую
      console.warn('[models/preview] запрос не удался:', e instanceof Error ? e.message : e);
    })
    .finally(() => { _previewInflight.delete(key); previewEmit(); });
}

// Сброс кэша превью — зовём после записи настроек (слоты/пресеты/ячейки/персоны),
// чтобы строки-итоги пересчитались свежим резолвом
export function invalidateEffectiveLines(): void {
  _previewCache.clear();
  previewEmit();
}

// Сырой ответ превью для контекста; null — грузится / запрос не удался / резолвить нечего
export function usePreview(ctx: EffectiveLineContext): ModelPreviewResponse | null {
  const q = toQuery(ctx);
  const key = q ? queryKey(q) : null;
  useEffect(() => { if (q) ensurePreview(q); });
  return useSyncExternalStore(
    fn => { _previewListeners.add(fn); return () => { _previewListeners.delete(fn); }; },
    () => (key ? _previewCache.get(key) ?? null : null),
    () => null,
  );
}

// Чип модели на карточке персоны-сабагента (спека «Чип модели…»): разрешённое
// состояние модели для пары (персона, сессия) — label/hint считает бэкенд. Кэш общий
// с превью «Сейчас пойдёт»: invalidateEffectiveLines сбрасывает и чипы, они зависят
// от тех же настроек моделей (слоты, матрицы, конфиг провайдеров). Работает постфактум —
// состояние вычисляется при открытии ленты, а не берётся из истории.
// null — нет пары (карточка вне сессии / обычный агент) либо ответ ещё грузится.
export function useSubagentModelChip(personaId: string | null | undefined,
  sessionId: string | null | undefined): SubagentModelChip | null {
  const key = personaId && sessionId ? queryKey({ personaId, sessionId }) : null;
  useEffect(() => { if (personaId && sessionId) ensurePreview({ personaId, sessionId }); },
    [personaId, sessionId]);
  return useSyncExternalStore(
    fn => { _previewListeners.add(fn); return () => { _previewListeners.delete(fn); }; },
    () => (key ? _previewCache.get(key)?.subagentChip ?? null : null),
    () => null,
  );
}

// Строка-итог «Сейчас пойдёт: …» для места выбора; null — ничего не показываем
export function useEffectiveLine(ctx: EffectiveLineContext): string | null {
  const d = usePreview(ctx);
  if (!d) return null;
  // У ячейки специальности уровень — это сама строка матрицы, а не «задан задачей»
  const tierText = ctx.kind === 'specialty' && ctx.tier
    ? `уровень «${TIER_TITLE[ctx.tier]}»` : undefined;
  return formatEffectiveLine(d, { tierText });
}

// MAJOR-fix этапа 4: компонент, который зависит только от факта обновления превью,
// подписывается на этот хук вместо того чтобы держать свой срез, протухающий между
// ре-рендерами. Никаких запросов не шлёт — только пересчитывается на previewEmit.
export function usePreviewTick(): number {
  return useSyncExternalStore(
    fn => { _previewListeners.add(fn); return () => { _previewListeners.delete(fn); }; },
    () => _previewTick,
    () => 0,
  );
}

export function placesWord(n: number): string {
  return n === 1 ? `${n} месте` : `${n} местах`;
}
