import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Контракт формы ответа фронт/бэк для ОБОИХ мест, где фронт читает состав
// подсистем:
//   1) `/api/auth/me` → поле `subsystems` (гейт `useSubsystem`);
//   2) `/api/admin/subsystems` → список для админского экрана «Подсистемы».
//
// Почему сторож на форму вообще существует. Дефект Д-1 отчёта QA пилота
// «отключаемая подсистема Notes»: бэк отдавал `subsystems` массивом активных
// ключей (`SubsystemStateStore.ActiveKeys()`), а фронт ждал
// `Record<string, boolean>` и делал `{ ...arr }` — получались числовые ключи
// `'0','1'…`, и `useSubsystem('notes')` возвращал `false` при включённой
// подсистеме. Сторожа не было, дефект дошёл до стенда.
//
// Почему покрыты ДВА эндпоинта, а не один. Сторож, заведённый под Д-1, покрыл
// только `/api/auth/me` — и ровно тот же класс дефекта тут же повторился
// в соседнем непокрытом эндпоинте: `SubsystemsController` отдавал голый массив,
// а `api/subsystems.ts` деструктурировал `({ subsystems })` → `undefined` →
// экран падал на `.filter` в ErrorBoundary. Отсюда правило: покрыт каждый
// эндпоинт, из которого фронт читает состав подсистем, обе его стороны.
//
// Тесты читают ИСХОДНИКИ (C# и TS) как текст: рантайм-проверка потребовала бы
// поднятого бэка, а нам нужен дешёвый гейт, краснеющий в обычном `npm run test`.
// Отсюда хрупкость к строковым литералам — она осознанная: постановка каждый
// раз указывает конкретный файл и конкретное поле.

const here = dirname(fileURLToPath(import.meta.url));
const backendRoot = resolve(here, '../../../../backend');
const authControllerCs = resolve(backendRoot, 'ClaudeHomeServer/Controllers/AuthController.cs');
const subsystemsControllerCs = resolve(backendRoot, 'ClaudeHomeServer/Controllers/SubsystemsController.cs');
const appSubsystemCs = resolve(backendRoot, 'ClaudeHomeServer.Core/Services/Composition/IAppSubsystem.cs');
const meTypeTs = resolve(here, '../../types/index.ts');
const subsystemsApiTs = resolve(here, '../../api/subsystems.ts');
const subsystemsPageTsx = resolve(here, '../../pages/SubsystemsPage.tsx');

function readFile(path: string): string {
  return readFileSync(path, 'utf-8');
}

// ───────────────────────── /api/auth/me ─────────────────────────

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

describe('контракт subsystems фронт/бэк: /api/auth/me', () => {
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
    const src = readFile(appSubsystemCs);
    const match = src.match(/public\s+IReadOnlyList<string>\s+ActiveKeys\s*\(/);
    expect(match,
      'SubsystemStateStore.ActiveKeys() должен возвращать IReadOnlyList<string> — это и есть ' +
      'источник массива для /api/auth/me.subsystems. Смена на Dictionary ломает контракт.'
    ).not.toBeNull();
  });
});

// ─────────────────────── /api/admin/subsystems ───────────────────────

// Аргумент `Ok(...)` в методе List(). Берём первый `return Ok(` файла:
// контроллер односоставной, другого действия в нём нет.
function okArgument(src: string): string {
  const match = src.match(/return\s+Ok\(([\s\S]*?)\);/);
  expect(match, 'SubsystemsController.cs: ожидался `return Ok(...);` в List()').not.toBeNull();
  return (match?.[1] ?? '').replace(/\s+/g, ' ').trim();
}

