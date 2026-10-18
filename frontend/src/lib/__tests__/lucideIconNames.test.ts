import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { iconNames } from 'lucide-react/dynamic';
import { LUCIDE_ICON_NAMES_LIST } from '../lucideIconNames.g';
import { lucideGlyphName } from '../lucideIcon';

// Сторож стартового графа (перф). Сгенерированный список имён обязан совпадать с пакетом
// (ADR-009 §5.2), а исходники — не тянуть весь lucide статически: статический
// `import { icons } from 'lucide-react'` или `import ... from 'lucide-react/dynamic'`
// превращали index.html в 1756 тегов modulepreload.

describe('сгенерированный список имён значков', () => {
  it('совпадает с iconNames установленного lucide-react', () => {
    // Упало после апгрейда пакета — перегенерировать: node scripts/gen-glyph-names.mjs
    expect([...LUCIDE_ICON_NAMES_LIST].sort()).toEqual([...iconNames].sort());
  });
});

describe('lucideGlyphName: PascalCase → имя значка', () => {
  it.each([
    ['Cat', 'cat'], ['MousePointerClick', 'mouse-pointer-click'], ['Trash2', 'trash-2'],
    ['ArrowUp01', 'arrow-up-01'],
  ])('%s → %s', (pascal, kebab) => {
    expect(lucideGlyphName(pascal)).toBe(kebab);
  });

  it('незнакомое и пустое имя — null (чат остаётся без значка)', () => {
    expect(lucideGlyphName('NoSuchIconXyz')).toBeNull();
    expect(lucideGlyphName('')).toBeNull();
    expect(lucideGlyphName(null)).toBeNull();
  });
});

const srcRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry !== '__tests__' && entry !== 'node_modules') sourceFiles(full, out);
    } else if (/\.(ts|tsx)$/.test(entry) && !/\.d\.ts$/.test(entry)) {
      out.push(full);
    }
  }
  return out;
}

// Код без комментариев: в комментариях старый импорт цитируется как причина правки
const code = (f: string) => readFileSync(f, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

describe('стартовый граф: нет статического импорта всего lucide', () => {
  const files = sourceFiles(srcRoot);

  it('нет namespace-набора `icons` из lucide-react', () => {
    const bad = files.filter(f => /import\s*\{[^}]*\bicons\b[^}]*\}\s*from\s*['"]lucide-react['"]/.test(code(f))
      || /import\s*\*\s*as\s+\w+\s+from\s*['"]lucide-react['"]/.test(code(f)));
    expect(bad.map(f => path.relative(srcRoot, f))).toEqual([]);
  });

  it('lucide-react/dynamic подключается только динамическим import()', () => {
    const bad = files.filter(f => /^\s*(import|export)\b(?!\s+type\b)[^;]*from\s*['"]lucide-react\/dynamic(\.mjs)?['"]/m.test(code(f)));
    expect(bad.map(f => path.relative(srcRoot, f))).toEqual([]);
  });
});
