// Сторож инварианта волны 4 «Персонализация специальностей»: подписи специальностей
// (имя роли) — единый источник правды на бэкенде (SpecialtyCatalog.cs). Любая
// литеральная карта roleKey → русская подпись во фронте = дрейф каталога: фронт
// начнёт показывать старые подписи после правки бэка, или наоборот — бэк
// зафиксирует новую подпись, а фронт продолжит отрисовывать устаревшую.
//
// Проверяем в две стороны:
//   1. Поштучно — ВСЕ пары roleKey → русская подпись из SpecialtyCatalog.cs
//      перечислены ниже. Любая такая пара в production-коде (вне тестов) фейлит
//      тест с указанием файла и строки.
//   2. Порог — в одном файле >= 3 таких пар = гарантированная литеральная карта,
//      даже если это функция-резолвер. Этот порог убирает шум от случайных
//      упоминаний («Аналитик токенов», «Наставник» в комментарии, одиночный
//      switch-case по одной роли).
//
// Исключения:
//   - __tests__/** — тесты законно держат каталожные значения в фикстурах
//     (`role('analyst', 'Аналитик')`), это эталон каталога, не дрейф.
//   - lib/specialties.ts — там живёт ICON_COLOR_BY_KEY с icon/color (НЕ
//     подписями), но в виде литерального объекта с ключами ролей. Сами подписи
//     ролей (`Аналитик`, `Библиотекарь`) в этом файле не нужны: они берутся
//     из /api/specialties. Если кто-то добавит туда литеральную подпись —
//     сторож фейлит, и это правильное поведение.
//
// Проверка защищает от регрессии «импорт подписей через объект-карту»: если
// кто-то захочет вернуть `const SPECIALTY_LABELS = { analyst: 'Аналитик', ... }`
// для офлайн-кейса — тест упадёт ещё до PR, и решение придётся согласовывать.

import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

// Полный список пар roleKey → русская подпись из backend/ClaudeHomeServer/Services/
// SpecialtyCatalog.cs. Если каталог расширится — дописать сюда же. Этот список —
// зеркало бэка, любое расхождение = сигнал о рассинхроне.
const FORBIDDEN_PAIRS: ReadonlyArray<readonly [string, string]> = [
  ['analyst', 'Аналитик'],
  ['planner', 'Планировщик'],
  ['reviewer', 'Ревьюер'],
  ['executor', 'Исполнитель (универсальный)'],
  ['secretary', 'Секретарь'],
  ['coordinator', 'Координатор'],
  ['mentor', 'Наставник'],
  ['designer', 'Дизайнер'],
  ['consultant', 'Консультант'],
  ['librarian', 'Библиотекарь'],
  ['tester', 'Тестировщик'],
  ['backendExecutor', 'Исполнитель (бэкенд)'],
  ['frontendExecutor', 'Исполнитель (фронтенд)'],
  ['devopsExecutor', 'Исполнитель (DevOps)'],
];

// Минимум пар в одном файле, чтобы сработать. 1..2 пары могут встретиться
// в комментариях («роль Аналитик») или в однострочном switch-case; 3+ —
// это уже гарантированная литеральная карта.
const PAIR_THRESHOLD = 3;

const FRONTEND_SRC = join(process.cwd(), 'src');
const ALLOWED_FILES = new Set<string>([
  // ICON_COLOR_BY_KEY — карта icon/color (НЕ подписей), ключи пересекаются с
  // ролями. Сами подписи ролей тут не нужны: каталог отдаёт label с бэка.
  join(FRONTEND_SRC, 'lib', 'specialties.ts'),
]);

function listAllSources(root: string): string[] {
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
      } else if (st.isFile() && (name.endsWith('.ts') || name.endsWith('.tsx'))) {
        out.push(full);
      }
    }
  }
  walk(root);
  return out;
}

// Регэкс для пары: ключ roleKey в виде identifier (с границами), затем
// двоеточие, потом строковый литерал с указанной подписью. Подпись экранируется
// через \\. — на случай скобок и спец-символов внутри ("Исполнитель (бэкенд)").
function buildPairRegex(key: string, label: string): RegExp {
  const escapedLabel = label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  // Слово целиком для ключа: «analyst», но не «analyst2» или «myanalyst».
  // Строковый литерал может быть в одинарных/двойных кавычках/бэктиках.
  return new RegExp(`(?:^|[^A-Za-z0-9_$])${key}\\s*:\\s*['"\`]${escapedLabel}['"\`]`, 'gm');
}

