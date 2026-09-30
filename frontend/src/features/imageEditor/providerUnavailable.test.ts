// Заведённый, но лежащий поставщик (available: false): пометка «недоступен» в обоих выборах,
// пункт остаётся выбираемым; старые ответы без поля — без пометки
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ImageEditCatalog, ImageEditProvider } from './api';
import { ProviderItems } from './ProviderModelPicker';
import { ProviderOpts } from './strip/ImagesStrip';
import { providerTitle, unavailableMark } from './format';

const FAL: ImageEditProvider = { key: 'fal', label: 'fal', priceUnit: 'usd', models: [{ id: 'auto', label: 'Авто' }] };
const local = (available?: boolean): ImageEditProvider =>
  ({ key: 'local', label: 'Локальные модели', priceUnit: 'free', models: [{ id: 'auto', label: 'Авто' }], ...(available === undefined ? {} : { available }) });
const catalog = (lp: ImageEditProvider): ImageEditCatalog => ({
  default: { provider: 'local', model: 'auto' }, providers: [lp, FAL],
  limits: { maxFileMb: 20, maxReferences: 4, maxCount: 4 }, reason: null,
});

const picker = (c: ImageEditCatalog) =>
  renderToStaticMarkup(createElement(ProviderItems, { catalog: c, value: 'settings', onPick: () => {} }));
const strip = (c: ImageEditCatalog) =>
  renderToStaticMarkup(createElement(ProviderOpts, { catalog: c, choice: 'settings', onPick: () => {} }));
// Кнопки выбора поставщика в полосе «Картинки» — по одной на пункт
const buttons = (html: string) => html.match(/<button[^>]*>.*?<\/button>/g) ?? [];

describe('поставщик с available: false', () => {
  it('подпись и пометка', () => {
    expect(unavailableMark(local(false))).toBe('недоступен');
    expect(providerTitle(local(false))).toBe('Локальные модели (недоступен)');
    for (const p of [local(), local(true)]) {
      expect(unavailableMark(p)).toBe('');
      expect(providerTitle(p)).toBe('Локальные модели');
    }
  });

  it('выбор поставщика в редакторе: пометка у пункта и у «Как в настройках», пункты не заблокированы', () => {
    const html = picker(catalog(local(false)));
    expect(html.match(/недоступен/g)).toHaveLength(2);
    expect(html).not.toMatch(/disabled|aria-disabled="true"/);
  });

  it('полоса «Картинки»: «сейчас Локальные модели (недоступен)», кнопка остаётся кликабельной', () => {
    const html = strip(catalog(local(false)));
    expect(html).toContain('сейчас Локальные модели (недоступен)');
    expect(html).toContain('недоступен · бесплатно, на своей видеокарте');
    const btns = buttons(html);
    expect(btns).toHaveLength(3);
    btns.forEach(b => expect(b).not.toMatch(/disabled/));
  });

  it('поле не пришло или true — без пометки', () => {
    for (const c of [catalog(local()), catalog(local(true))]) {
      expect(picker(c)).not.toContain('недоступен');
      expect(strip(c)).not.toContain('недоступен');
    }
  });
});
