import { describe, expect, it } from 'vitest';
import { dativeName, genitiveName } from '../russianName';

describe('dativeName: дательный падеж имени для «Написать …»', () => {
  it('склоняет имена персон', () => {
    const cases: Record<string, string> = {
      Вера: 'Вере', Светлана: 'Светлане', Лиза: 'Лизе', Кира: 'Кире', Ника: 'Нике',
      Полина: 'Полине', Анна: 'Анне', Ольга: 'Ольге', Саша: 'Саше', Илья: 'Илье',
      София: 'Софии', Мария: 'Марии',
      Дмитрий: 'Дмитрию', Сергей: 'Сергею',
      Олег: 'Олегу', Максим: 'Максиму', Артем: 'Артему', Виктор: 'Виктору', Ассистент: 'Ассистенту',
    };
    for (const [name, expected] of Object.entries(cases)) expect(dativeName(name)).toBe(expected);
  });

  it('латиницу не склоняет', () => {
    expect(dativeName('Claude')).toBe('Claude');
    expect(dativeName('GLM 5.2')).toBe('GLM 5.2');
    expect(dativeName('AI')).toBe('AI');
  });

  it('неоднозначное и составное — null', () => {
    expect(dativeName('Игорь')).toBeNull();
    expect(dativeName('Любовь')).toBeNull();
    expect(dativeName('Вера Павловна')).toBeNull();
    expect(dativeName('ГР')).toBeNull();
    expect(dativeName('')).toBeNull();
  });
});

describe('genitiveName: родительный падеж имени для «своё у …»', () => {
  it('склоняет имена персон', () => {
    const cases: Record<string, string> = {
      Вера: 'Веры', Лиза: 'Лизы', Кира: 'Киры', Ника: 'Ники', Полина: 'Полины',
      // после г/к/х/ж/ч/ш/щ — «и»
      Ольга: 'Ольги', Наташа: 'Наташи', Вика: 'Вики',
      София: 'Софии', Мария: 'Марии', Таня: 'Тани', Наталья: 'Натальи', Майя: 'Майи',
      Игорь: 'Игоря', Дмитрий: 'Дмитрия', Сергей: 'Сергея', Алексей: 'Алексея',
      Глеб: 'Глеба', Александр: 'Александра', Тимур: 'Тимура', Ассистент: 'Ассистента',
    };
    for (const [name, expected] of Object.entries(cases)) expect(genitiveName(name)).toBe(expected);
  });

  it('латиницу не склоняет', () => {
    expect(genitiveName('Claude')).toBe('Claude');
    expect(genitiveName('GLM 5.2')).toBe('GLM 5.2');
  });

  it('неоднозначное и составное — null', () => {
    expect(genitiveName('Нико')).toBeNull();
    expect(genitiveName('Вера Павловна')).toBeNull();
    expect(genitiveName('ГР')).toBeNull();
    expect(genitiveName('')).toBeNull();
  });
});
