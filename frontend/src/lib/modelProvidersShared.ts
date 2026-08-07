import { useEffect, useState } from 'react';
import type { CSSProperties, Dispatch, SetStateAction } from 'react';
import { api, type ModelTiers } from './api';
import { C, FONT, FS } from './design';
import { modelLabel, providerLabel, modelProvider,
  type ProviderCapabilities, type ModelOption } from './models';
import { effectiveTierModel as tierModelOf } from './modelTiers';
import type { OllamaUsageInfo, AppSettings, UserProfile } from '../types';

// Общие константы, типы и хелперы раздела «Поставщики моделей» — без компонентов
// (react-refresh требует компоненты держать отдельно). Используются вкладочной
// раскладкой раздела и контролами её вкладок.

// Ненавязчивая hover-подсветка строки действия — через инжектиый класс (как в IconButton),
// без per-row состояния (строк в списке много, группами по разделам).
export const ROW_CLASS = 'cc-mprov-row';
if (typeof document !== 'undefined' && !document.getElementById('cc-mprov-row-style')) {
  const el = document.createElement('style');
  el.id = 'cc-mprov-row-style';
  el.textContent = `.${ROW_CLASS}:hover{background:${C.bgSelected};}`;
  document.head.appendChild(el);
}

export const groupHeaderStyle: CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.06em', margin: '14px 2px 6px',
};

// Заголовок уровня (uppercase-метка слева)
export function levelTitleStyle(): CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 6,
    fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
    textTransform: 'uppercase', letterSpacing: '0.07em', margin: '0 2px 8px',
  };
}

// Ключ действия утреннего брифа (LocalActionCatalog.DailyBriefing) — у него, помимо выбора
// исполнителя, есть тумблер «присылать ли по расписанию» (AppSettings.DailyBriefingEnabled).
export const BRIEFING_KEY = 'daily-briefing';

// Прессеты быстрой настройки мест применения моделей. Старые пресеты (recommended/balanced/free/local)
// убраны из UI — бэк их оставляет, но фронт больше не шлёт.
export type PresetKey = 'tiers' | 'tiers-local';

// Слоты моделей инстанса: три именованные модели, на которые ссылаются назначения мест.
// field — имя поля AppSettings (патчится по одному), route — значение назначения.
export type TierKey = 'strong' | 'medium' | 'weak';
export const TIERS: Record<TierKey, { title: string; hint: string; field: keyof AppSettings; route: string }> = {
  strong: { title: 'Сильная', hint: 'Чаты, сложные задачи', field: 'modelTierStrong', route: 'tier:strong' },
  medium: { title: 'Средняя', hint: 'Сабагенты, тяжёлый фон', field: 'modelTierMedium', route: 'tier:medium' },
  weak: { title: 'Слабая', hint: 'Теги, заголовки, мелочь', field: 'modelTierWeak', route: 'tier:weak' },
};
export const TIER_ORDER: TierKey[] = ['strong', 'medium', 'weak'];

// Единая подпись состояния слота. Для личных/пользовательских слотов пустое значение
// показывает наследование от глобального слота: «Как у всех · {modelLabel}». Для глобального
// контекста пустой слот — «не задана — решает CLI».
export function tierSubtitle(model: string, inheritedModel?: string | null): string {
  if (model) {
    const provider = providerLabel(modelProvider(model));
    return provider ? `${modelLabel(model)} · ${provider}` : modelLabel(model);
  }
  if (inheritedModel) {
    return `Как у всех · ${modelLabel(inheritedModel)}`;
  }
  if (inheritedModel === undefined) {
    return 'не задана — решает CLI';
  }
  return 'Как у всех · не задана — решает CLI';
}

// Статус адаптера → цвет точки (только дизайн-токены)
export const DOT_COLOR: Record<'active' | 'inactive' | 'offline', string> = {
  active: C.success,
  inactive: C.textMuted,
  offline: C.warning,
};

// Провайдер годится для чипса-быстрого выбора: настроен и несёт полную тройку слотов.
export function hasTierTriple(caps: ProviderCapabilities): boolean {
  return caps.tierStrong != null && caps.tierMedium != null && caps.tierWeak != null;
}

// === Общие данные раздела ===
// Владелец состояния один (модалка — легаси или вкладочная), секции читают и правят
// через него: слоты в уровнях 2/3 и подписи действий должны сходиться в один снимок.
export interface ProviderData {
  info: OllamaUsageInfo | undefined;
  setInfo: Dispatch<SetStateAction<OllamaUsageInfo | undefined>>;
  globalSettings: AppSettings | null;
  setGlobalSettings: Dispatch<SetStateAction<AppSettings | null>>;
  users: UserProfile[];
  ownTiers: ModelTiers | null;
  setOwnTiers: Dispatch<SetStateAction<ModelTiers | null>>;
  userTiers: ModelTiers | null;
  setUserTiers: Dispatch<SetStateAction<ModelTiers | null>>;
  // Активные значения слотов для текущего контекста (личной/выбранный юзер/глобальный)
  selectedTiers: ModelTiers | null;
  globalTiers: ModelTiers | null;
  // Эффективная модель слота для текущего контекста — ею подписываются места применения
  effectiveTierModel: (t: TierKey) => string;
}

