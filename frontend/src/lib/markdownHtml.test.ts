// Санитайз HTML в markdown: что проходит и что вырезается.
//
// Тест гоняет ту же цепочку, что и MarkdownViewer (remark → rehype-raw → rehype-sanitize),
// но без React — схема это правило безопасности, и проверяться оно должно отдельно
// от рендера.

import { describe, it, expect } from 'vitest';
import { unified } from 'unified';
import remarkParse from 'remark-parse';
import remarkRehype from 'remark-rehype';
import rehypeRaw from 'rehype-raw';
import rehypeSanitize from 'rehype-sanitize';
import rehypeStringify from 'rehype-stringify';
import { HTML_SCHEMA } from './markdownHtml';

const render = (md: string) => String(unified()
  .use(remarkParse)
  .use(remarkRehype, { allowDangerousHtml: true })
  .use(rehypeRaw)
  .use(rehypeSanitize, HTML_SCHEMA)
  .use(rehypeStringify)
  .processSync(md));

describe('HTML в markdown — что проходит', () => {
  it('центрирование логотипа README: div align + img с размерами', () => {
    const html = render('<div align="center"><img src="logo.png" width="88" height="88" alt="Logo" /></div>');

    expect(html).toContain('<div align="center">');
    expect(html).toContain('src="logo.png"');
    expect(html).toContain('width="88"');
  });

  it('details/summary — свёрнутые разделы README', () => {
    const html = render('<details><summary>Подробности</summary>\n\nтекст\n\n</details>');

    expect(html).toContain('<details>');
    expect(html).toContain('<summary>Подробности</summary>');
  });

  it('внутренние схемы ссылок продукта переживают санитайз', () => {
    // Их подставляет препроцессинг заметок; вырезанный href убил бы вики-переходы
    const html = render('[к заметке](wikilink:%D0%90) и ![вложение](noteatt:img.png)');

    expect(html).toContain('href="wikilink:%D0%90"');
    expect(html).toContain('src="noteatt:img.png"');
  });
});

describe('HTML в markdown — что вырезается', () => {
  it('script не исполняется и не остаётся в дереве', () => {
    const html = render('текст\n\n<script>alert(1)</script>');

    expect(html).not.toContain('<script');
    expect(html).not.toContain('alert(1)');
  });

  it('обработчики событий срезаются с уцелевшего тега', () => {
    const html = render('<img src="x.png" onerror="alert(1)" alt="x" />');

    expect(html).toContain('<img');
    expect(html).not.toContain('onerror');
  });

  it('javascript:-ссылка теряет href', () => {
    const html = render('[клик](javascript:alert(1))');

    expect(html).not.toContain('javascript:');
  });

  it('style и iframe не проходят', () => {
    const html = render('<style>body{display:none}</style>\n\n<iframe src="https://example.com"></iframe>');

    expect(html).not.toContain('<style');
    expect(html).not.toContain('<iframe');
  });

  it('форму собрать нельзя: form вырезан, а поле обезврежено до disabled-чекбокса', () => {
    const html = render('<form action="https://evil.example"><input name="password" type="password" /></form>');

    expect(html).not.toContain('<form');
    // input в defaultSchema оставлен ради списков задач GitHub, но только как
    // выключенный чекбокс: ни ввести, ни отправить в нём нечего
    expect(html).toContain('disabled');
    expect(html).toContain('type="checkbox"');
    expect(html).not.toContain('type="password"');
  });
});
