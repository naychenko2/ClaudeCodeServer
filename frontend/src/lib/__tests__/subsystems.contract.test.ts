import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Контракт фронт/бэк для поля `subsystems` в `/api/auth/me`.
//
// Под Д-1 отчёта QA пилота «отключаемая подсистема Notes»: бэк отдавал
// `subsystems` массивом активных ключей (`SubsystemStateStore.ActiveKeys()`),
// а фронт ждал `Record<string, boolean>` и делал `{ ...arr }` — получались
// числовые ключи `'0','1'…`, и `useSubsystem('notes')` возвращал `false`
// при включённой подсистеме. Сторожа не было, дефект дошёл до стенда.
//
// Тест закрывает регресс формы по двум линиям:
//   1) AuthController.cs: на строке Me() поле `subsystems = ...` строится
//      из вызова, возвращающего массив (`subsystems.ActiveKeys()` или
//      эквивалент — `IReadOnlyList<string>`, `.ToList()`, `.ToArray()`,
//      литерал-массив). Спред массива на фронте → числовые ключи, потому
//      НЕ Record-сборка (`Subsystems().ToDictionary(...)`,
//      `.ActiveKeys().ToDictionary(...)` и пр. — красный).
//   2) Me.subsystems в `frontend/src/types/index.ts` — `string[]`,
//      не `Record<string, boolean>`. Подмена типа на Record на фронте
//      красная.
//
// Если кто-то сменит форму на одной стороне и не сменит на другой —
// минимум одна из линий красная. Постановка задачи требует «краснеть при
// рассинхроне формы», и этот тест ровно это и делает.

const here = dirname(fileURLToPath(import.meta.url));
const authControllerCs = resolve(here, '../../../../backend/ClaudeHomeServer/Controllers/AuthController.cs');
const meTypeTs = resolve(here, '../../types/index.ts');

function readFile(path: string): string {
  return readFileSync(path, 'utf-8');
}

// В теле метода Me() ищем место, где в ответе кладётся поле `subsystems = …`.
// Хрупко к строковому литералу, но постановка прямо указывает на этот
// конкретный файл и поле — обвязки менять не нужно.
function subsystemsAssignmentLine(src: string): string {
  // Берём строку, в которой встречается присваивание поля. Окружающий
  // Ok(new { ... }) обходим регэкспом: всё между «new {» и «}» метода Me()
  // может быть длинным, но литерал `subsystems = …` всегда один.
  const match = src.match(/subsystems\s*=\s*([^\n,]+)/);
  expect(match, 'AuthController.cs: ожидалось присваивание `subsystems = ...` в Me()').not.toBeNull();
  return (match?.[1] ?? '').trim();
}

// Поле `subsystems?:` в типе Me. Хрупко к строковому литералу — задача про
// контрактное поле, не про полный парсер TS.
function subsystemsTypeLine(src: string): string {
  const match = src.match(/subsystems\?:\s*([^\n;]+)/);
  expect(match, 'types/index.ts: ожидалось поле `subsystems?: ...` в Me').not.toBeNull();
  return (match?.[1] ?? '').trim();
}

// Выражение, которое бэк использует для построения `subsystems = …`,
// ОБЯЗАНО приводить к массиву строк. Прямой белый список — никаких
// ToDictionary / Dictionary / .ToLookup; литералы массивов и `.ActiveKeys()`
// приветствуются.
function isArrayForm(expr: string): boolean {
  const e = expr.replace(/\s+/g, ' ').trim();
  // Запрет: всё, что даёт словарь ключ→значение. Сюда попадают и .ToDictionary(...),
  // и .Active().ToDictionary(...), и литералы {"notes": true}.
  if (/\bToDictionary\s*\(/.test(e)) return false;
  if (/\bToLookup\s*\(/.test(e)) return false;
  if (/^\{["']/.test(e)) return false;       // литерал-объект
  if (/\bDictionary\s*</.test(e)) return false;
  // Разрешаем: явные массивные формы.
  if (/\bActiveKeys\s*\(/.test(e)) return true;
  if (/\.ActiveKeys\s*\(\s*\)/.test(e)) return true;
  if (/\.ToList\s*\(\s*\)/.test(e)) return true;
  if (/\.ToArray\s*\(\s*\)/.test(e)) return true;
  if (/^\[/.test(e)) return true;             // литерал-массив
  // На крайний случай — голое имя, по соглашению Стора (`ActiveKeys` явно
  // возвращает массив, см. SubsystemStateStore.ActiveKeys()). Падаем только
  // если встретили неизвестную форму.
  return /\bActiveKeys\b/.test(e);
}

describe('контракт subsystems фронт/бэк', () => {
  it('AuthController.Me кладёт subsystems из массивной формы', () => {
    const src = readFile(authControllerCs);
    const expr = subsystemsAssignmentLine(src);
    expect(isArrayForm(expr),
      `AuthController.Me клал subsystems = ${expr}; ` +
      'контракт — массив активных ключей (SubsystemStateStore.ActiveKeys()). ' +
      'Record-сборка (ToDictionary, литерал {…}) ломает гейт на фронте: спред массива ' +
      'даст числовые ключи, useSubsystem("notes") будет false при включённой подсистеме.'
    ).toBe(true);
  });

  it('Me.subsystems в types/index.ts — string[] (не Record)', () => {
    const src = readFile(meTypeTs);
    const decl = subsystemsTypeLine(src);
    expect(decl,
      'types/index.ts: ожидалось `subsystems?: string[]` (или эквивалент `Array<string>` / `readonly string[]`).'
    ).toMatch(/^(string\[\]|Array<string>|readonly\s+string\[\])$/);
  });

  it('SubsystemStateStore.ActiveKeys() возвращает массив строк', () => {
    // Корневая точка контракта на бэке. Если кто-то перепишет её
    // на `IReadOnlyDictionary<string,bool>` или подобное — два теста выше
    // покраснеют (выражение перестанет быть массивной формой), но мы
    // дополнительно фиксируем сам сигнатурный контракт: возвращаемый тип
    // должен быть IEnumerable-семейства, не Dictionary.
    const storeFile = resolve(here, '../../../../backend/ClaudeHomeServer.Core/Services/Composition/IAppSubsystem.cs');
    const src = readFile(storeFile);
    const match = src.match(/public\s+IReadOnlyList<string>\s+ActiveKeys\s*\(/);
    expect(match,
      'SubsystemStateStore.ActiveKeys() должен возвращать IReadOnlyList<string> — это и есть ' +
      'источник массива для /api/auth/me.subsystems. Смена на Dictionary ломает контракт.'
    ).not.toBeNull();
  });
});