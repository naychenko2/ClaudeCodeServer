// A1: POSIX-роуты без расширения (/api/…, /hubs/…) — не ссылка на файл, обычный текст.
// Windows-путь (диск) и POSIX-путь с расширением файла — ссылка, как раньше.
import { describe, it, expect, vi } from 'vitest';

// MarkdownContent тянет react-markdown и api-модуль — для чистой функции не нужны
vi.mock('react-markdown', () => ({ default: () => null, defaultUrlTransform: (u: string) => u }));
vi.mock('remark-gfm', () => ({ default: () => undefined }));
vi.mock('react-syntax-highlighter', () => ({ Prism: () => null }));
vi.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ oneDark: {} }));
vi.mock('../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../../lib/api', () => ({ api: {} }));

import { isFileLikeAbsPath } from '../MarkdownContent';

describe('isFileLikeAbsPath', () => {
  it('POSIX-роут без расширения → не файл', () => {
    expect(isFileLikeAbsPath('/api/mcp/calls')).toBe(false);
    expect(isFileLikeAbsPath('/api/auth/ping')).toBe(false);
    expect(isFileLikeAbsPath('/hubs/session')).toBe(false);
  });

  it('POSIX-путь с расширением файла → файл', () => {
    expect(isFileLikeAbsPath('/workspace/src/a.ts')).toBe(true);
  });

  it('Windows-путь с диском → файл всегда, даже без расширения', () => {
    expect(isFileLikeAbsPath('C:\\Temp\\x.txt')).toBe(true);
    expect(isFileLikeAbsPath('C:\\Temp\\noext')).toBe(true);
  });

  it('относительный путь и внешняя ссылка → не абсолютный путь', () => {
    expect(isFileLikeAbsPath('src/a.ts')).toBe(false);
    expect(isFileLikeAbsPath('https://example.com/a.ts')).toBe(false);
  });
});
