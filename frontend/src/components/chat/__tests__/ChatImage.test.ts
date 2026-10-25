// Регрессия: картинка проекта в markdown чата вечно висела на «Загрузка изображения…».
// ref из useInView стоял только на финальном <a>, а до загрузки в DOM лишь плейсхолдер без
// ref → IntersectionObserver ничего не наблюдал → inView=false → fetch не стартовал никогда.
// Рефы в renderToStaticMarkup не вызываются, поэтому проверяем сам элемент, который вернул
// ChatImage: в React 19 ref — обычный prop.
// F2a («Резолвинг ссылок по worktree», разрез §4): порядок резолва — индекс → абсолютный
// путь → кэш locate; второго резолвера в ChatImage нет — он лишь передаёт sessionId чата
// в files.content; «ленивое» упоминание не становится ссылкой, пока locate не ответил.
import { describe, it, expect, vi } from 'vitest';
import { createElement, type ReactElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// react-markdown и remark-gfm — настоящие: кейс R-F2a MAJOR-1 проверяет сам рендер
// ссылки через components-хэндлер, а не только чистую функцию резолва.
vi.mock('react-markdown', async (importOriginal) => importOriginal());
vi.mock('remark-gfm', async (importOriginal) => importOriginal());
vi.mock('react-syntax-highlighter', () => ({ Prism: () => null }));
vi.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ oneDark: {} }));
vi.mock('../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../MermaidDiagram', () => ({ MermaidDiagram: () => null }));

// api.files.getContent/locate — шпионы: тест видит ровно то, что отправил компонент.
const net = vi.hoisted(() => ({
  getContent: [] as Array<{ projectId: string; path: string; opts?: { sessionId?: string } }>,
  locate: [] as Array<{ projectId: string; paths: string[]; sessionId?: string }>,
}));
vi.mock('../../../lib/api', () => ({
  api: {
    files: {
      getContent: (projectId: string, path: string, featureId?: string, opts?: { sessionId?: string }) => {
        net.getContent.push({ projectId, path, opts });
        return Promise.resolve({ content: null, isBinary: false, isImage: false });
      },
      locate: (projectId: string, paths: string[], sessionId?: string) => {
        net.locate.push({ projectId, paths, sessionId });
        return Promise.resolve([]);
      },
    },
  },
}));

const view = vi.hoisted(() => ({ ref: () => {}, inView: false }));
vi.mock('../../../lib/useInView', () => ({ useInView: () => [view.ref, view.inView] }));

import { ChatImage, DeferredFileLink, MarkdownContent, resolveFileMention } from '../MarkdownContent';
import { ChatProjectContext, ChatSessionContext, ChatOpenFileContext } from '../contexts';
import type { ProjectFileIndex } from '../../../lib/projectFileIndex';

const PROJECT = { id: 'p1', rootPath: '/home/u/proj' };

function renderChatImage(src: string, sessionId: string | null = null): ReactElement<{ ref?: unknown; children?: unknown }> {
  let out: ReactElement | null = null;
  const Probe = () => { out = ChatImage({ src, alt: 'кот' }) as ReactElement; return null; };
  renderToStaticMarkup(createElement(ChatProjectContext.Provider, { value: PROJECT },
    createElement(ChatSessionContext.Provider, { value: sessionId }, createElement(Probe))));
  return out!;
}

describe('ChatImage', () => {
  it('плейсхолдер до загрузки несёт ref наблюдателя видимости', () => {
    view.inView = false;
    net.getContent.length = 0;
    const el = renderChatImage('.cc-attachments/cat.png');
    expect(el.type).toBe('span');
    expect(el.props.children).toBe('Загрузка изображения…');
    expect(el.props.ref).toBe(view.ref);
  });

  it('loadProjectImage передаёт sessionId чата в files.content', () => {
    view.inView = true;
    net.getContent.length = 0;
    renderChatImage('.cc-attachments/cat.png', 'sess-42');
    expect(net.getContent.length).toBe(1);
    expect(net.getContent[0].path).toBe('.cc-attachments/cat.png');
    // Мутация: если sessionId не передать, эта строка краснеет.
    expect(net.getContent[0].opts?.sessionId).toBe('sess-42');
  });

  it('ключ кэша содержит sessionId: один rel в разных чатах грузится отдельно', () => {
    view.inView = true;
    net.getContent.length = 0;
    renderChatImage('.cc-attachments/cat.png', 'sess-a');
    renderChatImage('.cc-attachments/cat.png', 'sess-b');
    renderChatImage('.cc-attachments/cat.png', 'sess-a'); // тот же ключ — дедуп в полёте
    expect(net.getContent.length).toBe(2);
  });

  it('remote URL не идёт через files.content', () => {
    view.inView = true;
    net.getContent.length = 0;
    renderChatImage('https://fal.media/x.png');
    expect(net.getContent.length).toBe(0);
  });
});

