import { icons, type LucideIcon } from 'lucide-react';

// Имя lucide-компонента (PascalCase, из Session.topic) → компонент иконки или null.
//
// Свободный выбор значка темы чата: модель подбирает любое из ~1700 имён lucide-react
// под предмет разговора (Cat, Dog, Bug, User, MousePointerClick…), фронт рендерит то, что
// найдётся в общем объекте icons. Незнакомое имя (модель промахнулась / бэк ушёл вперёд
// версией) → null, чат остаётся без значка — это мягкая деградация, не поломка.
//
// Бандл: import { icons } тащит весь lucide-react (~1700 иконок). Для внутреннего продукта
// CCS это приемлемый обмен за максимальную различимость чатов.
export function lucideIcon(name?: string | null): LucideIcon | null {
  if (!name) return null;
  const icon = (icons as Record<string, LucideIcon>)[name];
  return typeof icon === 'function' || typeof icon === 'object' ? icon : null;
}
