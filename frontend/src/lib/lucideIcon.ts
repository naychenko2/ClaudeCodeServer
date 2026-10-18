import { LUCIDE_ICON_NAMES_LIST } from './lucideIconNames.g';

// Имя lucide-компонента (PascalCase, из Session.topic) → kebab-имя значка для GlyphIcon
// или null.
//
// Свободный выбор значка темы чата: модель подбирает любое из ~1700 имён lucide-react
// под предмет разговора (Cat, Dog, Bug, User, MousePointerClick…). Незнакомое имя (модель
// промахнулась / бэк ушёл вперёд версией) → null, чат остаётся без значка — мягкая
// деградация, не поломка.
//
// Бандл: раньше здесь был `import { icons } from 'lucide-react'`, который статически
// тащил весь набор в стартовый граф (1700+ modulepreload в index.html). Теперь только
// строковый список имён; сами значки грузит GlyphIcon лениво, по одному.
const PASCAL_TO_NAME = new Map<string, string>();
for (const kebab of LUCIDE_ICON_NAMES_LIST) {
  // Та же свёртка, что у lucide: arrow-up-0-1 → ArrowUp01, trash-2 → Trash2
  const pascal = kebab.replace(/^([a-z0-9])|-+([a-z0-9])/g, (_m, a: string | undefined, b: string | undefined) => (a ?? b ?? '').toUpperCase());
  PASCAL_TO_NAME.set(pascal, kebab);
}

export function lucideGlyphName(name?: string | null): string | null {
  if (!name) return null;
  return PASCAL_TO_NAME.get(name) ?? null;
}
