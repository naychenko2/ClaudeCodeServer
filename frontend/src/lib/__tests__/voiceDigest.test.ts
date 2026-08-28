// Выжимка для озвучки (стиль digest): извлечение блока <voice> и его безусловный вырез
// из текста, который идёт в синтез речи и в ленту.

import { describe, it, expect } from 'vitest';
import { extractVoiceDigest, splitVoiceDigest, splitBoldSpans, splitBulletKind } from '../turnSpeechStream';
import { stripVoiceMarker, sanitizeForSpeech } from '../tts';
import { normalizeVoiceStyle, isVoiceStyle, voiceStyleFor } from '../voiceStyle';

describe('extractVoiceDigest', () => {
  it('берёт содержимое блока в конце ответа', () => {
    const text = 'Полный ответ с разбором.\n\n<voice>Коротко: делай через А.</voice>';
    expect(extractVoiceDigest(text)).toBe('Коротко: делай через А.');
  });

  it('без маркера возвращает null', () => {
    expect(extractVoiceDigest('Обычный ответ без выжимки.')).toBeNull();
  });

  it('игнорирует маркер внутри блока кода — ответ про саму фичу показывает его примером', () => {
    const text = 'Формат такой:\n\n```\n<voice>Пример из документации.</voice>\n```\n\nЭто всё.';
    expect(extractVoiceDigest(text)).toBeNull();
  });

  it('берёт последний блок, если модель выдала два', () => {
    const text = '<voice>Первая.</voice>\nЕщё текст.\n<voice>Вторая.</voice>';
    expect(extractVoiceDigest(text)).toBe('Вторая.');
  });

  it('незакрытый блок выжимкой не считается — ход ещё стримится', () => {
    expect(extractVoiceDigest('Ответ.\n\n<voice>Начало выжим')).toBeNull();
  });

  it('пустой блок — не выжимка', () => {
    expect(extractVoiceDigest('Ответ.\n<voice>   </voice>')).toBeNull();
  });
});

describe('stripVoiceMarker', () => {
  it('режет всё от маркера до конца текста', () => {
    expect(stripVoiceMarker('Тело ответа.\n\n<voice>Суть.</voice>').trim()).toBe('Тело ответа.');
  });

  it('режет и полуоткрытый хвост стрима — иначе тег мигал бы в ленте', () => {
    expect(stripVoiceMarker('Тело ответа.\n\n<voi').trim()).toBe('Тело ответа.');
    expect(stripVoiceMarker('Тело ответа.\n\n<voice>Нача').trim()).toBe('Тело ответа.');
  });

  it('текст без маркера не трогает', () => {
    expect(stripVoiceMarker('Просто ответ.')).toBe('Просто ответ.');
  });

  // Ответ, показывающий формат маркера примером, не должен обрываться на экране —
  // а это любой разговор про саму фичу и любая цитата из доков проекта
  it('не режет по маркеру внутри блока кода', () => {
    const text = 'Формат такой:\n\n```\n<voice>Пример.</voice>\n```\n\nПродолжение ответа.';
    const out = stripVoiceMarker(text);
    expect(out).toContain('Продолжение ответа');
    expect(out).toContain('<voice>Пример.</voice>');
  });

  it('режет настоящий маркер после блока кода', () => {
    const text = 'Код:\n\n```\nconst a = 1;\n```\n\nВывод.\n\n<voice>Суть.</voice>';
    const out = stripVoiceMarker(text);
    expect(out).toContain('const a = 1');
    expect(out).toContain('Вывод.');
    expect(out).not.toContain('Суть.');
  });

  it('незакрытый блок кода на стриме не трогает', () => {
    const text = 'Смотри:\n\n```\n<voice>внутри кода';
    expect(stripVoiceMarker(text)).toBe(text);
  });

  it('одиночный «<» в конце ответа не теряется', () => {
    expect(stripVoiceMarker('Сравни a <')).toBe('Сравни a <');
  });

  // Живая регрессия: ответ про схемы упомянул ```mermaid прямо в строке таблицы, парсер
  // счёл эти бэктики открытием блока кода, счёт фенсов уехал — и остаток ответа
  // (вместе с маркером) остался «внутри кода». Маркер уехал в ленту сырым
  it('тройные бэктики посреди строки блока кода не открывают', () => {
    const text = [
      '| файл | было | стало |',
      '| --- | --- | --- |',
      '| style.md | нельзя определить | интерфейс сам просит ```mermaid в промпте |',
      '',
      '```mermaid',
      'flowchart TD',
      '  A --> B',
      '```',
      '',
      '<voice>Суть ответа.</voice>',
    ].join('\n');
    const out = stripVoiceMarker(text);
    expect(out).toContain('flowchart TD');
    expect(out).toContain('в промпте');
    expect(out).not.toContain('<voice>');
    // Второй парсер обязан видеть тот же текст: разъедься они — плашка есть, маркер сырой
    expect(extractVoiceDigest(text)).toBe('Суть ответа.');
  });

  it('фенс с отступом до трёх пробелов (пункт списка) блоком остаётся', () => {
    const text = '- пример:\n\n   ```\n   <voice>Не выжимка.</voice>\n   ```\n\nХвост.';
    expect(stripVoiceMarker(text)).toBe(text);
    expect(extractVoiceDigest(text)).toBeNull();
  });
});

