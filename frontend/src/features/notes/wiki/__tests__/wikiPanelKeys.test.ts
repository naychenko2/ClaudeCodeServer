// W3: таблица «подвкладка вики → ключ панели в реестре зон». Подпись и значок панели в рельсе
// обязаны совпадать со строкой WIKI_SUBTABS — иначе мобильная шторка и десктопная рельса
// называют одно и то же по-разному.

import { describe, expect, it } from 'vitest';
import { PANEL_META, WIKI_PANEL_KEYS } from '../../../../pages/workspace/panelCatalog';
import { WIKI_SUBTABS, WIKI_SUBTAB_PANEL } from '../wikiTabs';

describe('WIKI_SUBTAB_PANEL: подпись и значок панели равны строке WIKI_SUBTABS', () => {
  const rows = WIKI_SUBTABS.filter(t => t.id !== 'pages');

  it('у каждой подвкладки, кроме «Страниц», есть панель; ключи панелей — ровно WIKI_PANEL_KEYS', () => {
    expect(rows.map(t => WIKI_SUBTAB_PANEL[t.id as keyof typeof WIKI_SUBTAB_PANEL]).sort())
      .toEqual([...WIKI_PANEL_KEYS].sort());
  });

  for (const tab of rows) {
    it(`«${tab.label}»: PANEL_META[${WIKI_SUBTAB_PANEL[tab.id as keyof typeof WIKI_SUBTAB_PANEL]}]`, () => {
      const meta = PANEL_META[WIKI_SUBTAB_PANEL[tab.id as keyof typeof WIKI_SUBTAB_PANEL]];
      expect(meta.title).toBe(tab.label);
      expect(meta.Icon).toBe(tab.icon);
    });
  }
});
