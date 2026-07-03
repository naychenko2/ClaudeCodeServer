import { describe, it, expect } from 'vitest';
import { parseWorkflowMeta, workflowName } from '../workflowMeta';

// --- Фикстуры скриптов workflow ---

const SCRIPT_FULL = `
export const meta = {
  name: 'release-flow',
  description: 'Подготовка релиза',
  phases: [
    { title: 'Сборка', detail: 'dotnet build + npm build' },
    { title: 'Тесты' },
    { title: 'Деплой', detail: 'публикация на прод' },
  ],
};

export default async function run({ agents }) {
  // поле description ниже не должно попасть в мету
  const cfg = { description: 'мусор из тела скрипта' };
  await agents.run(cfg);
}
`;

const SCRIPT_NO_PHASES = `
export const meta = { name: 'quick-fix', description: 'Быстрый фикс' };
export default async () => {};
`;

// Скобки внутри строк меты — сбалансированные, разбор по балансу их переживает
const SCRIPT_BRACES_IN_STRINGS = `
export const meta = {
  name: 'braces',
  description: 'Обработка {шаблонов} в тексте',
  phases: [
    { title: 'Парсинг [токенов]', detail: 'вложенные (скобки) и {фигурные}' },
  ],
};
`;

const SCRIPT_NAME_ONLY = `
export const meta = {
  name: 'no-desc-flow',
  phases: [{ title: 'Один' }],
};
`;

describe('parseWorkflowMeta', () => {
  it('полная мета: name, description и phases с detail', () => {
    const meta = parseWorkflowMeta({ script: SCRIPT_FULL });
    expect(meta).not.toBeNull();
    expect(meta!.name).toBe('release-flow');
    expect(meta!.description).toBe('Подготовка релиза');
    expect(meta!.phases).toEqual([
      { title: 'Сборка', detail: 'dotnet build + npm build' },
      { title: 'Тесты', detail: undefined },
      { title: 'Деплой', detail: 'публикация на прод' },
    ]);
  });

  it('description из тела скрипта не подхватывается (разбор по балансу скобок)', () => {
    expect(parseWorkflowMeta({ script: SCRIPT_FULL })!.description).not.toContain('мусор');
  });

  it('мета без phases → phases undefined', () => {
    const meta = parseWorkflowMeta({ script: SCRIPT_NO_PHASES });
    expect(meta).toEqual({ name: 'quick-fix', description: 'Быстрый фикс', phases: undefined });
  });

  it('сбалансированные скобки внутри строк не ломают разбор', () => {
    const meta = parseWorkflowMeta({ script: SCRIPT_BRACES_IN_STRINGS });
    expect(meta!.description).toBe('Обработка {шаблонов} в тексте');
    expect(meta!.phases).toHaveLength(1);
    expect(meta!.phases![0].title).toBe('Парсинг [токенов]');
  });

  it('мета только с name', () => {
    const meta = parseWorkflowMeta({ script: SCRIPT_NAME_ONLY });
    expect(meta!.name).toBe('no-desc-flow');
    expect(meta!.description).toBeUndefined();
    expect(meta!.phases).toEqual([{ title: 'Один', detail: undefined }]);
  });

  it('скрипт без export const meta → null', () => {
    expect(parseWorkflowMeta({ script: 'export default async () => {};' })).toBeNull();
  });

  it('нет script (сохранённый workflow) или мусорный input → null', () => {
    expect(parseWorkflowMeta({ name: 'saved-flow' })).toBeNull();
    expect(parseWorkflowMeta(null)).toBeNull();
    expect(parseWorkflowMeta(undefined)).toBeNull();
    expect(parseWorkflowMeta({ script: 42 })).toBeNull();
  });

  it('незакрытая мета (нет закрывающей скобки) → null', () => {
    expect(parseWorkflowMeta({ script: 'export const meta = { name: "x"' })).toBeNull();
  });
});

describe('workflowName', () => {
  it('приоритет: description → name из меты → input.name → «Workflow»', () => {
    expect(workflowName({ script: SCRIPT_FULL })).toBe('Подготовка релиза');
    expect(workflowName({ script: SCRIPT_NAME_ONLY })).toBe('no-desc-flow');
    expect(workflowName({ name: 'saved-flow' })).toBe('saved-flow');
    expect(workflowName({})).toBe('Workflow');
  });

  it('script без меты, но с input.name → input.name', () => {
    expect(workflowName({ script: 'export default async () => {};', name: 'fallback' })).toBe('fallback');
  });
});