describe('sanitizeForSpeech и маркер', () => {
  // Вырез безусловный: маркер остаётся в истории и после выключения digest, а тегов
  // санитайзер иначе не трогает вовсе — синтезатор зачитал бы «voice» вслух
  it('не отдаёт маркер в синтез речи', () => {
    const spoken = sanitizeForSpeech('Ответ целиком.\n\n<voice>Суть ответа.</voice>');
    expect(spoken).not.toContain('voice');
    expect(spoken).toContain('Ответ целиком');
  });
});

describe('normalizeVoiceStyle', () => {
  it('пустое, битое и легаси значение — разговор', () => {
    expect(normalizeVoiceStyle(undefined)).toBe('talk');
    expect(normalizeVoiceStyle(null)).toBe('talk');
    expect(normalizeVoiceStyle('')).toBe('talk');
    expect(normalizeVoiceStyle('shout')).toBe('talk');
  });

  it('известные значения пропускает как есть', () => {
    expect(normalizeVoiceStyle('talk')).toBe('talk');
    expect(normalizeVoiceStyle('digest')).toBe('digest');
    expect(isVoiceStyle('digest')).toBe(true);
    expect(isVoiceStyle('shout')).toBe(false);
  });
});

// Стиль не настраивается — он функция устройства. Телефон в руке слышит ответ целиком,
// экран перед глазами получает полный ответ и пересказ вслух
describe('voiceStyleFor', () => {
  it('узкий экран — разговор, широкий — пересказ', () => {
    expect(voiceStyleFor(true)).toBe('talk');
    expect(voiceStyleFor(false)).toBe('digest');
  });
});

// Плашка «Коротко» показывает выжимку тезисами (вывод + пункты) — переносы строк в
// обычном div схлопываются, поэтому разбор делается кодом, а не разметкой
describe('splitVoiceDigest', () => {
  it('делит на вывод и тезисы', () => {
    const { lead, bullets } = splitVoiceDigest('Правку можно катить.\n- собрал бэкенд\n- прогнал тесты');
    expect(lead).toBe('Правку можно катить.');
    expect(bullets).toEqual(['собрал бэкенд', 'прогнал тесты']);
  });

  it('сплошной абзац остаётся выводом — старые ответы и стиль digest', () => {
    const { lead, bullets } = splitVoiceDigest('Всё готово, ничего делать не надо.');
    expect(lead).toBe('Всё готово, ничего делать не надо.');
    expect(bullets).toEqual([]);
  });

  it('строка без маркера после пунктов — перенос последнего тезиса', () => {
    const { bullets } = splitVoiceDigest('Итог.\n- первый тезис\n  и его хвост\n- второй');
    expect(bullets).toEqual(['первый тезис и его хвост', 'второй']);
  });

  it('понимает звёздочку и буллет как маркер пункта', () => {
    const { lead, bullets } = splitVoiceDigest('* один\n• два');
    expect(lead).toBe('');
    expect(bullets).toEqual(['один', 'два']);
  });
});

