import { describe, expect, it } from 'vitest';
import { purgeLegacyComposerKeys } from './legacyKeys';

function fakeStorage(init: Record<string, string>) {
  const m = new Map(Object.entries(init));
  return {
    get length() { return m.size; },
    key: (i: number) => [...m.keys()][i] ?? null,
    removeItem: (k: string) => { m.delete(k); },
    keys: () => [...m.keys()],
  };
}

describe('чистка ключей прежних полос', () => {
  it('убирает выбор полосы и свёрнутость, чужие ключи не трогает', () => {
    const s = fakeStorage({
      'cc-composer-strip:s1': 'images',
      'cc-composer-strip-collapsed:s1:images': '1',
      'cc_gen_panel_dismissed': '[]',
      'cc_composer_hidden': '[]',
    });
    expect(purgeLegacyComposerKeys(s)).toBe(2);
    expect(s.keys()).toEqual(['cc_gen_panel_dismissed', 'cc_composer_hidden']);
  });

  it('пустое хранилище — нечего убирать', () => {
    expect(purgeLegacyComposerKeys(fakeStorage({}))).toBe(0);
  });
});
