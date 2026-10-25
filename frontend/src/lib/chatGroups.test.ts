import { describe, it, expect } from 'vitest';
import { queueLabel, queueLabelParts } from './chatGroups';

// R-Q5D-D (ревью RV-K-Q5D-B, миноры 2–4): подпись очереди — структура, а не строка.
// Потребители (пилюля, плашка, рельса) берут поля, а не разбирают строку по ' · '.
// Смена разделителя/формата в хелпере не может молча убрать цифры из подписи и aria:
// тест «цифры в каждой части» краснеет на такую смену (c2).

describe('queueLabelParts: структура вместо разбора строки', () => {
  it('есть позиция — все части отдельные (label / bold / post / ariaSuffix)', () => {
    const p = queueLabelParts({ position: 2, length: 3 });
    expect(p.label).toBe('в очереди · 2 из 3');
    expect(p.bold).toBe('В очереди');
    expect(p.post).toBe(' · 2 из 3');
    expect(p.ariaSuffix).toBe(' · в очереди 2 из 3');
  });

  it('нет queue / нули — нигде нет цифр: label «в очереди», aria — « · в очереди»', () => {
    for (const q of [null, undefined, { position: 0, length: 3 }, { position: 2, length: 0 }]) {
      const p = queueLabelParts(q);
      expect(p.label).toBe('в очереди');
      expect(p.bold).toBe('В очереди');
      expect(p.post).toBe('');
      expect(p.ariaSuffix).toBe(' · в очереди');
    }
  });

  it('цифры есть в каждой несущей их части: смена формата не может молча их убрать (c2)', () => {
    // Тест не зависит от разделителя: проверяет наличие цифр в каждом поле структуры.
    // Смена формата в хелпере, убравшая цифры из label, суффикса или aria, — красная
    // здесь, а не потерянная молча в подписи и aria (так работали разбор по ' · ' и
    // магическое sep + 3 в FeatureBanner до этого шага).
    const p = queueLabelParts({ position: 7, length: 12 });
    const nums = '7 из 12';
    expect(p.label).toContain(nums);
    expect(p.post).toContain(nums);
    expect(p.ariaSuffix).toContain(nums);
  });
});

describe('queueLabel: строка = label структуры (один источник формата)', () => {
  it('queueLabel побайтно равен parts.label при тех же входах', () => {
    for (const q of [null, undefined, { position: 2, length: 3 }, { position: 1, length: 1 }]) {
      expect(queueLabel(q)).toBe(queueLabelParts(q).label);
    }
  });
});
