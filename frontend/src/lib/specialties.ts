// Каталог специальностей и настройки к ним. Каталог грузится один раз и кэшируется
// модульно — у него несколько потребителей: раздел «Поставщики моделей» (вкладки
// Специальности/Пресеты), форма и мастер персоны.
// Подписи приходят с бэкенда (SpecialtyCatalog) — на фронте свои строки не хардкодятся.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { presetRoute } from './presets';
import type {
  ModelTierValue, PersonaBindingMode, PersonaBindingType, SpecialtyCatalogEntry,
  SpecialtyDefaultBinding, SpecialtyPromptSection, SpecialtyPromptSectionsCatalog,
  SpecialtyPromptSectionsForSpecialty, SpecialtyPromptSectionMeta, SpecialtySettingsLayer,
  SpecialtyTemplateSettings,
} from '../types';

// === Значок и цвет роли (приходят с бэка: SpecialtyCatalogEntry.icon / color) ===
//
// Решение владельца (24.08.2026): значок и цвет роли задаёт продукт и не настраивается.
// Источник — белый список SpecialtyCatalog.Entry.Icon / Color на бэкенде (14 ролей).
// Бэкенд отдаёт icon/color в /api/specialties, фронт берёт их оттуда; неизвестное/пустое
// имя — фолбэк на 'circle' (DynamicIcon отрисует его всегда; «мусор» из каталога не
// ломает UI, см. QA B1 25.08.2026). Имена lucide-иконок — только из белого списка
// `iconNames` (lucide-react/dynamic), неверные дают «Name in Lucide DynamicIcon not found».
export function roleIconName(catalog: SpecialtyCatalogEntry | null | undefined, _key: string): string {
  return catalog?.icon || 'circle';
}

// Ключ палитры AGENT_COLORS (см. components/AgentSelector.tsx). Совпадает с
// SpecialtyCatalog.Entry.Color на бэке — фронт передаёт ключ, AGENT_COLORS даёт hex.
export function roleColorKey(catalog: SpecialtyCatalogEntry | null | undefined, _key: string): string {
  return catalog?.color || 'brown';
}

// === Аватарки ролей (assets/specialties/<roleKey>.jpg) ===
//
// Vite собирает все .jpg из assets/specialties статически через import.meta.glob.
// Отсутствующий файл НЕ ломает сборку и не вызывает ошибку компиляции — он просто
// не попадает в объект, и hasRoleAvatar вернёт false (раздел обязан жить и без
// аватарок). URL возвращается строкой-дефолтом из glob-модуля.
type AvatarGlob = Record<string, { default: string }>;
const ROLE_AVATAR_MODULES = import.meta.glob<{ default: string }>(
  '../assets/specialties/*.jpg',
  { eager: true },
) as unknown as AvatarGlob;

const ROLE_AVATAR_BY_KEY: Record<string, string> = {};
for (const path in ROLE_AVATAR_MODULES) {
  const m = path.match(/\/([^/]+)\.jpg$/);
  if (m) ROLE_AVATAR_BY_KEY[m[1]] = ROLE_AVATAR_MODULES[path].default;
}

// Есть ли аватарка для ключа роли (для выбора между img и lucide-фолбэком).
export function hasRoleAvatar(roleKey: string): boolean {
  return roleKey in ROLE_AVATAR_BY_KEY;
}

// URL аватарки роли (строка для <img src=...>), либо undefined если файла нет.
export function roleAvatarUrl(roleKey: string): string | undefined {
  return ROLE_AVATAR_BY_KEY[roleKey];
}

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

// Слить свежий слой (клон + новый пресет из PresetOptions.savePreset, приходит через
// PresetCreationCtx.onCreated) с правкой ОДНОЙ ячейки матрицы в тот же объект — вызывающий
// шлёт результат ОДНИМ onSaveLayer (регрессия 65d8df66, CRITICAL 1: раздельные PUT по
// одному слою гонятся, второй ответ стирает только что созданный пресет).
export function mergePresetIntoCell(freshLayer: SpecialtySettingsLayer, key: string,
  tier: 'strong' | 'medium' | 'weak', presetId: string,
  template: SpecialtyCatalogEntry['template'] = null): SpecialtySettingsLayer {
  return withTierCell(freshLayer, key, tier, presetRoute(presetId), template);
}

