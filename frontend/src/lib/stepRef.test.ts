// m-6: ведущий «{stepRef}:» от заголовка задачи отрезается (рядом уже моно-номер шага).
// Чистая функция — 4 грани (с префиксом / без / пустой·null ref / ref с пробелами)
// + случай «название = номеру» (без двоеточия префикс не распознаётся, возвращается как есть).
import { describe, expect, it } from 'vitest';
import { stripStepRefPrefix, stepDisplayName } from './stepRef';

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

  // Дизайн-ревью F2, п. 6: имя фичи перед кодом в заголовке («S4b-3 · LLM-wiki · S4b-3: …»)
  // давало двойной код — срезается и код после разделителя «·».
  it('код после разделителя «·»: «LLM-wiki · S4b-3: разбор…» → «LLM-wiki · разбор…»', () => {
    expect(stripStepRefPrefix('LLM-wiki · S4b-3: разбор транскрипта', 'S4b-3')).toBe('LLM-wiki · разбор транскрипта');
  });

  it('хвостовой разделитель после среза не остаётся', () => {
    expect(stripStepRefPrefix('LLM-wiki · S4b-3:', 'S4b-3')).toBe('LLM-wiki');
  });

  it('другой код в середине не трогается: чужой stepRef в заголовке остаётся', () => {
    expect(stripStepRefPrefix('Бэк · Ш9: соседний шаг', 'Ш8')).toBe('Бэк · Ш9: соседний шаг');
  });
});

// r-2: название шага для строки, где рядом уже моно-номер (общий хелпер NowCard/строки шага).
describe('stepDisplayName', () => {
  it('нормальное название → возвращается (ведущий «{stepRef}:» отрезается)', () => {
    expect(stepDisplayName('Ш1: пересборка', 'Ш1')).toBe('пересборка');
    expect(stepDisplayName('Бэк-шаг', 'D1')).toBe('Бэк-шаг');
  });

  it('название = номеру → null (не «Ш8 Ш8»)', () => {
    expect(stepDisplayName('Ш8', 'Ш8')).toBeNull();
  });

  it('пустое название → null (остаётся только номер)', () => {
    expect(stepDisplayName('', 'Ш8')).toBeNull();
  });
});
