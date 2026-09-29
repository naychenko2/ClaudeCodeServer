// Самовключение режима поля ввода (слот composer-mode, ComposerModeApi.autoSelect).
// Правило записки v3: есть выбранная картинка — есть переключатель; режим «Картинка»
// включается сам по поводу, который назвал владелец режима (черновик, «Редактировать»),
// а не только от клика в интерфейсе — фокус может прийти от агента, после перезагрузки
// или из другой вкладки. Чистая функция — под юнит-тестом.

import type { ComposerModeApi, ComposerModeCtx } from './subsystems/registryCore';

export interface ComposerModeEntry { name?: string; action?: ComposerModeApi }

// modes — только доступные сейчас режимы. prevKey — ключ, на который поле уже
// переключалось; тот же ключ второй раз режим не навязывает, иначе ручной уход в «Чат»
// откатывался бы на каждой перерисовке
export function nextComposerMode(
  modes: readonly ComposerModeEntry[], ctx: ComposerModeCtx, prevKey: string | null, modeId: string | null,
): { key: string | null; modeId: string | null } {
  for (const m of modes) {
    const k = m.name && m.action?.autoSelect?.(ctx);
    if (!k) continue;
    const key = `${m.name}:${k}`;
    return { key, modeId: key === prevKey ? modeId : m.name! };
  }
  return { key: null, modeId };
}

// Затравка поля режима (ComposerModeApi.prefill). key — повод (режим + нить): фиксируется
// с первого рендера на нём, даже пока текста нет, — иначе первый запуск свежей нити,
// пришедший после отправки, вернул бы промпт в очищенное поле. auto — текст, который
// положили мы сами: пока человек его не трогал, смена нити заменяет его новым
export interface PrefillState { key: string | null; auto: string | null }

export function nextPrefill(
  state: PrefillState, next: { key: string; text: string | null } | null, field: string,
): { state: PrefillState; field: string } {
  if (!next || next.key === state.key) return { state, field };
  const untouched = !field.trim() || (state.auto !== null && field === state.auto);
  if (!untouched) return { state: { key: next.key, auto: null }, field };
  return { state: { key: next.key, auto: next.text }, field: next.text ?? '' };
}
