// Гейты пунктов меню аватара по включённости подсистем (S2b-2b).
// Сам AvatarMenu.tsx не тронут: он уже прячет пункт, когда его колбэк undefined
// (`{onOpenKnowledge && ...}`) — гейт лишь превращает колбэк в undefined там,
// где подсистема выключена. Пропы собираются в HubHeader и проходят через gateAvatarMenu.
//
// Ключи с CanDisable=false по матрице S2a-1 (например, llm) в карту не внесены:
// их нельзя выключить — гейтить нечего.

import { SUBSYSTEMS } from '../lib/subsystems';

// Проп <AvatarMenu> → ключ из SUBSYSTEMS. Единственный источник ответа
// «какой пункт меню аватара к какой подсистеме относится».
export const AVATAR_MENU_SUBSYSTEM = {
  // «Знания»
  onOpenKnowledge: SUBSYSTEMS.knowledge,
  // «Аналитика токенов»
  onOpenSpend: SUBSYSTEMS.spend,
  // «Что нового»
  onShowHistory: SUBSYSTEMS.changelog,
  // «Выкатить на бой»
  onShowDeploy: SUBSYSTEMS.deploy,
} as const;

// Копия пропсов <AvatarMenu>: у пропа из карты при выключенном ключе — undefined
// (AvatarMenu сам прячет пункт). Пропы вне карты не меняются; при включённом
// ключе остаётся тот же колбэк. isEnabled — переданный геттер (в проде —
// isSubsystemEnabled из lib/subsystems), чтобы гейт оставался чистым и тестируемым.
export function gateAvatarMenu<T extends object>(props: T, isEnabled: (key: string) => boolean): T {
  // Двойной привод к Record: T — общий (индексной сигнатуры у него нет),
  // а правку нам нужен произвольный ключ карты.
  const gated = { ...props } as unknown as Record<string, unknown>;
  for (const [prop, key] of Object.entries(AVATAR_MENU_SUBSYSTEM)) {
    if (!isEnabled(key)) gated[prop] = undefined;
  }
  return gated as unknown as T;
}
