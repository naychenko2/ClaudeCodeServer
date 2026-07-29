import { describe, it, expect } from 'vitest';
import { slugify, splitAnchor, isExternal, resolveRelative, resolveDocLink, sliceSection } from './docsLinks';

// Контракт с сервером: те же правила лежат в Services/Docs/DocsIndexService.
// Расхождение алгоритмов ломает переходы по якорям — поэтому набор дублирует
// серверные кейсы (DocsIndexTests), а не проверяет что-то своё.
describe('slugify', () => {
  it('снимает markdown-разметку и нормализует разделители', () => {
    expect(slugify('Инварианты `SafeJoin` и **границы**')).toBe('инварианты-safejoin-и-границы');
  });

  it('схлопывает повторы дефисов и обрезает края', () => {
    expect(slugify('  — Раздел: (важный) — ')).toBe('раздел-важный');
  });

  it('идемпотентен на готовом слаге', () => {
    expect(slugify('первый-раздел')).toBe('первый-раздел');
  });
});

describe('splitAnchor', () => {
  it('делит путь и якорь, нормализуя якорь', () => {
    expect(splitAnchor('./b.md#Первый-Раздел')).toEqual(['./b.md', 'первый-раздел']);
  });

  it('без якоря возвращает null', () => {
    expect(splitAnchor('docs/a.md')).toEqual(['docs/a.md', null]);
  });

  it('декодирует процент-энкодинг кириллицы', () => {
    // Так href приезжает из remark: без decode слаг был мусорным и раздел не находился
    expect(splitAnchor('setup.md#retention-%D1%81%D1%80%D0%BE%D0%BA-%D1%85%D1%80%D0%B0%D0%BD%D0%B5%D0%BD%D0%B8%D1%8F'))
      .toEqual(['setup.md', 'retention-срок-хранения']);
  });

  it('якорь энкодленной ссылки совпадает со слагом заголовка', () => {
    const fromLink = splitAnchor('x.md#%D0%9E%D0%B1%D0%B7%D0%BE%D1%80')[1];
    expect(fromLink).toBe(slugify('Обзор'));
  });
});

describe('isExternal', () => {
  it.each(['https://example.com', 'http://x.dev', 'mailto:a@b.c', '//cdn.example'])(
    'внешняя: %s', (href) => expect(isExternal(href)).toBe(true));

  it.each(['./a.md', 'docs/a.md', '/docs/a.md', '#якорь'])(
    'не внешняя: %s', (href) => expect(isExternal(href)).toBe(false));
});

describe('resolveRelative', () => {
  it('резолвит относительно папки документа', () => {
    expect(resolveRelative('docs/adr/0001.md', './0002.md')).toBe('docs/adr/0002.md');
    expect(resolveRelative('docs/adr/0001.md', '../sandbox.md')).toBe('docs/sandbox.md');
  });

  it('путь от корня начинается со слэша', () => {
    expect(resolveRelative('docs/a.md', '/README.md')).toBe('README.md');
  });

  it('выход выше корня проекта — null', () => {
    expect(resolveRelative('docs/a.md', '../../secret.md')).toBeNull();
  });
});

describe('resolveDocLink', () => {
  const known = new Set(['readme.md', 'docs/architecture.md']);

  it('ссылка на документ области — doc', () => {
    expect(resolveDocLink('README.md', './docs/architecture.md#обзор', known))
      .toEqual({ kind: 'doc', target: 'docs/architecture.md', anchor: 'обзор' });
  });

  it('ссылка на файл вне области — repo', () => {
    expect(resolveDocLink('README.md', 'backend/Program.cs', known))
      .toEqual({ kind: 'repo', target: 'backend/Program.cs', anchor: null });
  });

  it('якорь без пути ведёт в текущий документ', () => {
    expect(resolveDocLink('docs/architecture.md', '#инварианты', known))
      .toEqual({ kind: 'doc', target: 'docs/architecture.md', anchor: 'инварианты' });
  });

  it('внешняя ссылка отдаётся как есть', () => {
    expect(resolveDocLink('README.md', 'https://example.com', known))
      .toEqual({ kind: 'external', target: 'https://example.com', anchor: null });
  });
});

describe('sliceSection', () => {
  const md = [
    '# Документ',
    '',
    '## Первый раздел',
    'текст первого',
    '',
    '### Подраздел',
    'вложенный текст',
    '',
    '## Второй раздел',
    'текст второго',
  ].join('\n');

  it('режет от заголовка до следующего того же уровня, забирая подразделы', () => {
    const section = sliceSection(md, 'первый-раздел');
    expect(section).toContain('текст первого');
    expect(section).toContain('вложенный текст');
    expect(section).not.toContain('текст второго');
  });

  it('последний раздел режется до конца документа', () => {
    expect(sliceSection(md, 'второй-раздел')).toContain('текст второго');
  });

  it('сохраняет разметку исходного markdown', () => {
    const withCode = ['## Сборка', '', '```bash', 'dotnet build', '```'].join('\n');
    expect(sliceSection(withCode, 'сборка')).toContain('```bash');
  });

  it('заголовок внутри блока кода не считается началом раздела', () => {
    const tricky = ['```', '## Не заголовок', '```', '', '## Настоящий', 'тело'].join('\n');
    expect(sliceSection(tricky, 'не-заголовок')).toBeNull();
    expect(sliceSection(tricky, 'настоящий')).toContain('тело');
  });

  it('неизвестный слаг — null', () => {
    expect(sliceSection(md, 'нет-такого')).toBeNull();
  });
});
