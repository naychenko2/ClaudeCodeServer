// Ядро локального ранжирования: связывает каталог действий, сбор контекста и клиент
// Ollama. Используется обоими слоями AI-хаба — проактивными подсказками (push) и
// палитрой (pull). Не импортируется из actions.tsx — цикла нет.

import { AI_ACTIONS, type AiActionCtx } from './actions';
import { collectContext } from './contextCollect';
import { suggestActions } from './ollama';
import { maxLevel, type SuggestionLevel } from './levels';

export interface RankResult {
  available: boolean;                          // ответила ли модель (false → фолбэк на правила)
  ranked: { id: string; level: SuggestionLevel }[];
  aggregate: SuggestionLevel;                  // максимум по ranked — для реакции FAB
}

// Отранжировать доступные сейчас действия по содержанию открытой сущности.
// null — ранжировать нечего (нет доступных действий / офлайн / ошибка сбора).
export async function rankContext(ctx: AiActionCtx, maxK = 3): Promise<RankResult | null> {
  const options = AI_ACTIONS.filter(a => a.when(ctx)).map(a => ({ id: a.id, title: a.title, hint: a.hint }));
  const collected = await collectContext(ctx, options);
  if (!collected) return null;

  const res = await suggestActions(collected.type, collected.text, collected.actions, maxK);
  const ranked = res.ranked.filter(r => r.level !== 'none');
  return { available: res.available, ranked, aggregate: maxLevel(ranked.map(r => r.level)) };
}
