// Уровень рекомендации AI-хаба — сквозная абстракция, единая для обоих источников:
// LLM (Ollama) и rule-based фолбэка. По ней меняется вид FAB (бледный → яркий →
// анимированный) и порог всплытия балуна. Источник уровня абстрагирован — UI-реакция
// одна и та же независимо от того, кто уровень выставил.

export type SuggestionLevel = 'none' | 'minor' | 'medium' | 'strong';

const ORDER: Record<SuggestionLevel, number> = { none: 0, minor: 1, medium: 2, strong: 3 };

// Максимум по набору уровней (агрегат контекста для реакции FAB).
export function maxLevel(levels: SuggestionLevel[]): SuggestionLevel {
  return levels.reduce<SuggestionLevel>((acc, l) => (ORDER[l] > ORDER[acc] ? l : acc), 'none');
}

// Порог, с которого балун всплывает сам — только сильная рекомендация (strong).
// medium/minor не всплывают и не «будят» кнопку; они видны лишь в открытой палитре.
export function shouldSurface(level: SuggestionLevel): boolean {
  return ORDER[level] >= ORDER.strong;
}

// Метка уровня для бейджа в балуне.
export function levelLabel(level: SuggestionLevel): string {
  switch (level) {
    case 'strong': return 'Рекомендую';
    case 'medium': return 'Есть идея';
    default: return 'Можно';
  }
}
