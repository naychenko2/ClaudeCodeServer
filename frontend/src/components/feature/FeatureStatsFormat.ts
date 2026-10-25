// Форматтеры чисел и подписи групп статистики фичи (ST-F8, ST-F14б).
// Чистые помощники, без DOM.

import { roleLabel } from '../../lib/roleLabel';
import { modelLabel } from '../../lib/models';
import type { Persona } from '../../types';

// Токены: «1,2М» / «640 тыс.» / «12 345». null → '' (нет данных).
export function formatTokens(n: number | null): string {
  if (n == null) return '';
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1).replace('.', ',')}М`;
  if (n >= 1_000) return `${Math.round(n / 1000)} тыс.`;
  return String(n);
}

// Деньги (USD): «$15,60» / «$0,42». null → '' (нет данных).
export function formatUsd(n: number | null): string {
  if (n == null) return '';
  return `$${n.toFixed(2).replace('.', ',')}`;
}

// Доля 0..1 → «42 %». Округление каждой доли независимо: сумма текстов может
// дать 101 % — полосу кольца вид строит от суммы частей, а не из этих текстов.
export function percentText(share: number): string {
  return `${Math.round(share * 100)} %`;
}

// Доля 0..1 → «31 %». null → '' (не считалась).
export function sharePercent(share: number | null): string {
  if (share == null) return '';
  return percentText(share);
}

// Ключ группы «роли» (сырой taskRole) → русская подпись (roleLabel).
// 'step' без stepRef/lane → roleLabel пуст → нейтральное «шаг».
// Единственная функция подписи роли для кольца, таблицы, карточек, чипов и подписей чатов.
export function roleGroupLabel(key: string): string {
  const l = roleLabel(key, null, null);
  if (l) return l;
  return key === 'step' ? 'шаг' : key;
}

// Ключ группы «модели» (modelFull) → читаемое имя модели (modelLabel; незнакомый — как есть).
export function modelGroupLabel(key: string): string {
  return modelLabel(key) || key;
}

// Ключ группы «персоны» (personaId) → имя персоны; недоступна → «персона недоступна» (не UUID).
export function personaGroupLabel(key: string, personas: Persona[]): string {
  const p = personas.find(x => x.id === key);
  return p ? p.name : 'персона недоступна';
}
