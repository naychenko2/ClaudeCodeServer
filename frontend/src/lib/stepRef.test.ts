// m-6: ведущий «{stepRef}:» от заголовка задачи отрезается (рядом уже моно-номер шага).
// Чистая функция — 4 грани (с префиксом / без / пустой·null ref / ref с пробелами)
// + случай «название = номеру» (без двоеточия префикс не распознаётся, возвращается как есть).
import { describe, expect, it } from 'vitest';
import { stripStepRefPrefix } from './stepRef';

describe('stripStepRefPrefix', () => {
  it('с префиксом: «Ш1: парсер» → «парсер»', () => {
    expect(stripStepRefPrefix('Ш1: парсер', 'Ш1')).toBe('парсер');
  });

  it('без префикса: заголовок не трогает', () => {
    expect(stripStepRefPrefix('Парсер', 'Ш1')).toBe('Парсер');
  });

  it('пустой/null stepRef: заголовок не трогает', () => {
    expect(stripStepRefPrefix('Ш1: парсер', null)).toBe('Ш1: парсер');
    expect(stripStepRefPrefix('Ш1: парсер', '')).toBe('Ш1: парсер');
  });

  it('stepRef с пробелами: тримается, префикс всё равно отрезается', () => {
    expect(stripStepRefPrefix('Ш1: парсер', '  Ш1  ')).toBe('парсер');
  });

  it('название = номеру: без двоеточия префикс не распознаётся, возвращается как есть', () => {
    expect(stripStepRefPrefix('Ш8', 'Ш8')).toBe('Ш8');
  });
});
