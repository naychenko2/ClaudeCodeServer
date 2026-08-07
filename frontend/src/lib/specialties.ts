// Каталог специальностей и настройки к ним. Каталог грузится один раз и кэшируется
// модульно — у него несколько потребителей: раздел «Поставщики моделей» (вкладки
// Специальности/Пресеты), форма и мастер персоны.
// Подписи приходят с бэкенда (SpecialtyCatalog) — на фронте свои строки не хардкодятся.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import type { ModelTierValue, SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtyTemplateSettings } from '../types';

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

// Подпись специальности по ключу; "any" — «Любая специальность» (запись defaultSpecialty
// слоя, наследник правила "any" из v1). Ключ без пары в каталоге — как есть.
export const ANY_SPECIALTY = 'any';

export function specialtyLabel(catalog: SpecialtyCatalogEntry[] | null, key: string): string {
  if (key === ANY_SPECIALTY) return 'Любая специальность';
  return catalog?.find(e => e.key === key)?.label ?? key;
}

export function emptyLayer(): SpecialtySettingsLayer {
  return { specialties: {}, presets: [] };
}

export function cloneLayer(layer: SpecialtySettingsLayer): SpecialtySettingsLayer {
  return JSON.parse(JSON.stringify(layer)) as SpecialtySettingsLayer;
}

// Id пресета: crypto.randomUUID в secure-контексте, иначе запасной вариант
// (образец — BoardColumnsDialog). Бэкенд досоздаёт пустой id сам, но фронт
// задаёт его сразу, чтобы оптимистичный апдейт и инлайн-переименование сходились.
export function newPresetId(): string {
  try { return crypto.randomUUID(); } catch { return `preset-${Date.now()}-${Math.round(Math.random() * 1e6)}`; }
}

// Клонирует базовый слой и дописывает в него НОВЫЙ пресет (inline-сборка цепочки,
// PresetOptions.savePreset) — не сохраняет. Вызывающий решает: если это единственная
// правка слоя — шлёт onSaveLayer сам; если рядом есть ДРУГАЯ правка того же слоя
// (ячейка матрицы «Исключений») — обязан слить обе в ОДИН клон и один PUT. Раздельные
// PUT по одному слою гонятся: второй ответ (по seq) побеждает первый и стирает только
// что созданный пресет (ревью 65d8df66, CRITICAL 1 — «Исключения» теряли цепочку).
export function withNewPreset(baseLayer: SpecialtySettingsLayer, id: string, name: string,
  steps: string[]): SpecialtySettingsLayer {
  const next = cloneLayer(baseLayer);
  next.presets.push({ id, name, description: null, steps });
  return next;
}

// --- Матрица моделей по уровням у специальности (ADR-007 §2) ---

// Запись специальности в слое: существующую возвращаем как есть; новую — с шаблоном
// прав, скопированным из ЭФФЕКТИВНОГО шаблона (запись слоя заменяет запись нижнего
// слоя ЦЕЛИКОМ — «пустая» owner-запись сбросила бы права специальности к дефолту кода).
function recordOf(layer: SpecialtySettingsLayer, key: string,
  template: SpecialtyCatalogEntry['template']): SpecialtyTemplateSettings {
  if (key === ANY_SPECIALTY) {
    return layer.defaultSpecialty ?? {
      access: 'full', tools: null, disallowedTools: null,
    };
  }
  return layer.specialties[key] ?? {
    access: template?.access ?? 'full',
    tools: template?.tools ?? null,
    disallowedTools: template?.disallowedTools ?? null,
  };
}

// Иммутабельно записать запись обратно в слой (key "any" → defaultSpecialty)
function withRecord(layer: SpecialtySettingsLayer, key: string,
  rec: SpecialtyTemplateSettings): SpecialtySettingsLayer {
  const next = cloneLayer(layer);
  if (key === ANY_SPECIALTY) next.defaultSpecialty = rec;
  else next.specialties[key] = rec;
  return next;
}

// Задать/очистить ячейку уровня у специальности (value '' — очистить к наследованию).
// template — эффективный шаблон прав (нужен при создании записи, см. recordOf).
export function withTierCell(layer: SpecialtySettingsLayer, key: string,
  tier: 'strong' | 'medium' | 'weak', value: string,
  template: SpecialtyCatalogEntry['template'] = null): SpecialtySettingsLayer {
  const rec = { ...recordOf(layer, key, template) };
  const cell = value.trim() || null;
  if (tier === 'strong') rec.tierStrong = cell;
  else if (tier === 'medium') rec.tierMedium = cell;
  else rec.tierWeak = cell;
  return withRecord(layer, key, rec);
}

// Задать/снять «Уровень по умолчанию» специальности ('' — снять)
export function withDefaultTier(layer: SpecialtySettingsLayer, key: string,
  tier: ModelTierValue | '', template: SpecialtyCatalogEntry['template'] = null): SpecialtySettingsLayer {
  const rec = { ...recordOf(layer, key, template) };
  rec.defaultTier = tier || null;
  return withRecord(layer, key, rec);
}

// Действующая запись специальности для владельца: owner-слой ЦЕЛИКОМ заменяет
// глобальный (без полевого слияния) — повторяет семантику бэкенда (TemplateSettings).
// "any": owner defaultSpecialty → глобальный defaultSpecialty.
export function effectiveSpecialtyRecord(globalLayer: SpecialtySettingsLayer,
  ownerLayer: SpecialtySettingsLayer, key: string): SpecialtyTemplateSettings | null {
  if (key === ANY_SPECIALTY) return ownerLayer.defaultSpecialty ?? globalLayer.defaultSpecialty ?? null;
  return ownerLayer.specialties[key] ?? globalLayer.specialties[key] ?? null;
}
