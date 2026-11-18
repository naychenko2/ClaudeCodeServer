// К8a («Знания репетитора», §8 п.5): детская лента не несёт ссылок наружу и внешних
// картинок. С ChildFeedContext=true markdown-ссылка рисуется текстом без <a>, внешняя
// картинка (http(s): и /api/proxy) заменяется подписью alt, data: остаётся картинкой.
// Без провайдера (взрослая лента) — ссылки и картинки рисуются как раньше (регрессия).
// Вторая группа — перерендер одного экземпляра ChatImage (ревью 6b50faac о Rules of Hooks).
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Управляемые шпионы: inView для useInView (по умолчанию false — как раньше) и счётчик
// getContent — тест перерендера судит по нему, был ли повторный запрос после ремоунта.
const ctl = vi.hoisted(() => ({ inView: false, contentCalls: 0 }));

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
      getContent: () => {
        ctl.contentCalls++;
        return Promise.resolve({ content: null, isBinary: false, isImage: false });
      },
      locate: () => Promise.resolve([]),
    },
  },
}));
vi.mock('../../../lib/useInView', () => ({ useInView: () => [() => {}, ctl.inView] }));
// readStoredToken — без хранилища (среда node): /api/proxy строится без access_token
vi.mock('../../../lib/offline', () => ({ readStoredToken: () => null }));

import { ChatImage, MarkdownContent } from '../MarkdownContent';
import { ChatProjectContext, ChildFeedContext } from '../contexts';
import { installFakeDom, mount, textNodes } from './fakeDom';

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

// Ревью 6b50faac: ранний return в ChatImage (детская лента) стоял ДО useState/useInView —
// при перерендере того же экземпляра (смена src или провайдера без ремоунта) он нарушал
// Rules of Hooks. В React 19.2.8 переходы 0↔N хуков не бросают «Rendered fewer/more hooks»
// (0-хуковый render = mount, «fewer» проверяется только после хотя бы одного вызванного
// хука), но состояние экземпляра при этом всё равно теряется: после blocked-перехода
// компонент перемонтируется с чистыми useState. Тесты ниже — перерендер одного экземпляра
// через createRoot (fakeDom): первые два фиксируют отсутствие ошибок и корректный DOM,
// третий — то, что состояние (failed) переживает blocked-переход и повторного запроса нет.
describe('перерендер одного ChatImage (исправление Rules of Hooks)', () => {
  const childTree = (src: string) =>
    createElement(ChildFeedContext.Provider, { value: true }, createElement(ChatImage, { src, alt: 'котик' }));

  it('смена src с внешнего на data: — без ошибки, картинка появляется', () => {
    installFakeDom();
    const { root, container } = mount(childTree('https://e.com/a.png'));
    // Детская лента: внешняя картинка — подпись alt, <img> нет
    expect(container.querySelectorAll('img').length).toBe(0);
    expect(textNodes(container)).toContain('котик');
    // Тот же экземпляр, src → data:. Со старым ранним return это был бы ремоунт с чистыми
    // useState (в React 19.2.8 без броска — см. комментарий выше); DOM обязан быть верным.
    act(() => { root.render(childTree('data:image/png;base64,iVBORw0KGgo=')); });
    const imgs = container.querySelectorAll('img');
    expect(imgs.length).toBe(1);
    expect(imgs[0].getAttribute('src')).toContain('data:image/png');
  });

  it('смена провайдера false→true без ремоунта — без ошибки, картинка становится alt', () => {
    installFakeDom(); // сам, а не побочным эффектом предыдущего теста: прогон по filter — один
    const adult = (feed: boolean) =>
      createElement(ChildFeedContext.Provider, { value: feed }, createElement(ChatImage, { src: 'https://e.com/a.png', alt: 'котик' }));
    const { root, container } = mount(adult(false));
    // Взрослая лента: внешняя картинка — <img>
    expect(container.querySelectorAll('img').length).toBe(1);
    // Та же позиция, провайдер → детская лента: alt вместо картинки, без ошибок.
    act(() => { root.render(adult(true)); });
    expect(container.querySelectorAll('img').length).toBe(0);
    expect(textNodes(container)).toContain('котик');
  });

  it('failed-состояние локальной картинки переживает blocked-переход — без повторного запроса', async () => {
    installFakeDom();
    ctl.inView = true; // fetch разрешён — как с реальным IntersectionObserver в кадре
    const project = { id: 'p1', rootPath: '/root' };
    const feed = (src: string) =>
      createElement(ChatProjectContext.Provider, { value: project },
        createElement(ChildFeedContext.Provider, { value: true }, createElement(ChatImage, { src, alt: 'котик' })));
    const calls0 = ctl.contentCalls;
    try {
      const { root, container } = mount(feed('/root/img.png'));
      await act(async () => {}); // дренируем цепочку fetch: ответ не картинка → failed
      expect(ctl.contentCalls - calls0).toBe(1);
      expect(textNodes(container).join('')).toContain('🖼 котик');
      // Тот же экземпляр: внешняя src (blocked) → обратно тот же локальный путь.
      await act(async () => { root.render(feed('https://e.com/a.png')); });
      expect(textNodes(container).join('')).toContain('котик');
      await act(async () => { root.render(feed('/root/img.png')); });
      // С безусловными хуками failed переживает переход: плейсхолдер тот же, второго запроса нет.
      // Со старым ранним return экземпляр перемонтировался с чистыми useState → повторный
      // getContent (счётчик 2) — тест краснеет.
      expect(ctl.contentCalls - calls0).toBe(1);
      expect(textNodes(container).join('')).toContain('🖼 котик');
    } finally {
      ctl.inView = false;
    }
  });
});
