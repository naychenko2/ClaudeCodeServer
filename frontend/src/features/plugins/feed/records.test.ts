// Slug из ленты — недоверенный ввод: isDraftSlug + DraftAnchor с невалидным slug.
// Запись plugin_draft несёт ГОЛЫЙ slug (без publisher): валидный голый рисует карточку,
// а id с «/» (publisher/slug), ../x, пробел, пустой, 200 символов, не-строка →
// нейтральная строка «Черновик больше недоступен», без запросов и кнопок.

import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { isDraftSlug, DraftAnchor } from './records';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem } from '../../../types';

// Минимальный ctx: item с module_record и заданным slug
function ctxWithSlug(slug: unknown): ChatItemToolCtx {
  const item = {
    kind: 'module_record',
    recordType: 'plugin_draft',
    data: { slug },
    fallback: null,
  } as unknown as ChatItem;
  return { item, projectId: 'p1' };
}

describe('isDraftSlug', () => {
  it('валидные: голый slug (без publisher, без «/»)', () => {
    expect(isDraftSlug('my-plugin')).toBe(true);
    expect(isDraftSlug('a')).toBe(true);
    expect(isDraftSlug('a-b-c-123')).toBe(true);
  });

  it('невалидные: id с «/», ../x, пробел, пустой, 200 символов, не-строка', () => {
    expect(isDraftSlug('local-1/my-plugin')).toBe(false); // с «/» — это id пакета, не голый slug
    expect(isDraftSlug('../x')).toBe(false);
    expect(isDraftSlug('a b')).toBe(false); // пробел
    expect(isDraftSlug('')).toBe(false);
    expect(isDraftSlug('a'.repeat(200))).toBe(false);
    expect(isDraftSlug(42)).toBe(false);
    expect(isDraftSlug(null)).toBe(false);
    expect(isDraftSlug(undefined)).toBe(false);
    expect(isDraftSlug({})).toBe(false);
  });
});

describe('DraftAnchor с невалидным slug', () => {
  it('id с «/» (local-1/my-plugin) → нейтральная строка, без DraftFeedCard', () => {
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug('local-1/my-plugin') }));
    expect(html).toContain('Черновик больше недоступен');
  });

  it('../x → нейтральная строка, без DraftFeedCard', () => {
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug('../x') }));
    expect(html).toContain('Черновик больше недоступен');
  });

  it('пробел → нейтральная строка', () => {
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug('a b') }));
    expect(html).toContain('Черновик больше недоступен');
  });

  it('пустая строка → нейтральная строка', () => {
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug('') }));
    expect(html).toContain('Черновик больше недоступен');
  });

  it('200 символов → нейтральная строка', () => {
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug('a'.repeat(200)) }));
    expect(html).toContain('Черновик больше недоступен');
  });

  it('не-строка (число) → нейтральная строка', () => {
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug(42) }));
    expect(html).toContain('Черновик больше недоступен');
  });

  it('валидный голый slug → DraftFeedCard (не нейтральная строка)', () => {
    // DraftFeedCard — async-компонент; в SSR (effects не бегут) запросов нет,
    // главное — НЕ «Черновик больше недоступен» (иначе slug сочли бы невалидным).
    const html = renderToStaticMarkup(createElement(DraftAnchor, { ctx: ctxWithSlug('my-plugin') }));
    expect(html).not.toContain('Черновик больше недоступен');
  });
});