export function useProviderData(isAdmin: boolean, contextUserId: string | null): ProviderData {
  const [info, setInfo] = useState<OllamaUsageInfo | undefined>(undefined);
  // Глобальные настройки инстанса: слоты тиров (уровень 2 в режиме «Общие») +
  // тумблер утреннего брифа (уровень 3). Для обычного пользователя — только источник
  // подписей «как у всех» и для расчёта эффективного слабого слота.
  const [globalSettings, setGlobalSettings] = useState<AppSettings | null>(null);
  // Личные слоты обычного пользователя.
  const [ownTiers, setOwnTiers] = useState<ModelTiers | null>(null);
  // Для админа: список пользователей и слоты выбранного контекста уровня 2.
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [userTiers, setUserTiers] = useState<ModelTiers | null>(null);

  useEffect(() => {
    let cancelled = false;
    if (isAdmin) {
      api.usage.get()
        .then(d => { if (!cancelled) setInfo(d.ollama ?? { enabled: false, actions: [] }); })
        .catch(() => { if (!cancelled) setInfo({ enabled: false, actions: [] }); });
      api.users.list()
        .then(list => { if (!cancelled) setUsers(list); })
        .catch(() => { /* список пользователей не критичен для диалога */ });
    }
    api.settings.get()
      .then(s => { if (!cancelled) setGlobalSettings(s); })
      .catch(() => { /* слоты останутся пустыми, тумблер — включённым */ });
    if (!isAdmin) {
      api.meModelTiers.get()
        .then(t => { if (!cancelled) setOwnTiers(t); })
        .catch(() => { /* остаёмся на пустых слотах */ });
    }
    return () => { cancelled = true; };
  }, [isAdmin]);

  // Загрузка слотов выбранного пользователя для админа. Сброс слотов и режима
  // редактирования делает обработчик выбора в селекторе (не setState в теле
  // effect — иначе каскад ре-рендеров, на что ругается react-hooks/set-state-in-effect).
  useEffect(() => {
    if (!isAdmin || !contextUserId) return;
    let cancelled = false;
    api.adminUserModelTiers.get(contextUserId)
      .then(t => { if (!cancelled) setUserTiers(t); })
      .catch(() => { if (!cancelled) setUserTiers(null); });
    return () => { cancelled = true; };
  }, [isAdmin, contextUserId]);

  // Глобальные слоты — для подписей наследования и для уровня 3 (он всегда глобальный).
  const globalTiers: ModelTiers | null = globalSettings
    ? {
      strong: globalSettings.modelTierStrong ?? null,
      medium: globalSettings.modelTierMedium ?? null,
      weak: globalSettings.modelTierWeak ?? null,
    }
    : null;

  // Активные значения слотов для текущего контекста уровня 2.
  let selectedTiers: ModelTiers | null;
  if (!isAdmin) selectedTiers = ownTiers;
  else if (contextUserId) selectedTiers = userTiers;
  else selectedTiers = globalTiers;

  // Эффективная модель слота для текущего контекста: личный слот выбранного
  // пользователя, если задан, иначе глобальный. Ей подписываются места уровня 3 —
  // сами назначения общие для всех, а модель за слотом зависит от контекста.
  // Склейка одна на весь фронт (ею же подписан выбор уровня у задач и персон).
  const effectiveTierModel = (t: TierKey): string =>
    tierModelOf(t, selectedTiers, globalTiers);

  return {
    info, setInfo, globalSettings, setGlobalSettings, users,
    ownTiers, setOwnTiers, userTiers, setUserTiers,
    selectedTiers, globalTiers, effectiveTierModel,
  };
}

// === Плитки провайдеров (уровень 1) ===

export interface ProviderTile {
  name: string;
  status: 'active' | 'inactive' | 'offline';
  statusLabel: string;
  count?: number;
}

export function buildProviderTiles(providers: { key: string; caps: ProviderCapabilities }[],
  models: ModelOption[], info: OllamaUsageInfo | undefined): ProviderTile[] {
  const providerTiles: ProviderTile[] = providers.map(p => ({
    name: p.caps.displayName || providerLabel(p.key),
    status: (p.caps.configured === false ? 'inactive' : 'active') as ProviderTile['status'],
    statusLabel: p.caps.configured === false ? 'Не настроен' : 'Активен',
    count: models.filter(m => (m.provider ?? modelProvider(m.value)) === p.key).length || undefined,
  }));
  const ollamaTile: ProviderTile | null = info ? {
    name: 'Ollama',
    status: (info.enabled ? 'active' : 'offline') as ProviderTile['status'],
    statusLabel: info.enabled ? (info.model ? `Локальная · ${info.model}` : 'Активен') : 'Офлайн',
    count: undefined,
  } : null;
  return [...providerTiles, ...(ollamaTile ? [ollamaTile] : [])];
}

// Слот назначения ("tier:*", легаси "default"/"claude" ≙ средняя) или null для прочих маршрутов
export function routeTier(route: string | null | undefined): TierKey | null {
  const r = route ?? '';
  if (r === 'default' || r === 'claude' || r === 'tier:medium') return 'medium';
  if (r === 'tier:strong') return 'strong';
  if (r === 'tier:weak') return 'weak';
  return null;
}

// Человекочитаемая подпись маршрута. У слота показываем и его имя, и модель
// за ним — иначе «Сильная» ничего не говорит о том, что реально поедет.
export function routeLabel(route: string | null | undefined, ollamaModel?: string,
  tierModels?: Record<TierKey, string>): string {
  const r = route ?? '';
  if (!r) return 'не задан';
  if (r === 'local') return `Локальная${ollamaModel ? ` · ${ollamaModel}` : ''}`;
  const tier = routeTier(r);
  if (tier) {
    const m = tierModels?.[tier] ?? '';
    return m ? `${TIERS[tier].title} · ${modelLabel(m)}` : `${TIERS[tier].title} · не задана`;
  }
  return modelLabel(r);
}
