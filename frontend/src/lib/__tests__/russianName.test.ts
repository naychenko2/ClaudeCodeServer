import { describe, expect, it } from 'vitest';
import { dativeName } from '../russianName';

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