describe('DeferredFileLink — ссылка не рендерится, пока locate не ответил', () => {
  it('ответа нет — обычный текст, ни FileLink, ни запроса', () => {
    net.locate.length = 0;
    const html = renderToStaticMarkup(createElement(ChatProjectContext.Provider, { value: PROJECT },
      createElement(DeferredFileLink, { raw: 'docs/report.md', children: 'report.md' })));
    // Мутация: если рендерить ссылку сразу (без ожидания), обе строки краснеют.
    expect(html).not.toContain('Открыть');
    expect(html).not.toContain('<a ');
    expect(net.locate.length).toBe(0); // в SSR эффект не выполняется — запрос не уходит
  });
});

describe('resolveFileMention — порядок: индекс → абсолютный путь → locate', () => {
  const INDEX: ProjectFileIndex = new Map([
    ['frontend/src/lib/design.ts', 'frontend/src/lib/design.ts'],
  ]);

  it('точное совпадение в индексе — ссылка, locate не зовётся', () => {
    expect(resolveFileMention(INDEX, PROJECT, null, 'frontend/src/lib/design.ts'))
      .toEqual({ path: 'frontend/src/lib/design.ts' });
  });

  it('абсолютный путь — ссылка без проверки индекса (A1)', () => {
    const r = resolveFileMention(INDEX, PROJECT, null, '/home/u/proj/docs/report.md');
    expect(r?.path).toBe('/home/u/proj/docs/report.md');
  });

  it('относительный путь, которого нет в индексе, — lazy, ожидание locate', () => {
    expect(resolveFileMention(INDEX, PROJECT, null, '.cc-attachments/qa/screen.png'))
      .toEqual({ lazy: '.cc-attachments/qa/screen.png' });
  });

  it('голое имя и роут без расширения — не ссылка', () => {
    expect(resolveFileMention(INDEX, PROJECT, null, 'report.md')).toBeNull();
    expect(resolveFileMention(INDEX, PROJECT, null, '/api/mcp/calls')).toBeNull();
  });

  // R-F2a MAJOR-1: внешний URL похож на путь («//host/…» — даже на абсолютный), но
  // дереву не принадлежит: резолв обязан отсечь его до lazy-ветки.
  it('внешний URL — не путь дерева: ни ссылка, ни lazy', () => {
    expect(resolveFileMention(INDEX, PROJECT, null, 'https://example.com/guide.md')).toBeNull();
    expect(resolveFileMention(INDEX, PROJECT, null, 'www.example.com/guide.md')).toBeNull();
    expect(resolveFileMention(INDEX, PROJECT, null, '//cdn.example.com/x.png')).toBeNull();
  });
});

describe('Внешняя ссылка остаётся ссылкой (R-F2a MAJOR-1)', () => {
  it('[x](https://example.com/guide.md) рендерится <a>, locate не зовётся', () => {
    net.locate.length = 0;
    const html = renderToStaticMarkup(createElement(ChatProjectContext.Provider, { value: PROJECT },
      createElement(ChatOpenFileContext.Provider, { value: () => undefined },
        createElement(MarkdownContent, { text: '[x](https://example.com/guide.md)' }))));
    // Мутация: без отсечения URL в resolveFileMention href уходит в DeferredFileLink —
    // остаётся голый текст, проверка <a> краснеет.
    expect(html).toContain('<a href="https://example.com/guide.md"');
    expect(net.locate.length).toBe(0);
  });
});
