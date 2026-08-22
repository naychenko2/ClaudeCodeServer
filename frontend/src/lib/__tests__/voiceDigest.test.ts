// Выжимка для озвучки (стиль digest): извлечение блока <voice> и его безусловный вырез
// из текста, который идёт в синтез речи и в ленту.

import { describe, it, expect } from 'vitest';
import { extractVoiceDigest } from '../turnSpeechStream';
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
