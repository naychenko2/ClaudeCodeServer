// Заведённый, но лежащий поставщик (available: false): пометка «не отвечает» цветом
// предупреждения в обоих выборах, пункт остаётся выбираемым; старые ответы без поля — без пометки
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ImageEditCatalog, ImageEditProvider } from './api';
import { ProviderItems } from './ProviderModelPicker';
import { ProviderOpts } from './strip/settings/ProviderSection';
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
// Пометка отдельным span цвета предупреждения (токен C.warningText)
const warn = (text: string) => new RegExp(`<span style="[^"]*color:var\\(--c-warning-text\\)[^"]*">(<svg.*?</svg>)?${text}</span>`);

describe('поставщик с available: false', () => {
  it('подпись и пометка', () => {
    expect(unavailableMark(local(false))).toBe('не отвечает');
    expect(providerTitle(local(false))).toBe('Локальные модели (не отвечает)');
    for (const p of [local(), local(true)]) {
      expect(unavailableMark(p)).toBe('');
      expect(providerTitle(p)).toBe('Локальные модели');
    }
  });

  it('выбор поставщика в редакторе: пометка у пункта и у «Как в настройках», пункты не заблокированы', () => {
    const html = picker(catalog(local(false)));
    expect(html.match(/не отвечает/g)).toHaveLength(2);
    expect(html.match(new RegExp(warn('не отвечает').source, 'g'))).toHaveLength(2);
    expect(html).toMatch(new RegExp(`сейчас Локальные модели — выбрал администратор.*${warn('не отвечает').source}`));
    expect(html.match(/lucide-triangle-alert|lucide-alert-triangle/g)).toHaveLength(2);
    expect(html).not.toMatch(/disabled|aria-disabled="true"/);
  });

  it('полоса «Картинки»: пометка отдельным span, остальная подсказка прежняя, кнопка кликабельна', () => {
    const html = strip(catalog(local(false)));
    expect(html).toMatch(new RegExp(`сейчас Локальные модели · ${warn('не отвечает').source}`));
    expect(html).toMatch(new RegExp(`${warn('не отвечает').source} · бесплатно, на своей видеокарте`));
    const btns = buttons(html);
    expect(btns).toHaveLength(3);
    btns.forEach(b => expect(b).not.toMatch(/disabled/));
  });

  it('поле не пришло или true — без пометки', () => {
    for (const c of [catalog(local()), catalog(local(true))]) {
      expect(picker(c)).not.toContain('не отвечает');
      expect(strip(c)).not.toContain('не отвечает');
    }
  });
});