// Жирные опоры в плашке «Коротко»: единственная разметка, разрешённая внутри блока.
// Показ и речь разбирают звёздочки одинаково, поэтому рядом же проверяем санитайзер
describe('splitBoldSpans', () => {
  it('делит строку на обычные и жирные отрезки', () => {
    expect(splitBoldSpans('**Список чатов** помечает **значком** правки')).toEqual([
      { text: 'Список чатов', bold: true },
      { text: ' помечает ', bold: false },
      { text: 'значком', bold: true },
      { text: ' правки', bold: false },
    ]);
  });

  it('строка без выделений — один обычный отрезок', () => {
    expect(splitBoldSpans('Обычный тезис.')).toEqual([{ text: 'Обычный тезис.', bold: false }]);
  });

  it('непарная звёздочка остаётся текстом — иначе жирным уехало бы полстроки', () => {
    expect(splitBoldSpans('оценка 5**, а не 4')).toEqual([{ text: 'оценка 5**, а не 4', bold: false }]);
  });

  it('маркер пункта разбирается раньше жирного и его не съедает', () => {
    const { bullets } = splitVoiceDigest('Итог.\n- **собрал** бэкенд');
    expect(bullets).toEqual(['**собрал** бэкенд']);
    expect(splitBoldSpans(bullets[0])[0]).toEqual({ text: 'собрал', bold: true });
  });

  it('вслух звёздочки не читаются — санитайзер их срезает', () => {
    expect(sanitizeForSpeech('**Список чатов** помечает правки')).toContain('Список чатов помечает');
    expect(sanitizeForSpeech('**Список чатов** помечает правки')).not.toContain('*');
  });
});

// Пометка типа тезиса: на экране она значок, в речи её нет вовсе. Разметка, а не текст
describe('splitBulletKind', () => {
  it('снимает пометку и отдаёт тип', () => {
    expect(splitBulletKind('[+] собрал бэкенд')).toEqual({ kind: 'done', text: 'собрал бэкенд' });
    expect(splitBulletKind('[!] может сломаться')).toEqual({ kind: 'risk', text: 'может сломаться' });
    expect(splitBulletKind('[>] осталось выкатить')).toEqual({ kind: 'next', text: 'осталось выкатить' });
  });

  it('без пометки текст не трогается — старые ответы и забывчивость модели', () => {
    expect(splitBulletKind('обычный тезис')).toEqual({ kind: null, text: 'обычный тезис' });
  });

  it('пометка в середине строки не считается — она только в начале', () => {
    expect(splitBulletKind('правка [+] в тексте')).toEqual({ kind: null, text: 'правка [+] в тексте' });
  });

  it('пустой пункт остаётся как есть — иначе строка исчезла бы с экрана', () => {
    expect(splitBulletKind('[+]')).toEqual({ kind: null, text: '[+]' });
  });

  it('работает поверх разбора выжимки и не мешает жирному', () => {
    const { bullets } = splitVoiceDigest('Итог.\n- [!] **второй акцент** смажет первый');
    const { kind, text } = splitBulletKind(bullets[0]);
    expect(kind).toBe('risk');
    expect(splitBoldSpans(text)[0]).toEqual({ text: 'второй акцент', bold: true });
  });

  it('вслух пометка не звучит — санитайзер срезает её вместе с дефисом', () => {
    const spoken = sanitizeForSpeech('Итог.\n- [+] собрал бэкенд\n- [!] может сломаться');
    expect(spoken).toContain('собрал бэкенд');
    expect(spoken).not.toContain('[');
    expect(spoken).not.toContain('+');
  });
});
