// Сводка полосы «Картинки» (прототип полос, вариант C): кнопка настроек, свёрнутая строка
// и пункт меню переключателя говорят одно и то же. Чистая функция — под юнит-тестом.

import { variantsWord } from '../format';

export interface StripSummaryParts {
  // «hero.png · версия 2»; null — картинка не выбрана
  focus: string | null;
  provider: string | null;
  model: string | null;
  count: number;
  price: string | null;
  character: string | null;
}

// short — кнопка-сводка: «fal · FLUX Kontext · 2 вар. · ≈ $0.08»; иначе — строка целиком:
// «Работаем с: hero.png · версия 2 · fal · FLUX Kontext · 2 варианта · ≈ $0.08 · Аня»
export function stripSummary(p: StripSummaryParts, short = false): string {
  if (short) return [p.provider, p.model, `${p.count} вар.`, p.price].filter(Boolean).join(' · ');
  return [
    p.focus ? `Работаем с: ${p.focus}` : 'Картинка не выбрана',
    [p.provider, p.model].filter(Boolean).join(' · '),
    variantsWord(p.count),
    p.price,
    p.character,
  ].filter(Boolean).join(' · ');
}
