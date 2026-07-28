// Уровень модели («слот»): сильная / средняя / слабая. Уровень задают задача и персона —
// какая за ним стоит модель, решают личный слот пользователя поверх глобального слота
// инстанса. Здесь — единственная точка склейки на фронте (её же зовёт диалог поставщиков)
// и стор эффективных моделей для форм, которые подписывают варианты уровня.

import { useEffect, useSyncExternalStore } from 'react';
import { api, type ModelTiers } from './api';

export type ModelTierKey = 'strong' | 'medium' | 'weak';

export const TIER_ORDER: ModelTierKey[] = ['strong', 'medium', 'weak'];

export const TIER_TITLE: Record<ModelTierKey, string> = {
  strong: 'Сильная',
  medium: 'Средняя',
  weak: 'Слабая',
};

// Уровень с провода (задача, персона): бэкенд шлёт camelCase-имя слота, всё прочее — «не задан»
export function parseTier(value?: string | null): ModelTierKey | '' {
  return value === 'strong' || value === 'medium' || value === 'weak' ? value : '';
}

// Эффективная модель слота: личный слот пользователя, если задан, иначе глобальный.
// `||`, а не `??`: оптимистичное сохранение слота кладёт в стейт пустую строку (null придёт
// лишь с ответом сервера), и `??` пропустил бы её как валидное значение — подпись мигнула
// бы на «не задана» вместо глобальной.
export function effectiveTierModel(t: ModelTierKey, own: ModelTiers | null, global: ModelTiers | null): string {
  return own?.[t] || global?.[t] || '';
}

// Модели слотов текущего пользователя (личные поверх глобальных) — снимок для стора.
export type TierModels = Record<ModelTierKey, string>;

const EMPTY: TierModels = { strong: '', medium: '', weak: '' };

let _snapshot: TierModels = EMPTY;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

function getSnapshot(): TierModels {
  return _snapshot;
}

// Ленивая загрузка (как ensurePersonasLoaded): личные слоты + глобальные настройки.
// Ошибка любой из половин не валит стор — остаёмся с тем, что удалось прочитать,
// подпись уровня просто останется без модели.
export function ensureTierModelsLoaded(): Promise<void> {
  if (_loading) return _loading;
  let own: ModelTiers | null = null;
  let global: ModelTiers | null = null;
  _loading = Promise.all([
    api.meModelTiers.get().then(t => { own = t; }).catch(() => {}),
    api.settings.get().then(s => {
      global = { strong: s.modelTierStrong ?? null, medium: s.modelTierMedium ?? null, weak: s.modelTierWeak ?? null };
    }).catch(() => {}),
  ]).then(() => {
    _snapshot = {
      strong: effectiveTierModel('strong', own, global),
      medium: effectiveTierModel('medium', own, global),
      weak: effectiveTierModel('weak', own, global),
    };
    _listeners.forEach(fn => fn());
  });
  return _loading;
}

// Реактивные модели слотов для форм: до загрузки — пусто (варианты без подписи модели)
export function useTierModels(): TierModels {
  useEffect(() => { void ensureTierModelsLoaded(); }, []);
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
