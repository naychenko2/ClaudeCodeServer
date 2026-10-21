// S4: потребители читают мету и домашнюю зону панели через резолверы каталога
// (panelMeta/panelHome), а не напрямую из PANEL_META/PANEL_HOME — иначе ключ
// плагина (без меты в реестре) роняет панель. Два сторожа:
//  • скан src/: прямого PANEL_META[/PANEL_HOME[ вне panelCatalog.ts и тестов нет
//    (реестр живёт в panelCatalog.ts, тесты реестра — в contextPanelHost.test.ts);
//  • panelMetaResolved: ключ плагина без меты даёт fallback (заголовок из panelId,
//    иконка по умолчанию), а не undefined — панель не падает.
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { panelMeta, panelMetaResolved } from './panelCatalog';

// Корень src/: отсюда скан всех потребителей (кроме panelCatalog.ts и тестов)
const SRC = fileURLToPath(new URL('../..', import.meta.url));

function sources(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) sources(p, out);
    else if (/\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name) && name !== 'panelCatalog.ts') out.push(p);
  }
  return out;
}

describe('S4: потребители на резолверах каталога', () => {
  it('прямого PANEL_META[/PANEL_HOME[ в потребителях нет (реестр — в panelCatalog.ts)', () => {
    const offenders = sources(SRC)
      .filter(f => /PANEL_META\[|PANEL_HOME\[/.test(readFileSync(f, 'utf8')))
      .map(f => f.slice(SRC.length + 1));
    expect(offenders).toEqual([]);
  });

  it('panelMetaResolved: ключ плагина без меты — fallback, а не undefined (панель не падает)', () => {
    const K = 'plugin:acme/x:main';
    // panelMeta для незарегистрированного плагина — undefined (реестр пуст)
    expect(panelMeta(K)).toBeUndefined();
    // ... но panelMetaResolved даёт запасную мету: заголовок из panelId, иконка по умолчанию
    const m = panelMetaResolved(K);
    expect(m.title).toBe('main');
    expect(m.Icon).toBeDefined();
  });

  it('panelMetaResolved: статический ключ — из реестра, без fallback', () => {
    expect(panelMetaResolved('files').title).toBe('Файлы');
    expect(panelMetaResolved('chats').title).toBe('Чаты');
  });
});
