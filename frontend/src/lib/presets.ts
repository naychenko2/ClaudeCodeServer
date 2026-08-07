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
import type { ScopedPreset, SpecialtySettingsResponse, ModelRoutePreset,
  ModelPreviewResponse, PlacePresetRef } from '../types';

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
  if (!preset) return 'Пресет удалён — работает настройка по умолчанию';
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

// Перечитать с сервера (после внешних изменений)
export function reloadPresetSettings(): void {
  _settings = null;
  void ensurePresetSettingsLoaded();
}

// Точка записи для модалки «Поставщики моделей»: после оптимистичного сохранения слоя
// подкладываем свежий снимок, чтобы все подписчики (пикеры, формы) увидели правку сразу.
export function updateSpecialtySettings(s: SpecialtySettingsResponse): void {
  _settings = s;
  invalidateEffectiveLines();
  emit();
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

// Контекст → query preview-эндпоинта. null — резолвить нечего (например, персона
// ещё не создана). Персоне и специальности подставляем место chat-persona: без place
// превью при пустых матрицах/слотах возвращало Empty и строка не рисовалась вовсе
// (дефект приёмки №2) — место добирает ответ своим назначением.
function toQuery(ctx: EffectiveLineContext): { place?: string; personaId?: string; specialty?: string; tier?: string } | null {
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

function queryKey(q: { place?: string; personaId?: string; specialty?: string; tier?: string }): string {
  return [q.place ?? '', q.personaId ?? '', q.specialty ?? '', q.tier ?? ''].join('|');
}

// Форматирование строки-итога (спека, блок 8):
//   «Сейчас пойдёт: Sonnet 5 · уровень «средняя» у персоны, модель — от специальности»
// Битая ссылка пресета: «Сейчас пойдёт: модель по умолчанию — пресет «…» удалён».
// null — строку показывать нечего (пустой резолв).
// opts.tierText — готовая подпись уровня вместо разбора tierOrigin: в ячейке
// специальности уровень — это сама строка, а не «задан задачей» (overrideTier
// эндпоинта), поэтому там tierOrigin не показываем.
export function formatEffectiveLine(d: ModelPreviewResponse, opts?: { tierText?: string }): string | null {
  if (d.preset?.broken) {
    return `Сейчас пойдёт: модель по умолчанию — пресет${d.preset.name ? ` «${d.preset.name}»` : ''} удалён`;
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
  return `Сейчас пойдёт: ${modelLabel(d.model)}${suffix}`;
}

// Кэш сырых ответов превью: значение null — запрос завершился ошибкой
const _previewCache = new Map<string, ModelPreviewResponse | null>();
const _previewInflight = new Set<string>();
const _previewListeners = new Set<() => void>();

function previewEmit() {
  _previewListeners.forEach(fn => fn());
}

function ensurePreview(q: NonNullable<ReturnType<typeof toQuery>>): void {
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

// Строка-итог «Сейчас пойдёт: …» для места выбора; null — ничего не показываем
export function useEffectiveLine(ctx: EffectiveLineContext): string | null {
  const d = usePreview(ctx);
  if (!d) return null;
  // У ячейки специальности уровень — это сама строка матрицы, а не «задан задачей»
  const tierText = ctx.kind === 'specialty' && ctx.tier
    ? `уровень «${TIER_TITLE[ctx.tier]}»` : undefined;
  return formatEffectiveLine(d, { tierText });
}

export function placesWord(n: number): string {
  return n === 1 ? `${n} месте` : `${n} местах`;
}
