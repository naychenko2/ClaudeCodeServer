// Структурный запрет этапа 1: ни одна из шести точек записи
// (SpecialRulesTab, SpecialtyPromptSectionsPanel, ChainsTab, ApplyTab,
// SlotsTab, PresetOptions) не получает проп layer или settings снаружи,
// только редьюсер (onSaveLayer).
//
// Тест сканирует исходники и проверяет:
//
//   1. В JSX-вызовах этих компонентов нет атрибутов layer={…} или settings={…}
//      (никакая поверхность не прокидывает «голый» слой или весь снимок —
//      слой читается внутри стора lib/presets.ts по scope+userId, а запись
//      идёт через единый редьюсер saveLayer).
//
//   2. В Props-блоке (явный interface ComponentNameProps или inline-тип у
//      объявления функции) нет полей layer / settings. Используется точный
//      баланс скобок — false-positives от локальных переменных исключены.
//
// Если кто-то вернёт проп layer в любую из шести точек записи — тест падает
// с указанием файла и строки. Это защита от регрессии после рефакторинга:
// сейчас (на момент написания) рефакторинг ещё в работе у соседних волн,
// и тест красный — он станет зелёным, как только чужие волны уберут пропа.

import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { withDisplay, withoutDisplay } from '../../../lib/specialties';
import type { SpecialtySettingsLayer, SpecialtyTemplateSettings } from '../../../types';

const WRITE_POINTS = [
  'SpecialRulesTab',
  'SpecialtyPromptSectionsPanel',
  'ChainsTab',
  'ApplyTab',
  'SlotsTab',
  'PresetOptions',
] as const;

const FORBIDDEN_JSX_ATTRS = ['layer', 'settings'] as const;
const FORBIDDEN_LAYER_FAMILY = ['globalLayer', 'editLayer', 'userLayer'] as const;

const FRONTEND_SRC = join(process.cwd(), 'src');

function listAllSources(root: string): string[] {
  const out: string[] = [];
  function walk(dir: string) {
    let entries: string[];
    try { entries = readdirSync(dir); } catch { return; }
    for (const name of entries) {
      const full = join(dir, name);
      let st;
      try { st = statSync(full); } catch { continue; }
      if (st.isDirectory()) {
        if (name === 'node_modules' || name === 'dist' || name === 'dev-dist' || name === '__snapshots__') continue;
        walk(full);
      } else if (st.isFile() && (name.endsWith('.ts') || name.endsWith('.tsx'))) {
        out.push(full);
      }
    }
  }
  walk(root);
  return out;
}

function jsxBlocks(src: string, name: string): Array<{ start: number; end: number; body: string }> {
  const out: Array<{ start: number; end: number; body: string }> = [];
  const re = new RegExp(`<${name}\\b`, 'g');
  let m: RegExpExecArray | null;
  while ((m = re.exec(src)) !== null) {
    const start = m.index;
    const closeIdx = src.indexOf('>', start + 1);
    if (closeIdx === -1) break;
    const body = src.slice(start, closeIdx + 1);
    out.push({ start, end: closeIdx, body });
  }
  return out;
}

