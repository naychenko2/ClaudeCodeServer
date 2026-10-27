import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Сторож контракта ключа пассивного кэша (F2c-d). Ключ записи — offline:<owner>:<url>;
// голый URL-ключ осиротел (его сносит проход обслуживания) и читать его больше нельзя.
// Потребители, которые пишут и читают кэш напрямую (sync.ts — листинги дерева и
// содержимое файлов, SessionList — список чатов), обязаны брать ключ из offline.ts.
// Проверка по исходнику: модуль тянет за собой api/signalr, функциональный тест потребовал бы
// мока всей цепочки — а контракт ключа тут и есть по исходнику.

const here = dirname(fileURLToPath(import.meta.url));
const src = (rel: string) => readFileSync(resolve(here, '..', '..', rel), 'utf-8');

describe('контракт ключа пассивного кэша', () => {
  it('sync.ts адресует записи кэша только через offline.ts', () => {
    const code = src('lib/sync.ts');
    expect(code).toMatch(/import \{[^}]*passiveCacheKey[^}]*\} from '\.\/offline'/);
    expect(code).toMatch(/import \{[^}]*fileContentCachePrefix[^}]*\} from '\.\/offline'/);
    // листинги дерева пишутся по ключу с владельцем, а не по голому URL
    expect(code).not.toMatch(/idbSet\(\s*url\b/);
    // содержимое файлов адресуется префиксом с владельцем, а не голым URL
    expect(code).not.toMatch(/idbGet<[^>]*>\(\s*`\/projects/);
    expect(code).not.toMatch(/idbDelete\(\s*`\/projects/);
  });

  it('SessionList читает список чатов по ключу с владельцем', () => {
    const code = src('components/SessionList.tsx');
    expect(code).toContain('passiveCacheKey');
    expect(code).not.toMatch(/idbGet<[^>]*>\(\s*`\/projects/);
  });
});