// Задать/снять «Уровень по умолчанию» специальности ('' — снять)
export function withDefaultTier(layer: SpecialtySettingsLayer, key: string,
  tier: ModelTierValue | '', template: SpecialtyCatalogEntry['template'] = null): SpecialtySettingsLayer {
  const rec = { ...recordOf(layer, key, template) };
  rec.defaultTier = tier || null;
  return withRecord(layer, key, rec);
}

// Действующий «Уровень по умолчанию» специальности по дереву настроек (для UI):
// запись специальности (owner → global), затем defaultSpecialty (owner → global).
// Семантика повторяет бэкенд SpecialtySettingsStore.SpecialtyDefaultTier, только
// отдельно по defaultTier — используется в подсказках «общая / своя», чтобы пользователь
// видел, откуда возьмётся уровень, если в текущем scope значение не задано.
export function effectiveDefaultTier(globalLayer: SpecialtySettingsLayer,
  ownerLayer: SpecialtySettingsLayer, key: string):
  { tier: ModelTierValue; source: 'owner' | 'global' } | null {
  const get = (layer: SpecialtySettingsLayer): SpecialtyTemplateSettings | null | undefined =>
    key === ANY_SPECIALTY ? layer.defaultSpecialty : layer.specialties[key];
  // 1. запись специальности в owner (если есть запись — даже пустая по defaultTier)
  const ownerSpec = ownerLayer && get(ownerLayer);
  if (ownerSpec && ownerSpec.defaultTier) return { tier: ownerSpec.defaultTier, source: 'owner' };
  // 2. запись специальности в global
  const globalSpec = globalLayer && get(globalLayer);
  if (globalSpec && globalSpec.defaultTier) return { tier: globalSpec.defaultTier, source: 'global' };
  // 3. defaultSpecialty в owner
  if (ownerLayer?.defaultSpecialty?.defaultTier) {
    return { tier: ownerLayer.defaultSpecialty.defaultTier, source: 'owner' };
  }
  // 4. defaultSpecialty в global
  if (globalLayer?.defaultSpecialty?.defaultTier) {
    return { tier: globalLayer.defaultSpecialty.defaultTier, source: 'global' };
  }
  return null;
}

// Действующая запись специальности для владельца: owner-слой ЦЕЛИКОМ заменяет
// глобальный (без полевого слияния) — повторяет семантику бэкенда (TemplateSettings).
// "any": owner defaultSpecialty → глобальный defaultSpecialty.
export function effectiveSpecialtyRecord(globalLayer: SpecialtySettingsLayer,
  ownerLayer: SpecialtySettingsLayer, key: string): SpecialtyTemplateSettings | null {
  if (key === ANY_SPECIALTY) return ownerLayer.defaultSpecialty ?? globalLayer.defaultSpecialty ?? null;
  return ownerLayer.specialties[key] ?? globalLayer.specialties[key] ?? null;
}

// === Секции промптов (фича specialty-prompt-sections, план «Секции промптов») ===
//
// Семантика наследования посекочная: enabled и text берутся каждый СВОИМ резолвом
// из верхнего слоя, где параметр ЗАДАН; явный off владельца перекрывает on админа
// (заданное значение, а не отсутствие записи). Повторяет логику бэкенда
// (SpecialtySettingsStore.EffectivePromptSectionStates).

// Откуда пришло значение параметра секции — для бейджа источника в UI.
export type PromptSectionSource = 'code' | 'global' | 'user' | 'owner';

export interface EffectivePromptSection {
  id: string;
  enabled: boolean;
  text: string;
  enabledSource: PromptSectionSource;
  textSource: PromptSectionSource;
}

// Каталог секций промптов: грузится по требованию, кэшируется на модуль — UI пере-
// рисовывается редко, повторные заходы не должны дёргать бэк заново. Инвалидация —
// перезагрузкой страницы (состав каталога меняется только с деплоем бэка).
let _promptSectionsCatalog: SpecialtyPromptSectionsCatalog | null = null;
let _promptSectionsLoading: Promise<SpecialtyPromptSectionsCatalog | null> | null = null;

