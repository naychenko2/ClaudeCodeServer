// Сторож класса дефекта «мусорный текст из JSX-комментария».
//
// Что происходит: `//` или `/*` внутри JSX-разметки — это НЕ комментарий.
// JSX ждёт выражения в `{...}`; текст между тегами попадает в DOM как есть.
// То есть строка
//
//   <div>
//     // тут комментарий
//   </div>
//
// отрисует в панели видимый мусор («// тут комментарий»). Правило eslint
// react/jsx-no-comment-textnodes ловило этот класс, но его единственный
// носитель eslint-plugin-react@7.37.5 требует peer eslint ≤ 9.7, а в
// проекте eslint 10.x — npm ci падал с ERESOLVE, и плагин убрали.
//
// Поэтому тот же класс закрывает этот тест: он разбирает ВСЕ .tsx в
// frontend/src через TypeScript-компилятор и ищет JSX-текстовые узлы
// (ts.isJsxText), в которых встречается `//` или `/*`. Правильный способ
// оставить комментарий в JSX — `{/* ... */}`; такой комментарий парсится
// как выражение (JsxExpression), а не как текст, поэтому в находки не
// попадает.
//
// Что проверяем:
//   1. Обход src/ находит .tsx-файлы (тест не протух из-за смены путей).
//   2. Ни один production-файл не содержит JSX-текста с `//` или `/*`.
//   3. Сам детектор ловит эталонный дефект — иначе тест зелёный вхолостую.

import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import ts from 'typescript';

const FRONTEND_SRC = join(process.cwd(), 'src');

// `//` внутри JSX-текста. Исключаем `://` — это схема в URL
// (https://example.com), мусором в разметке она не является.
const JSX_COMMENT_RE = /(^|\s)(\/\/|\/\*)/;

function listTsxFiles(root: string): string[] {
  const out: string[] = [];
  function walk(dir: string): void {
    let entries: string[];
    try { entries = readdirSync(dir); }
    catch { return; }
    for (const name of entries) {
      const full = join(dir, name);
      let st;
      try { st = statSync(full); }
      catch { continue; }
      if (st.isDirectory()) {
        if (name === 'node_modules' || name === 'dist' || name === 'dev-dist') continue;
        walk(full);
      } else if (st.isFile() && name.endsWith('.tsx')) {
        out.push(full);
      }
    }
  }
  walk(root);
  return out;
}

interface JsxTextHit {
  file: string;
  line: number;
  text: string;
}

// Комментарий в JSX-разметке = текстовый узел, содержащий `//` или `/*`.
// Комментарий в фигурных скобках ({/* ... */}) парсится как JsxExpression
// и в находки не попадает — так и должно быть.
function scanJsxTextComments(file: string): JsxTextHit[] {
  let src: string;
  try { src = readFileSync(file, 'utf8'); }
  catch { return []; }
  const sf = ts.createSourceFile(file, src, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const hits: JsxTextHit[] = [];
  (function walk(node: ts.Node): void {
    if (ts.isJsxText(node)) {
      for (const line of node.text.split('\n')) {
        if (!JSX_COMMENT_RE.test(line)) continue;
        if (line.includes('://')) continue; // схема в URL
        const { line: ln } = sf.getLineAndCharacterOfPosition(node.getStart(sf));
        hits.push({ file, line: ln + 1, text: line.trim() });
      }
    }
    ts.forEachChild(node, walk);
  })(sf);
  return hits;
}

describe('сторож: в JSX-разметке нет текстовых «комментариев» (`//`, `/*`)', () => {
  const files = listTsxFiles(FRONTEND_SRC);

  it('обход src/ находит .tsx-файлы', () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it('ни один production-.tsx не содержит мусорного текста в JSX', () => {
    const violations: JsxTextHit[] = [];
    for (const file of files) {
      // Тесты законно держат примеры разметки в фикстурах.
      if (file.includes(`${'__tests__'}/`) || file.includes('.test.')) continue;
      violations.push(...scanJsxTextComments(file));
    }
    expect(
      violations,
      'В JSX-разметке найден `//` или `/*` — это не комментарий, а текст, '
        + 'человек увидит мусор в панели. Пишите комментарий как {/* ... */}: '
        + violations.map(v => `${relative(process.cwd(), v.file)}:${v.line} — ${v.text}`).join('\n'),
    ).toEqual([]);
  });

  it('детектор ловит эталонный дефект (проверка самого теста)', () => {
    const broken = [
      'const a = (',
      '  <div>',
      '    // это не комментарий, а текст',
      '    <span>x</span>',
      '  </div>',
      ');',
    ].join('\n');
    const sf = ts.createSourceFile('broken.tsx', broken, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const texts: string[] = [];
    (function walk(node: ts.Node): void {
      if (ts.isJsxText(node)) texts.push(node.text);
      ts.forEachChild(node, walk);
    })(sf);
    const found = texts.some(t => t.split('\n').some(line => JSX_COMMENT_RE.test(line)));
    expect(found, 'детектор обязан видеть `//` в JSX-тексте').toBe(true);
  });

  it('корректный комментарий {/* ... */} детектором не считается мусором', () => {
    const ok = [
      'const a = (',
      '  <div>',
      '    {/* это настоящий комментарий */}',
      '    <span>x</span>',
      '  </div>',
      ');',
    ].join('\n');
    const sf = ts.createSourceFile('ok.tsx', ok, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const texts: string[] = [];
    (function walk(node: ts.Node): void {
      if (ts.isJsxText(node)) texts.push(node.text);
      ts.forEachChild(node, walk);
    })(sf);
    const found = texts.some(t => t.split('\n').some(line => JSX_COMMENT_RE.test(line)));
    expect(found, '{/* ... */} — выражение, а не текст; детектор не должен срабатывать').toBe(false);
  });
});
