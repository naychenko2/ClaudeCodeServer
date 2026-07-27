// Чистые функции над реестром общих тегов проекта (Project.tagRegistry).
// Реестр — упорядоченный список имён с опциональным цветом; порядок задаёт и
// последовательность секций в режиме «Теги», и очерёдность чипов на карточке.
import type { ProjectTag } from '../types';

// Индекс тега в реестре (сравнение без учёта регистра — бэк валидирует уникальность
// так же). Нет в реестре → null.
export function tagIndex(registry: ProjectTag[], name: string): number | null {
  const lower = name.toLowerCase();
  const i = registry.findIndex(t => t.name.toLowerCase() === lower);
  return i >= 0 ? i : null;
}

// Порядок тега по реестру. Тег вне реестра — за всеми реестровыми (Infinity),
// чтобы сироты тонули в конце и у реестровых, и друг у друга (дальше — по алфавиту).
export function tagOrder(registry: ProjectTag[], name: string): number {
  const i = tagIndex(registry, name);
  return i === null ? Number.POSITIVE_INFINITY : (registry[i].order ?? i);
}

// Цвет тега из реестра; без записи или без цвета → undefined (чип красится accent'ом).
export function tagColor(registry: ProjectTag[], name: string): string | undefined {
  const i = tagIndex(registry, name);
  return i === null ? undefined : registry[i].color;
}

// Копия массива имён тегов, отсортированная по порядку реестра: реестровые — по order,
// сироты — в конец по алфавиту (чипы на карточке идут в том же ритме, что и секции).
export function sortTagsByRegistry(tags: string[], registry: ProjectTag[]): string[] {
  return [...tags].sort((a, b) => {
    const d = tagOrder(registry, a) - tagOrder(registry, b);
    return d !== 0 ? d : a.localeCompare(b);
  });
}
