// Условие показа кнопки «Прибраться» в снимке промпта (в рендере не покрыть:
// диалог грузит снимок эффектом).
import { describe, it, expect } from 'vitest';
import { showTidyButton } from '../PromptSnapshotDialog';

const big = 60 * 1024;   // выше порога 50 КБ
const small = 10 * 1024; // ниже порога

describe('showTidyButton', () => {
  it('толстая карта проекта — кнопка есть', () => {
    expect(showTidyButton({
      fileTitle: 'CLAUDE.md проекта',
      projectId: 'p1', sizeBytes: big,
    })).toBe(true);
  });

  it('чат вне проекта — кнопки нет: уборка адресуется проекту', () => {
    expect(showTidyButton({
      fileTitle: 'CLAUDE.md проекта',
      projectId: null, sizeBytes: big,
    })).toBe(false);
  });

  it('карта тоньше порога — кнопка не навязывается', () => {
    expect(showTidyButton({
      fileTitle: 'CLAUDE.md проекта',
      projectId: 'p1', sizeBytes: small,
    })).toBe(false);
  });

  // «CLAUDE.md проекта (.claude)» — другой файл, и уборка его не трогает (предмет
  // правки ровно один: корневой CLAUDE.md, Р4 плана)
  it('соседняя карта .claude — не тот файл', () => {
    expect(showTidyButton({
      fileTitle: 'CLAUDE.md проекта (.claude)',
      projectId: 'p1', sizeBytes: big,
    })).toBe(false);
  });
});