export async function loadPromptSectionsCatalog(): Promise<SpecialtyPromptSectionsCatalog | null> {
  if (_promptSectionsCatalog) return _promptSectionsCatalog;
  if (_promptSectionsLoading) return _promptSectionsLoading;
  _promptSectionsLoading = api.specialties.promptSectionsCatalog()
    .then(c => { _promptSectionsCatalog = c; return c; })
    .catch(() => null)
    .finally(() => { _promptSectionsLoading = null; });
  return _promptSectionsLoading;
}

export function getPromptSectionsCatalog(): SpecialtyPromptSectionsCatalog | null {
  return _promptSectionsCatalog;
}

// Эффективные значения enabled/text и источник каждого для пары (specialty, sectionId).
// Поиск по слоям сверху вниз: сначала запись специальности (owner/user/global), затем
// defaultSpecialty той же цепочки. Источник `code` — дефолт кода из каталога.
export function effectivePromptSection(
  catalog: SpecialtyPromptSectionsCatalog | null,
  ownerLayer: SpecialtySettingsLayer | null,
  userLayer: SpecialtySettingsLayer | null,
  globalLayer: SpecialtySettingsLayer | null,
  specialtyKey: string,
  sectionId: string,
): EffectivePromptSection {
  const code = catalog?.specialties[specialtyKey]?.sections.find(s => s.id === sectionId);
  let enabled = code?.enabled ?? false;
  let text = code?.text ?? '';
  let enabledSource: PromptSectionSource = 'code';
  let textSource: PromptSectionSource = 'code';
  let enabledSet = false;
  let textSet = false;
  // Запись специальности в каждом слое + defaultSpecialty того же слоя (defaultSpecialty
  // применяется к «любой специальности» в логике матриц; здесь пробуем по факту наличия).
  const layerEntries: Array<[SpecialtySettingsLayer | null | undefined, PromptSectionSource]> = [
    [ownerLayer, 'owner'],
    [userLayer, 'user'],
    [globalLayer, 'global'],
  ];
  for (const [layer, source] of layerEntries) {
    if (!layer) continue;
    // Запись специальности и defaultSpecialty одного слоя — обе ищем; первое заданное
    // значение выигрывает, дальше не идём (сверху вниз, owner важнее global).
    for (const rec of [layer.specialties[specialtyKey], layer.defaultSpecialty]) {
      if (!rec?.promptSections) continue;
      const entry = rec.promptSections.find(p => p.id === sectionId);
      if (!entry) continue;
      if (!enabledSet) {
        enabled = entry.enabled;
        enabledSource = source;
        enabledSet = true;
      }
      if (!textSet && entry.text && entry.text.trim()) {
        text = entry.text.trim();
        textSource = source;
        textSet = true;
      }
      if (enabledSet && textSet) break;
    }
    if (enabledSet && textSet) break;
  }
  return { id: sectionId, enabled, text, enabledSource, textSource };
}

// Записать/обновить секцию в ЗАПИСИ специальности текущего слоя. enabledSet=true —
// пишем enabled, textSet=true — пишем text; пустой text трактуется как «снять override».
// override=null удаляет всю запись секции из слоя (наследование вниз).
export function withPromptSection(layer: SpecialtySettingsLayer, specialtyKey: string,
  sectionId: string, patch: { enabled?: boolean; text?: string | null; override?: false }): SpecialtySettingsLayer {
  const next = cloneLayer(layer);
  const rec = ensureSpecialtyRecord(next, specialtyKey);
  const sections = (rec.promptSections ?? []).filter(p => p.id !== sectionId);
  if (patch.override === false) {
    // Прямое обновление существующего override (нас не вызывают с override=false,
    // но семантика чёткая): сохраняем как есть
  }
  const existing = rec.promptSections?.find(p => p.id === sectionId);
  const merged: SpecialtyPromptSection = {
    id: sectionId,
    enabled: patch.enabled !== undefined ? patch.enabled : existing?.enabled ?? false,
    text: patch.text !== undefined ? patch.text : existing?.text ?? null,
  };
  sections.push(merged);
  rec.promptSections = sections;
  return next;
}

