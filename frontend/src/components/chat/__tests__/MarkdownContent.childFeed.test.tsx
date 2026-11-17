// К8a («Знания репетитора», §8 п.5): детская лента не несёт ссылок наружу и внешних
// картинок. С ChildFeedContext=true markdown-ссылка рисуется текстом без <a>, внешняя
// картинка (http(s): и /api/proxy) заменяется подписью alt, data: остаётся картинкой.
// Без провайдера (взрослая лента) — ссылки и картинки рисуются как раньше (регрессия).
import { describe, it, expect, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Шпионы — как в ChatImage.test.ts: рендер настоящий (react-markdown/remark-gfm),
// сеть и наблюдатель видимости — заглушки.
vi.mock('react-markdown', async (importOriginal) => importOriginal());
vi.mock('remark-gfm', async (importOriginal) => importOriginal());
vi.mock('react-syntax-highlighter', () => ({ Prism: () => null }));
vi.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ oneDark: {} }));
vi.mock('../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../../lib/api', () => ({
  api: {
    files: {
      getContent: () => Promise.resolve({ content: null, isBinary: false, isImage: false }),
      locate: () => Promise.resolve([]),
    },
  },
}));
vi.mock('../../../lib/useInView', () => ({ useInView: () => [() => {}, false] }));
// readStoredToken — без хранилища (среда node): /api/proxy строится без access_token
vi.mock('../../../lib/offline', () => ({ readStoredToken: () => null }));

import { MarkdownContent } from '../MarkdownContent';
import { ChildFeedContext } from '../contexts';

function render(text: string, childFeed?: boolean): string {
  const md = createElement(MarkdownContent, { text });
  // childFeed === undefined — БЕЗ провайдера: так же, как во взрослой ленте (дефолт false)
  const tree = childFeed === undefined ? md : createElement(ChildFeedContext.Provider, { value: childFeed }, md);
  return renderToStaticMarkup(tree);
}

const count = (html: string, re: RegExp) => (html.match(re) ?? []).length;

describe('MarkdownContent в детской ленте (К8a, §8 п.5)', () => {
  it('ссылка наружу — текст без <a>', () => {
    const html = render('[сайт](https://example.com)', true);
    // Мутация «игнорировать ChildFeedContext»: <a> возвращается, эта строка краснеет.
    expect(count(html, /<a\b/g)).toBe(0);
    expect(html).toContain('сайт');
  });

  it('внешняя картинка http(s) не грузится — вместо неё подпись alt', () => {
    const html = render('![котик](https://e.com/a.png)', true);
    expect(count(html, /<img\b/g)).toBe(0);
    expect(html).toContain('котик');
  });

  it('картинка через /api/proxy — тоже остаётся подписью', () => {
    // res.cloudinary.com проксируется (в AllowedHosts), значит src приходит уже как
    // /api/proxy?… — гейт обязан накрыть и эту форму, не только голый https:
    const html = render('![открытый](https://res.cloudinary.com/x.png)', true);
    expect(count(html, /<img\b/g)).toBe(0);
    expect(html).toContain('открытый');
  });

  it('data: остаётся картинкой', () => {
    // defaultUrlTransform режет data: (safeProtocol его не содержит) — в детской ленте
    // urlTransform пропускает data:image намеренно; без этой ветки проверка краснеет.
    const html = render('![png](data:image/png;base64,iVBORw0KGgo=)', true);
    expect(count(html, /<img\b/g)).toBe(1);
  });

  it('без провайдера — взрослая лента как раньше: ссылка остаётся <a>', () => {
    const html = render('[сайт](https://example.com)');
    expect(count(html, /<a href="https:\/\/example\.com"/g)).toBe(1);
  });

  it('без провайдера внешняя картинка остаётся <img>', () => {
    const html = render('![котик](https://e.com/a.png)');
    expect(count(html, /<img\b/g)).toBe(1);
  });
});