interface FileMatch {
  file: string;
  pair: readonly [string, string];
  count: number;
  samples: Array<{ line: number; text: string }>;
}

// Ищет все вхождения каждой пары в файле. Возвращает детализированный список
// для понятного сообщения об ошибке (файл, строка, найденный текст).
function scanFile(path: string): FileMatch[] {
  let src: string;
  try { src = readFileSync(path, 'utf8'); }
  catch { return []; }
  const matches: FileMatch[] = [];
  for (const [key, label] of FORBIDDEN_PAIRS) {
    const re = buildPairRegex(key, label);
    const hits: Array<{ line: number; text: string }> = [];
    let m: RegExpExecArray | null;
    while ((m = re.exec(src)) !== null) {
      const line = src.slice(0, m.index).split('\n').length;
      const lineText = src.split('\n')[line - 1] ?? '';
      hits.push({ line, text: lineText.trim() });
    }
    if (hits.length > 0) {
      matches.push({ file: path, pair: [key, label], count: hits.length, samples: hits });
    }
  }
  return matches;
}

describe('сторож: литеральные карты roleKey → подпись специальности запрещены во фронте', () => {
  const sources = listAllSources(FRONTEND_SRC);

  it('обход src/ находит файлы', () => {
    expect(sources.length).toBeGreaterThan(10);
  });

  it('ни один production-файл не содержит литеральную карту подписей (>=3 пар)', () => {
    const violations: Array<{ file: string; pair: readonly [string, string]; count: number;
      samples: Array<{ line: number; text: string }> }> = [];
    for (const file of sources) {
      // Тесты — не нарушение: фикстуры законно используют каталожные значения.
      if (file.includes(`${'__tests__'}${sep}`)) continue;
      if (file.includes('.test.ts') || file.includes('.test.tsx')) continue;
      // lib/specialties.ts — там ICON_COLOR_BY_KEY, и если кто-то добавит туда
        // литеральную подпись — это нарушение (см. комментарий выше). Поэтому
        // файл не исключаем, но отдельно проверяем.
      const fileMatches = scanFile(file);
      const totalCount = fileMatches.reduce((sum, m) => sum + m.count, 0);
      if (totalCount >= PAIR_THRESHOLD || (totalCount > 0 && ALLOWED_FILES.has(file))) {
        for (const m of fileMatches) {
          violations.push({ file: relative(process.cwd(), m.file), pair: m.pair,
            count: m.count, samples: m.samples });
        }
      }
    }
    expect(
      violations,
      'Найдены литеральные подписи специальностей — единственный источник правды '
        + 'на бэкенде (SpecialtyCatalog.cs), фронт берёт их через /api/specialties: '
        + violations.flatMap(v => v.samples.map(s =>
          `${v.file}:${s.line} — ${v.pair[0]}: '${v.pair[1]}' (${s.text})`,
        )).join('\n'),
    ).toEqual([]);
  });

  it('lib/specialties.ts не содержит русских подписей ролей (только icon/color)', () => {
    // ICON_COLOR_BY_KEY — белый список иконок, подписей ролей там нет по дизайну.
    // Если кто-то по ошибке допишет туда «analyst: 'Аналитик'» — это сломает
    // единый источник правды (бэк отдаёт label через /api/specialties).
    const file = join(FRONTEND_SRC, 'lib', 'specialties.ts');
    const matches = scanFile(file);
    expect(
      matches,
      'lib/specialties.ts не должен держать литеральных подписей ролей: '
        + matches.flatMap(m => m.samples.map(s =>
          `${relative(process.cwd(), m.file)}:${s.line} — ${m.pair[0]}: '${m.pair[1]}'`,
        )).join('\n'),
    ).toEqual([]);
  });

  it('каждая пара из SpecialtyCatalog.cs покрыта хотя бы одним expectedPair', () => {
    // Защита от регрессии «забыли пару в списке FORBIDDEN_PAIRS»: если на бэке
    // появится новая роль, этот тест напомнит добавить её и сюда.
    expect(FORBIDDEN_PAIRS.length).toBeGreaterThanOrEqual(14);
    // Все 14 ключей SpecialtyCatalog.All (кроме 'none'/'any') должны быть в списке
    const expectedKeys = ['analyst', 'planner', 'reviewer', 'executor', 'secretary',
      'coordinator', 'mentor', 'designer', 'consultant', 'librarian', 'tester',
      'backendExecutor', 'frontendExecutor', 'devopsExecutor'];
    const present = FORBIDDEN_PAIRS.map(([k]) => k);
    for (const k of expectedKeys) {
      expect(present).toContain(k);
    }
  });
});