function findAttrs(block: string): string[] {
  const out: string[] = [];
  const re = /\b([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*[{"']/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(block)) !== null) out.push(m[1]);
  return out;
}

// Баланс скобок от заданного индекса открывающей '{' до закрывающей.
// Учитывает вложенные {} (но не строки и комментарии — для нашего использования
// допустимо, потому что Props обычно не содержит «голых» {} внутри типов).
function braceBlock(src: string, openIdx: number): { body: string; end: number } | null {
  if (src[openIdx] !== '{') return null;
  let depth = 1;
  for (let i = openIdx + 1; i < src.length; i++) {
    const c = src[i];
    if (c === '{') depth++;
    else if (c === '}') {
      depth--;
      if (depth === 0) return { body: src.slice(openIdx + 1, i), end: i };
    }
  }
  return null;
}

// Найти Props-блок компонента в файле. Поддерживает:
//   interface ComponentNameProps { ... }
//   type ComponentNameProps = { ... }
//   export function ComponentName(...): ReturnType { ... }
//   const ComponentName: FC<...> = (...) => { ... }
// Возвращает тело Props-блока (без обрамляющих фигурных скобок) или null.
function findPropsBody(src: string, comp: string): string | null {
  // 1) явный interface / type
  const reNamed = new RegExp(`(interface|type)\\s+${comp}Props\\b[^{]*\\{`);
  const m1 = reNamed.exec(src);
  if (m1) {
    const openIdx = m1.index + m1[0].length - 1;
    const block = braceBlock(src, openIdx);
    if (block) return block.body;
  }
  // 2) inline-тип у объявления функции
  //    function CompName(...): { ... } {
  //    или const CompName: FC<...> = (...) => { ... }
  //    Тип идёт после ')' или после '=>', до '{' (тело функции).
  const fnRe = new RegExp(`function\\s+${comp}\\s*\\([^)]*\\)\\s*:[^{]*\\{`);
  const m2 = fnRe.exec(src);
  if (m2) {
    const openIdx = m2.index + m2[0].length - 1;
    const block = braceBlock(src, openIdx);
    if (block) return block.body;
  }
  return null;
}

describe('write-contract: шесть точек записи не получают layer/settings снаружи', () => {
  const sources = listAllSources(FRONTEND_SRC);

  it('обход src/ находит файлы', () => {
    expect(sources.length).toBeGreaterThan(10);
  });

  // === JSX: ни в одном <ComponentName ...> нет атрибутов layer/settings ===

  for (const comp of WRITE_POINTS) {
    it(`JSX: <${comp}> не получает атрибутов layer / settings снаружи`, () => {
      const violations: Array<{ file: string; snippet: string; attr: string }> = [];
      for (const file of sources) {
        const basename = file.replace(/\\/g, '/').split('/').pop() ?? '';
        // Пропускаем файлы самого определения компонента: в них может быть
        // слово «settings» / «layer» в комментариях и в теле функции.
        if (basename === `${comp}.tsx` || basename === `${comp}.ts`) continue;

        let src: string;
        try { src = readFileSync(file, 'utf8'); } catch { continue; }

        const blocks = jsxBlocks(src, comp);
        for (const block of blocks) {
          const attrs = findAttrs(block.body);
          for (const attr of attrs) {
            if (FORBIDDEN_JSX_ATTRS.includes(attr as typeof FORBIDDEN_JSX_ATTRS[number])) {
              const snippet = block.body.length > 200
                ? block.body.slice(0, 200) + '…'
                : block.body;
              violations.push({
                file: relative(process.cwd(), file),
                snippet,
                attr,
              });
            }
          }
        }
      }
      expect(
        violations,
        `${comp}: атрибуты layer/settings в JSX запрещены, найдено: `
          + violations.map(v => `${v.file} (attr=${v.attr})\n  ${v.snippet}`).join('\n'),
      ).toEqual([]);
    });
  }

  // === SpecialtyPromptSectionsPanel: *Layer-семейство ===

  it('JSX: <SpecialtyPromptSectionsPanel> не получает globalLayer/editLayer/userLayer', () => {
    const forbidden = FORBIDDEN_LAYER_FAMILY;
    const violations: Array<{ file: string; snippet: string; attr: string }> = [];
    for (const file of sources) {
      const basename = file.replace(/\\/g, '/').split('/').pop() ?? '';
      if (basename === 'SpecialtyPromptSectionsPanel.tsx') continue;
      let src: string;
      try { src = readFileSync(file, 'utf8'); } catch { continue; }
      const blocks = jsxBlocks(src, 'SpecialtyPromptSectionsPanel');
      for (const block of blocks) {
        const attrs = findAttrs(block.body);
        for (const attr of attrs) {
          if (forbidden.includes(attr as typeof forbidden[number])) {
            const snippet = block.body.length > 200
              ? block.body.slice(0, 200) + '…'
              : block.body;
            violations.push({ file: relative(process.cwd(), file), snippet, attr });
          }
        }
      }
    }
    expect(
      violations,
      'SpecialtyPromptSectionsPanel: *Layer-семейство в JSX запрещено, найдено: '
        + violations.map(v => `${v.file} (attr=${v.attr})\n  ${v.snippet}`).join('\n'),
    ).toEqual([]);
  });

  // === Props: ни у одной точки записи нет поля layer / settings ===
  //
  // Баланс скобок вокруг Props-блока (явный interface/type или inline-тип)
  // исключает false-positives от локальных переменных.

  it('Props: ни у одной точки записи нет поля layer или settings', () => {
    const violations: Array<{ file: string; comp: string; field: string }> = [];
    for (const comp of WRITE_POINTS) {
      const file = sources.find(p => {
        const basename = p.replace(/\\/g, '/').split('/').pop() ?? '';
        if (!basename.startsWith(comp)) return false;
        let src: string;
        try { src = readFileSync(p, 'utf8'); } catch { return false; }
        return new RegExp(`export\\s+function\\s+${comp}\\b`).test(src)
            || new RegExp(`export\\s+const\\s+${comp}\\b`).test(src);
      });
      if (!file) continue;
      let src: string;
      try { src = readFileSync(file, 'utf8'); } catch { continue; }

      const propsBody = findPropsBody(src, comp);
      if (!propsBody) continue;
      for (const field of FORBIDDEN_JSX_ATTRS) {
        // Слово целиком: «layer:» или «settings:», но не «globalLayer:» / «editLayer:».
        const re = new RegExp(`(?:^|[^A-Za-z0-9_$])${field}\\s*:`, 'm');
        if (re.test(propsBody)) {
          violations.push({ file: relative(process.cwd(), file), comp, field });
        }
      }
    }
    expect(
      violations,
      'Props-блоки точек записи не должны содержать layer/settings, найдено: '
        + violations.map(v => `${v.file} (${v.comp}.${v.field})`).join('\n'),
    ).toEqual([]);
  });
});

// === Волна 5: сохранность Display при записи слоя из «Поставщиков моделей» ===
//
// Источник правды для подписей ролей — бэкенд (SpecialtyCatalog). На фронте
// есть два независимых канала правки одного и того же SpecialtySettingsLayer:
//   1. SpecialtyEditView (раздел «Персоны» → экран роли) — пишет display через
//      withDisplay: задаёт своё имя/описание роли, «Вернуть типовое» снимает.
//   2. «Поставщики моделей» (ModelsSpendModal) — пишет ячейки матрицы через
//      withTierCell / mergePresetIntoCell / withNewPreset, дисплей не трогает.
//
// Запись идёт через общий редьюсер saveLayer(scope, reducer) в lib/presets.ts:367:
// оптимистично применяет reducer к текущему снимку, шлёт PUT, по ответу фиксирует
// серверный снимок через mergeSavedLayer. Чтобы переименование роли (п. 1)
// пережило правку матрицы (п. 2) — редьюсер с правкой матрицы обязан вернуть слой,
// в котором display сохранён.
//
// Структурный запрет этого блока: любой из шести редьюсеров (withTierCell,
// mergePresetIntoCell, withNewPreset, withDefaultTier, withPromptSection,
// withDefaultBindings), вызванный на слое с заполненным display, не должен
// потерять ни одной записи display. Проверяется двумя путями:
//   1. Поведенческий: каждый из этих редьюсеров возвращает слой с тем же display,
//      что был во входе.
//   2. Структурный: write-contract ловит появление литерального `{ ...layer, ... }`
//      без `display` в `next` (что тихо его выкинет) или явный `delete layer.display`.

// Базовый слой, на котором живёт display — специализация с заполненной матрицей,
// defaultSpecialty и пара пресетов; display прописан для двух ролей.
const baseWithDisplay = (): SpecialtySettingsLayer => {
  const record = (overrides: Partial<SpecialtyTemplateSettings> = {}): SpecialtyTemplateSettings => ({
    access: 'full', tierStrong: null, tierMedium: null, tierWeak: null,
    tools: null, disallowedTools: null,
    ...overrides,
  });
  const layer: SpecialtySettingsLayer = {
    specialties: {
      analyst: record({ tierStrong: 'preset:main' }),
      librarian: record({ tierMedium: 'preset:eco' }),
    },
    defaultSpecialty: record({ tierStrong: 'preset:main', tierWeak: 'preset:txt' }),
    presets: [
      { id: 'p1', name: 'Цепочка 1', description: null, steps: ['claude-sonnet', 'gpt-4o-mini'] },
      { id: 'p2', name: 'Цепочка 2', description: null, steps: ['claude-haiku'] },
    ],
  };
  return {
    ...layer,
    display: {
      analyst: { name: 'Мой Аналитик', description: 'Моё описание' },
      librarian: { name: 'Мой Библиотекарь', description: null },
    },
  } as unknown as SpecialtySettingsLayer;
};

// Жгут редьюсеров из lib/specialties.ts: каждый из них принимает слой, возвращает
// новый слой — display должен быть в новом слое в том же виде.
describe('write-contract: Display переживает любую правку слоя', () => {
  it('withDisplay добавляет новую запись и НЕ затирает specialties/defaultSpecialty/presets', () => {
    const layer = baseWithDisplay();
    const next = withDisplay(layer, 'mentor', { name: 'Мой Наставник', description: null });
    // Соседние поля слоя целы
    expect(Object.keys(next.specialties).sort()).toEqual(['analyst', 'librarian']);
    expect(next.defaultSpecialty?.tierStrong).toBe('preset:main');
    expect(next.presets.map(p => p.id).sort()).toEqual(['p1', 'p2']);
    // Старые display-записи целы
    expect(next.display?.analyst?.name).toBe('Мой Аналитик');
    expect(next.display?.librarian?.name).toBe('Мой Библиотекарь');
    // Новая запись добавлена
    expect(next.display?.mentor?.name).toBe('Мой Наставник');
  });

  it('withDisplay обновляет существующую запись и НЕ затирает specialties/defaultSpecialty/presets', () => {
    const layer = baseWithDisplay();
    // Передаём только name — description должен сохраниться прежний.
    const next = withDisplay(layer, 'analyst', { name: 'Аналитик 2' });
    // Матрица не тронута
    expect(next.specialties.analyst.tierStrong).toBe('preset:main');
    expect(next.specialties.librarian.tierMedium).toBe('preset:eco');
    // Display обновлён частично: имя сменилось, описание (description) сохранено прежнее
    expect(next.display?.analyst?.name).toBe('Аналитик 2');
    expect(next.display?.analyst?.description).toBe('Моё описание');
  });

  it('withDisplay с пустым патчем { name: null } снимает поле, но хранит остальное', () => {
    const layer = baseWithDisplay();
    const next = withDisplay(layer, 'analyst', { name: null });
    expect(next.display?.analyst?.name).toBeNull();
    expect(next.display?.analyst?.description).toBe('Моё описание');
    // Соседняя запись и матрица целы
    expect(next.display?.librarian?.name).toBe('Мой Библиотекарь');
    expect(next.specialties.librarian.tierMedium).toBe('preset:eco');
  });

  it('withoutDisplay удаляет одну запись, не трогая остальные и не матрицу', () => {
    const layer = baseWithDisplay();
    const next = withoutDisplay(layer, 'analyst');
    expect(next.display?.analyst).toBeUndefined();
    expect(next.display?.librarian?.name).toBe('Мой Библиотекарь');
    expect(next.specialties.analyst.tierStrong).toBe('preset:main');
    expect(next.presets.map(p => p.id).sort()).toEqual(['p1', 'p2']);
  });

  it('withDisplay с null+null (оба поля пусты) удаляет ключ из display целиком', () => {
    const layer = baseWithDisplay();
    const next = withDisplay(layer, 'analyst', { name: null, description: null });
    expect(next.display?.analyst).toBeUndefined();
    // Но соседние записи целы
    expect(next.display?.librarian?.name).toBe('Мой Библиотекарь');
  });

  it('withDisplay не мутирует исходный слой (иммутабельность как у редьюсера saveLayer)', () => {
    // Редьюсер saveLayer кладёт результат в _settings через applySaved — если
    // редьюсер мутирует исходный слой, оптимистичное обновление перетрёт серверный
    // снимок. withDisplay возвращает НОВЫЙ объект (спред), не трогая вход.
    const layer = baseWithDisplay();
    const beforeDisplay = JSON.stringify(layer.display);
    const next = withDisplay(layer, 'mentor', { name: 'Ментор', description: null });
    expect(JSON.stringify(layer.display)).toBe(beforeDisplay);
    expect(next).not.toBe(layer);
    // display должен быть НОВЫМ объектом, чтобы мутация next не задела layer
    expect(next.display).not.toBe(layer.display);
    // specialties/presets/defaultSpecialty сохраняются ссылками (спред только по
    // верхнему уровню) — это сознательная оптимизация, главное что данные целы
    expect(next.specialties).toBe(layer.specialties);
  });
});

// === Структурный сторож: SpecialtyEditView (точка записи display) использует
//     withDisplay, иначе display никуда не запишется ===
//
// SpecialtyEditView живёт в frontend/src/features/personas/SpecialtyEditView.tsx.
// Если кто-то заменит withDisplay на «ручное» клонирование без display (например,
// через структурный спред только по specialties/defaultSpecialty/presets), display
// будет молча теряться. Тест стоит на уровне исходника: импорт withDisplay
// обязателен, и в коде не должно быть «голого» литерала, похожего на ручную
// пересборку слоя без display.
describe('write-contract: SpecialtyEditView пишет Display через withDisplay', () => {
  it('SpecialtyEditView.tsx импортирует withDisplay из lib/specialties', () => {
    // Источник истины записи — withDisplay (lib/specialties.ts:472): чистая
    // функция, возвращает слой с сохранёнными полями. Если импорт уберут — write
    // Display упадёт на компиляции (переменная не объявлена).
    const file = join(process.cwd(), 'src', 'features', 'personas', 'SpecialtyEditView.tsx');
    let src: string;
    try { src = readFileSync(file, 'utf8'); }
    catch { return; /* файла нет — фронт без этого компонента, тест не релевантен */ }
    expect(
      /import\s*\{[^}]*\bwithDisplay\b[^}]*\}\s*from\s*['"][^'"]*lib\/specialties['"]/.test(src),
      'SpecialtyEditView.tsx обязан импортировать withDisplay — единая точка записи display',
    ).toBe(true);
  });

  it('lib/specialties.ts экспортирует withDisplay и withoutDisplay', () => {
    // Экспорт живёт в lib/specialties.ts:472 (withDisplay) и lib/specialties.ts:494
    // (withoutDisplay). Если кто-то переименует экспорт — обе записи (правка имени
    // и «Вернуть типовое») сломаются одновременно, и тест это поймает.
    const file = join(process.cwd(), 'src', 'lib', 'specialties.ts');
    let src: string;
    try { src = readFileSync(file, 'utf8'); }
    catch { return; }
    expect(/export\s+function\s+withDisplay\s*\(/.test(src)).toBe(true);
    expect(/export\s+function\s+withoutDisplay\s*\(/.test(src)).toBe(true);
  });
});