// Объявленный тип результата действия List().
function listReturnType(src: string): string {
  const match = src.match(/public\s+(?:async\s+)?([\w.<>,\s?[\]]+?)\s+List\s*\(/);
  expect(match, 'SubsystemsController.cs: ожидалось объявление метода List()').not.toBeNull();
  return (match?.[1] ?? '').replace(/\s+/g, ' ').trim();
}

// Отдаём ли мы ГОЛЫЙ массив, а не объект-обёртку. Обёртка `Ok(new { subsystems
// = … })` — ровно тот дефект, который уронил экран: фронт получал массив,
// а деструктурировал поле (или наоборот).
function isBareArrayResponse(expr: string): boolean {
  // Запрет: любая обёртка-объект — анонимная (`new { ... }`), именованная
  // (`new SubsystemsListResponse(...)`) и литерал.
  if (/^new\b/.test(expr)) return false;
  if (/^\{/.test(expr)) return false;
  // Запрет: присваивание поля внутри аргумента — признак объекта-обёртки
  // даже без ключевого слова new.
  if (/\bsubsystems\s*=/i.test(expr)) return false;
  // Разрешаем: явные коллекционные формы.
  if (/\bSnapshot\s*\(/.test(expr)) return true;
  if (/\.ToList\s*\(\s*\)/.test(expr)) return true;
  if (/\.ToArray\s*\(\s*\)/.test(expr)) return true;
  if (/^\[/.test(expr)) return true;
  return false;
}

// Тип-аргумент вызова `request<…>('/admin/subsystems')` в клиенте.
function clientRequestTypeArg(src: string): string {
  const match = src.match(/request<([^>]+(?:<[^>]*>)?[^>]*)>\s*\(\s*['"]\/admin\/subsystems['"]/);
  expect(match,
    "api/subsystems.ts: ожидался вызов `request<...>('/admin/subsystems')`"
  ).not.toBeNull();
  return (match?.[1] ?? '').replace(/\s+/g, ' ').trim();
}

// Параметр обработчика `.then(...)` у `subsystemsApi.get()` на экране.
// Деструктуризация `({ subsystems })` = потребление обёртки; при голом
// массиве она даёт `undefined` и экран падает на `.filter`.
//
// Внешние скобки снимаем: деструктурирующая стрелка ВСЕГДА обёрнута в них
// (`({ subsystems }) => …`), и без снятия проверка «начинается с `{`»
// молча пропускала бы ровно ту форму, ради которой заведена.
function pageThenParam(src: string): string {
  const match = src.match(/subsystemsApi\s*\.\s*get\s*\(\s*\)\s*\.then\(\s*([^=]+?)\s*=>/);
  expect(match,
    'SubsystemsPage.tsx: ожидался `subsystemsApi.get().then(<параметр> => ...)`'
  ).not.toBeNull();
  const raw = (match?.[1] ?? '').trim();
  return raw.startsWith('(') && raw.endsWith(')')
    ? raw.slice(1, -1).trim()
    : raw;
}

describe('контракт subsystems фронт/бэк: /api/admin/subsystems', () => {
  it('SubsystemsController.List отдаёт голый массив, а не объект-обёртку', () => {
    const src = readFile(subsystemsControllerCs);
    const expr = okArgument(src);
    expect(isBareArrayResponse(expr),
      `SubsystemsController.List отдавал Ok(${expr}); ` +
      'контракт — ГОЛЫЙ массив SubsystemInfo (subsystems.Snapshot(config)). ' +
      'Обёртка вида `new { subsystems = ... }` ломает клиента: `request<Subsystem[]>` ' +
      'вернёт объект, `.filter` на нём упадёт и унесёт экран в ErrorBoundary.'
    ).toBe(true);
  });

  it('SubsystemsController.List объявлен коллекцией, не обёрткой', () => {
    // Сигнатура — вторая линия обороны: если аргумент Ok() станет
    // переменной, форму всё равно видно по объявленному типу результата.
    const decl = listReturnType(readFile(subsystemsControllerCs));
    expect(decl,
      `SubsystemsController.List объявлен как ${decl}; ожидалась коллекция ` +
      '(`ActionResult<IReadOnlyList<SubsystemInfo>>` или эквивалент IEnumerable-семейства). ' +
      'Обёрточный тип (`ActionResult<object>`, свой response-record) означает смену формы ответа.'
    ).toMatch(/^ActionResult<\s*(IReadOnlyList|IReadOnlyCollection|IEnumerable|List|SubsystemInfo\[\])/);
  });

  it('api/subsystems.ts запрашивает массив, а не обёртку', () => {
    const arg = clientRequestTypeArg(readFile(subsystemsApiTs));
    expect(arg,
      `api/subsystems.ts: request<${arg}>('/admin/subsystems') — ожидался массивный тип ` +
      '(`Subsystem[]` / `Array<Subsystem>` / `readonly Subsystem[]`). ' +
      'Обёрточный интерфейс (`SubsystemsListResponse` и т. п.) расходится с бэком: ' +
      'контроллер отдаёт голый массив.'
    ).toMatch(/^(readonly\s+)?\w+\[\]$|^Array<\w+>$/);
  });

  it('SubsystemsPage потребляет ответ как список, без деструктуризации обёртки', () => {
    const param = pageThenParam(readFile(subsystemsPageTsx));
    expect(param.startsWith('{'),
      `SubsystemsPage.tsx: .then(${param} => ...) деструктурирует объект-обёртку. ` +
      'Бэк отдаёт голый массив — деструктуризация даст undefined, и `.filter` уронит ' +
      'экран в ErrorBoundary (именно так и был потерян экран «Подсистемы»).'
    ).toBe(false);
  });
});