// Снять override секции в слое: запись секции удаляется целиком (наследование вниз).
export function withoutPromptSection(layer: SpecialtySettingsLayer, specialtyKey: string,
  sectionId: string): SpecialtySettingsLayer {
  const next = cloneLayer(layer);
  const rec = next.specialties[specialtyKey];
  if (!rec?.promptSections) return next;
  const filtered = rec.promptSections.filter(p => p.id !== sectionId);
  if (filtered.length === 0) {
    delete rec.promptSections;
  } else {
    rec.promptSections = filtered;
  }
  // Если после удаления запись полностью пустая (нет прав, матриц и секций) — стираем
  // её, чтобы слой оставался «честным»: иначе пустая запись продолжала бы перекрывать
  // нижний слой (см. аналогичное поведение для матриц в SpecialtySettingsStore).
  if (isRecordEmpty(rec)) delete next.specialties[specialtyKey];
  return next;
}

// Записать/обновить запись DefaultBindings (типовой профиль умений роли) в слое.
// null — снять override профиля целиком (наследование вниз).
export function withDefaultBindings(layer: SpecialtySettingsLayer, specialtyKey: string,
  bindings: SpecialtyDefaultBinding[] | null): SpecialtySettingsLayer {
  const next = cloneLayer(layer);
  const rec = ensureSpecialtyRecord(next, specialtyKey);
  if (bindings == null) {
    delete rec.defaultBindings;
  } else {
    rec.defaultBindings = bindings.slice();
  }
  if (isRecordEmpty(rec)) delete next.specialties[specialtyKey];
  return next;
}

// Убедиться, что в слое есть запись специальности; создать пустую при отсутствии.
// Копия приватной логики SpecialtySettingsStore.EnsureRecord (бэкенд тоже так делает).
export function ensureSpecialtyRecord(layer: SpecialtySettingsLayer, specialtyKey: string): SpecialtyTemplateSettings {
  let rec = layer.specialties[specialtyKey];
  if (!rec) {
    rec = { access: 'full', tools: null, disallowedTools: null };
    layer.specialties[specialtyKey] = rec;
  }
  return rec;
}

// Запись специальности пуста по всем нашим полям (кроме access/tools/disallowedTools,
// которые мы не трогаем в этом плане — фронт их не правит здесь).
function isRecordEmpty(rec: SpecialtyTemplateSettings): boolean {
  const noSections = !rec.promptSections || rec.promptSections.length === 0;
  const noBindings = !rec.defaultBindings || rec.defaultBindings.length === 0;
  return noSections && noBindings;
}

// === Профиль умений роли ===
//
// Каталог отдаёт дефолтный профиль по специальности; в слое он переопределяется
// поэлементно (список целиком заменяет наследование вниз). Удобный конструктор DTO
// для UI (SkillSearchDialog и т. п.): skillName нужен только для типа 'skill'.

export function newDefaultBinding(type: PersonaBindingType,
  condition: string, mode: PersonaBindingMode = 'auto',
  skillName?: string | null): SpecialtyDefaultBinding {
  return { type, mode, condition: condition.trim(), skillName: type === 'skill' ? (skillName ?? null) : null };
}

// id временной записи профиля: в UI до сохранения работаем с черновиком по id.
// На бэке у SpecialtyDefaultBinding нет id (это просто запись в списке слоя); для
// key в React используем сгенерированный uuid, в PUT уходит весь список без id.
export function newDefaultBindingId(): string {
  try { return crypto.randomUUID(); } catch { return `db-${Date.now()}-${Math.round(Math.random() * 1e6)}`; }
}

// === Список секций каталога по порядку (для рендера в UI) ===

export function sectionsOf(catalog: SpecialtyPromptSectionsCatalog | null): SpecialtyPromptSectionMeta[] {
  return catalog?.sections ?? [];
}

// Запись секций для конкретной специальности (из каталога, не из слоя)
export function sectionsForSpecialty(catalog: SpecialtyPromptSectionsCatalog | null,
  specialtyKey: string): SpecialtyPromptSectionsForSpecialty | null {
  if (!catalog) return null;
  return catalog.specialties[specialtyKey] ?? null;
}

