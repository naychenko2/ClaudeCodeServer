// Каталог специальностей и настройки к ним. Каталог грузится один раз и кэшируется
// модульно — у него несколько потребителей: раздел «Поставщики моделей» (вкладки
// Специальности/Пресеты), форма и мастер персоны.
// Подписи приходят с бэкенда (SpecialtyCatalog) — на фронте свои строки не хардкодятся.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import type { SpecialtyCatalogEntry, SpecialtySettingsLayer } from '../types';

let _catalog: SpecialtyCatalogEntry[] | null = null;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

export async function ensureSpecialtiesLoaded(): Promise<void> {
  if (_catalog || _loading) return _loading ?? Promise.resolve();
  _loading = api.specialties.list()
    .then(list => { _catalog = list; })
    .catch(() => { /* флаг выключен или сервер недоступен — каталог останется null */ })
    .finally(() => { _loading = null; emit(); });
  return _loading;
}

// Перечитывает каталог заново (например, после смены подписей на бэкенде)
export function reloadSpecialties(): void {
  _catalog = null;
  void ensureSpecialtiesLoaded();
}

// Каталог специальностей, если данные уже загрузились; иначе null (загрузка в процессе
// или сервер недоступен) — потребители при null показывают состояние загрузки.
export function useSpecialtyCatalog(): SpecialtyCatalogEntry[] | null {
  useEffect(() => {
    void ensureSpecialtiesLoaded();
  }, []);
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _catalog,
    () => _catalog,
  );
}

// Подпись специальности по ключу; "any" — специальное значение правил пресетов.
// Ключ без пары в каталоге показываем как есть (лучше сырой ключ, чем пустота).
export const ANY_SPECIALTY = 'any';

export function specialtyLabel(catalog: SpecialtyCatalogEntry[] | null, key: string): string {
  if (key === ANY_SPECIALTY) return 'Любая специальность';
  return catalog?.find(e => e.key === key)?.label ?? key;
}

// --- Системные пресеты «по умолчанию» ---
// Модель по умолчанию для специальности хранится правилом «специальность → маршрут»
// в системном пресете слоя: глобальный слой задаёт общие значения, личный — только
// переопределения. Id разные НАМЕРЕННО: личный пресет с id глобального заместил бы
// его ЦЕЛИКОМ (EffectivePresets бэкенда), а так личное правило бьёт свою специальность,
// а неуказанные специальности наследуют глобальное правило — семантика «Как у всех».
export const DEFAULT_GLOBAL_PRESET_ID = 'default-global';
export const DEFAULT_OWNER_PRESET_ID = 'default-personal';
export const DEFAULT_PRESET_NAME = 'По умолчанию';

export function emptyLayer(): SpecialtySettingsLayer {
  return { specialties: {}, presets: [] };
}

export function cloneLayer(layer: SpecialtySettingsLayer): SpecialtySettingsLayer {
  return JSON.parse(JSON.stringify(layer)) as SpecialtySettingsLayer;
}

// Маршрут специальности из системного пресета слоя; null — правила нет
export function defaultRouteFor(layer: SpecialtySettingsLayer, presetId: string, specKey: string): string | null {
  const preset = layer.presets.find(p => p.id === presetId);
  const rule = preset?.rules.find(r => r.specialty === specKey);
  return rule?.route || null;
}

// Иммутабельно задать/снять маршрут специальности в системном пресете слоя.
// route '' снимает правило; опустевший пресет удаляется (пустой слой = сброс личных
// переопределений на бэкенде). Имя пресета задаётся при создании.
export function withDefaultRoute(
  layer: SpecialtySettingsLayer, presetId: string, specKey: string, route: string,
): SpecialtySettingsLayer {
  const next = cloneLayer(layer);
  let preset = next.presets.find(p => p.id === presetId);
  if (!preset) {
    if (!route) return next;
    preset = { id: presetId, name: DEFAULT_PRESET_NAME, description: null, rules: [] };
    next.presets.push(preset);
  }
  const i = preset.rules.findIndex(r => r.specialty === specKey);
  if (route) {
    if (i >= 0) preset.rules[i] = { specialty: specKey, route };
    else preset.rules.push({ specialty: specKey, route });
  } else if (i >= 0) {
    preset.rules.splice(i, 1);
  }
  if (preset.rules.length === 0) {
    next.presets = next.presets.filter(p => p.id !== presetId);
  }
  return next;
}

// Id пресета: crypto.randomUUID в secure-контексте, иначе запасной вариант
// (образец — BoardColumnsDialog). Бэкенд досоздаёт пустой id сам, но фронт
// задаёт его сразу, чтобы оптимистичный апдейт и инлайн-переименование сходились.
export function newPresetId(): string {
  try { return crypto.randomUUID(); } catch { return `preset-${Date.now()}-${Math.round(Math.random() * 1e6)}`; }
}

// Создать новый пресет в слое: имя проверяет вызывающий
export function withNewPreset(layer: SpecialtySettingsLayer, name: string): SpecialtySettingsLayer {
  const next = cloneLayer(layer);
  next.presets.push({ id: newPresetId(), name, description: null, rules: [] });
  return next;
}
