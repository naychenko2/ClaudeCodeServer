import { describe, it, expect } from 'vitest';
import { queueLabel, queueLabelParts, queueOrdinalWord, queueCountWord, queuePhraseParts } from './chatGroups';

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

// Q5I-1: словарь позиции словами для блока «Фича в очереди» (макет feature-queue-1.md).
// Отдельный от числовой подписи queueLabelParts: та живёт в плашке/пилюле/рельсе
// цифрами и этим шагом не меняется (постановка Q5I-1).

describe('queueOrdinalWord: порядковое слово позиции', () => {
  it('1–10 — словами, женский род (согласуется с «фичей»)', () => {
    const words = ['первая', 'вторая', 'третья', 'четвёртая', 'пятая', 'шестая', 'седьмая', 'восьмая', 'девятая', 'десятая'];
    words.forEach((w, i) => expect(queueOrdinalWord(i + 1)).toBe(w));
  });

  it('11 и дальше — «{N}-я»: форма за словами закреплена тестом, не «на глаз»', () => {
    // Порядок 11+ не имеет надёжной словоформы (11–19 легко портятся: «одинцатая»),
    // а слово за 20 не читается быстрее цифр — «{N}-я» единственная предсказуемая форма.
    expect(queueOrdinalWord(11)).toBe('11-я');
    expect(queueOrdinalWord(12)).toBe('12-я');
    expect(queueOrdinalWord(21)).toBe('21-я');
    expect(queueOrdinalWord(99)).toBe('99-я');
  });
});

describe('queueCountWord: длина очереди словом в родительном падеже', () => {
  it('1–10 — родительный падеж («из трёх [фич]»)', () => {
    const words = ['одной', 'двух', 'трёх', 'четырёх', 'пяти', 'шести', 'семи', 'восьми', 'девяти', 'десяти'];
    words.forEach((w, i) => expect(queueCountWord(i + 1)).toBe(w));
  });

  it('11 и дальше — цифрами: слова макет ограничил рамкой 1–10', () => {
    // Строка макета «Длину очереди пишем словами» идёт сразу за перечнем «одной…десяти»;
    // за рамкой слова не заданы, а плашка/пилюля/рельса длину уже дают числом — «11»
    // в блоке остаётся в одной системе с ними.
    expect(queueCountWord(11)).toBe('11');
    expect(queueCountWord(12)).toBe('12');
  });
});

describe('queuePhraseParts: фраза блока — структура, а не строка для разбора', () => {
  it('части отдельные, разделитель « из » — внутри хелпера', () => {
    const p = queuePhraseParts({ position: 2, length: 3 });
    expect(p).not.toBeNull();
    expect(p!.phrase).toBe('вторая из трёх');
    expect(p!.ordinal).toBe('вторая');
    expect(p!.count).toBe('трёх');
    expect(p!.sep).toBe(' из ');
    // phrase собрана из частей: потребитель не разбирает строку (урок R-Q5D-D).
    expect(p!.phrase).toBe(p!.ordinal + p!.sep + p!.count);
  });

  it('граница слов: 10 — последнее слово, 11 — уже другая форма', () => {
    expect(queuePhraseParts({ position: 10, length: 10 })!.phrase).toBe('десятая из десяти');
    expect(queuePhraseParts({ position: 11, length: 11 })!.phrase).toBe('11-я из 11');
  });

  it('нет валидной очереди — null (тот же признак, что у queueLabelParts)', () => {
    for (const q of [null, undefined, { position: 0, length: 3 }, { position: 2, length: 0 }]) {
      expect(queuePhraseParts(q)).toBeNull();
    }
  });

  it('числовая подпись не тронута: «в очереди · 2 из 3» живёт своим форматом', () => {
    // Q5I-1 не переводит плашку/пилюлю/рельсу на слова — это отдельное решение.
    expect(queueLabel({ position: 2, length: 3 })).toBe('в очереди · 2 из 3');
  });
});
