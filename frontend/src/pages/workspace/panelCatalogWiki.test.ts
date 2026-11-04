// Сторожа ключей панелей вики раздела «Заметки» (W1, notes-wiki-unified): реестр
// и вывод набора через shell-kit. Кода подключения здесь нет (W3) — проверяется
// только то, что ключи заведены правильно: стоят в PANEL_KEYS сразу после
// notesGraph, имеют мету и домашнюю зону 'right', входят в NOTES_KEYS, а
// sanitizeLayout принимает их при чтении раскладки (неизвестный ключ иначе
// молча выбрасывается из сохранённой раскладки).
import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import {
  NOTES_KEYS, PANEL_HOME, PANEL_KEYS, PANEL_META, WIKI_PANEL_KEYS, isPanelKey,
} from './panelCatalog';
import { sanitizeLayout } from './panelStackState';

const WIKI = ['wikiInbox', 'wikiJournal', 'wikiHealth', 'wikiSettings'] as const;
const TITLES: Record<string, string> = {
  wikiInbox: 'Входящие', wikiJournal: 'Журнал', wikiHealth: 'Здоровье', wikiSettings: 'Настройки',
};

describe('ключи панелей вики в реестре панелей', () => {
  it('WIKI_PANEL_KEYS — ровно четыре ключа вики', () => {
    expect(WIKI_PANEL_KEYS as readonly string[]).toEqual(WIKI);
  });

  it('ключи стоят в PANEL_KEYS сразу после notesGraph', () => {
    const keys = PANEL_KEYS as readonly string[];
    const at = keys.indexOf('notesGraph');
    expect(at).toBeGreaterThanOrEqual(0);
    expect(keys.slice(at + 1, at + 5)).toEqual(WIKI);
  });

  it('у каждой панели вики есть подпись и домашняя зона right', () => {
    for (const k of WIKI) {
      expect(PANEL_META[k].title).toBe(TITLES[k]);
      expect(PANEL_HOME[k]).toBe('right');
    }
  });

  it('NOTES_KEYS раздела «Заметки» расширен набором вики', () => {
    expect(NOTES_KEYS as readonly string[]).toEqual(['notesList', 'notesGraph', ...WIKI]);
  });
});

describe('раскладка с ключами вики читается (Готово по смыслу шага)', () => {
  it('sanitizeLayout не выбрасывает ключи вики', () => {
    expect(sanitizeLayout([['wikiInbox', 'wikiJournal']])).toEqual([['wikiInbox', 'wikiJournal']]);
    expect(sanitizeLayout([['wikiHealth'], ['wikiSettings']])).toEqual([['wikiHealth'], ['wikiSettings']]);
  });

  it('ключ вики — валидный ключ панели (isPanelKey), дубли в раскладке снимаются', () => {
    for (const k of WIKI) expect(isPanelKey(k)).toBe(true);
    expect(sanitizeLayout([['wikiInbox', 'wikiInbox'], ['wikiJournal']])).toEqual([['wikiInbox'], ['wikiJournal']]);
  });
});

describe('набор выведен через shell-kit', () => {
  it('export WIKI_PANEL_KEYS есть в ряду экспорта panelCatalog', () => {
    const src = readFileSync(new URL('../../lib/shell-kit/index.ts', import.meta.url), 'utf8');
    expect(src).toMatch(/export \{[^}]*\bWIKI_PANEL_KEYS\b[^}]*\} from '\.\.\/\.\.\/pages\/workspace\/panelCatalog'/);
  });
});
