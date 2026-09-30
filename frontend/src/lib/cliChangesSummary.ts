import { plural } from './plural';

// Сводка списка изменений claude CLI одной строкой: «4 версии: 38 новых возможностей,
// 65 изменений, 80 улучшений, 270 исправлений». Пункты поштучно админу не нужны — больше
// половины CHANGELOG составляют исправления; полный список — по ссылке на GitHub.
// Вид пункта — по первому слову (Added/Changed/Improved/Fixed), прочее — «других».
export const CLI_CHANGELOG_URL = 'https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md';

const KINDS: { word: string; forms: [string, string, string] }[] = [
  { word: 'Added', forms: ['новая возможность', 'новые возможности', 'новых возможностей'] },
  { word: 'Changed', forms: ['изменение', 'изменения', 'изменений'] },
  { word: 'Improved', forms: ['улучшение', 'улучшения', 'улучшений'] },
  { word: 'Fixed', forms: ['исправление', 'исправления', 'исправлений'] },
];

export function cliChangesSummary(changes: { items: string[] }[]): string | null {
  if (changes.length === 0) return null;
  const counts = new Map<string, number>();
  let other = 0;
  for (const v of changes) {
    for (const item of v.items) {
      const kind = KINDS.find(k => item.startsWith(k.word + ' '));
      if (kind) counts.set(kind.word, (counts.get(kind.word) ?? 0) + 1);
      else other++;
    }
  }
  const parts = KINDS
    .filter(k => (counts.get(k.word) ?? 0) > 0)
    .map(k => { const n = counts.get(k.word)!; return `${n} ${plural(n, ...k.forms)}`; });
  if (other > 0) parts.push(`${other} ${plural(other, 'другое', 'других', 'других')}`);
  const versions = `${changes.length} ${plural(changes.length, 'версия', 'версии', 'версий')}`;
  return parts.length > 0 ? `${versions}: ${parts.join(', ')}` : versions;
}
