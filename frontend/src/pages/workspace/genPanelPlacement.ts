// Место панелей генерации («Картинки», «Звук») в планшетной зоне (ADR-021 §3, макет
// image-editor-v4-panel). Обычная панель на узком планшете открывается ящиком поверх
// контента, но панель генерации работает ВМЕСТЕ с лентой и полем ввода: ящик закрывал
// композер с кнопкой запуска. Поэтому до GEN_PANEL_INLINE_MIN она стоит колонкой в потоке
// (уже обычной), а ниже в зоне её нет вовсе — её рисует шторкой полоса над полем ввода.

import { GEN_PANEL_INLINE_MIN, PANEL_INLINE_MAX_SHARE } from '../../lib/breakpoints';
import { genPanelKeys, type PanelKey } from './panelCatalog';

// Ширина колонки генерации там, где обычной панели места в потоке нет: 320 + лента 420 на 800
export const GEN_STACK_W = 320;

const isGen = (k: PanelKey) => genPanelKeys().includes(k);

// Есть ли панель в зоне при этой ширине окна (compact — планшетная зона)
export const genPanelInZone = (k: PanelKey, compact: boolean, windowWidth: number): boolean =>
  !compact || !isGen(k) || windowWidth >= GEN_PANEL_INLINE_MIN;

// Как стоит компактный стек: inline — в потоке шириной width, иначе ящиком поверх
export function compactStack(windowWidth: number, zoneWidth: number, keys: readonly PanelKey[]): { inline: boolean; width: number } {
  if (windowWidth >= zoneWidth / PANEL_INLINE_MAX_SHARE) return { inline: true, width: zoneWidth };
  if (windowWidth >= GEN_PANEL_INLINE_MIN && keys.length > 0 && keys.every(isGen)) {
    return { inline: true, width: Math.min(zoneWidth, GEN_STACK_W) };
  }
  return { inline: false, width: zoneWidth };
}
