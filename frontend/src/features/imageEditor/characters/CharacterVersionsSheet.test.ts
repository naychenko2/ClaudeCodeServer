// Шторка «Версии»: счётчик «N из 5», подпись своей LoRA без пути ccs/…, hideTitle прячет заголовок списка.
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { versionsCounter } from './CharacterVersionsSheet';
import { CharacterLoraVersions } from './CharacterLoraVersions';
import { VERSIONS_TEXT } from './loraTrainTexts';
import { VERSIONS_OWN } from './characterTexts';
import type { LoraVersionsHandle } from './useLoraVersions';

const FULL = 'ccs/u1/andrey/andrey_v1.safetensors';
const list = { available: true, items: [FULL], trained: { [FULL]: '2026-10-03T14:20:00Z' } };
const handle = (own: boolean) => ({
  data: { items: [], own: own ? { lora: FULL, trainedAt: '2026-10-03T14:20:00Z' } : null, limit: 5, used: 2 },
  ctl: null, pending: [], error: null, loading: false,
}) as unknown as LoraVersionsHandle;
const render = (hideTitle: boolean, own = true) => renderToStaticMarkup(createElement(CharacterLoraVersions, {
  loraList: list, versions: handle(own), hideTitle,
}));

describe('versionsCounter', () => {
  it('«N из 5»', () => expect(versionsCounter(2, 5)).toBe('2 из 5'));
  it('лимит не пришёл — пять', () => expect(versionsCounter(1, undefined)).toBe('1 из 5'));
});

describe('подпись своей LoRA в шторке', () => {
  it('имя и дата раздельно, без пути ccs/…', () => {
    const h = render(true);
    expect(h).toMatch(/data-lora-name[^>]*>andrey_v1</);
    expect(h).toMatch(/data-lora-date[^>]*>· 3 октября/);
    expect(h).not.toContain('ccs/');
  });
  it('в карточке подпись одной строкой', () => expect(render(false)).not.toContain('data-lora-name'));
});

describe('CharacterLoraVersions hideTitle', () => {
  it('без hideTitle заголовок на месте', () => expect(render(false)).toContain(VERSIONS_TEXT.title));
  it('с hideTitle заголовка нет, а строка про свою версию — новая', () => {
    const h = render(true);
    expect(h).not.toContain(VERSIONS_TEXT.title);
    expect(h).toContain(VERSIONS_OWN.title);
    expect(h).not.toContain(VERSIONS_TEXT.own);
    expect(h).not.toContain('ccs/');
  });
});
