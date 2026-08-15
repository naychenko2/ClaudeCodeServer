import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { sanitizeForSpeech, splitSentences } from '../tts';

// Запрос синтеза подменяем на управляемый: тесты промиса озвучки должны уметь и отдать
// «озвучено», и подвесить запрос навсегда (проверка немедленного резолва stopSpeaking)
const hoisted = vi.hoisted(() => ({
  requestImpl: (() => Promise.resolve(null)) as (path: string, opts?: unknown) => Promise<unknown>,
}));
vi.mock('../offline', () => ({
  request: (path: string, opts?: unknown) => hoisted.requestImpl(path, opts),
  subscribeConnectionState: () => () => {},
  getConnectionState: () => 'online',
}));

// Санитайзер и нарезка озвучки: весь риск «мусора в ушах» здесь, поэтому под тестом
describe('sanitizeForSpeech', () => {
  it('вырезает блоки кода целиком', () => {
    const md = 'Смотри так:\n```ts\nconst x = 1;\n```\nВот и всё.';
    const out = sanitizeForSpeech(md);
    expect(out).not.toContain('const x');
    expect(out).toContain('Смотри так');
    expect(out).toContain('Вот и всё');
  });

  it('убирает строки таблиц и ASCII-рамки', () => {
    const md = 'Итог:\n| Поле | Значение |\n|---|---|\n| a | 1 |\n+-----+\nГотово.';
    const out = sanitizeForSpeech(md);
    expect(out).not.toContain('|');
    expect(out).not.toContain('+---');
    expect(out).toContain('Итог');
    expect(out).toContain('Готово');
  });

  it('схлопывает ссылки', () => {
    expect(sanitizeForSpeech('Читай [документацию](https://example.com/docs) внимательно.'))
      .toBe('Читай документацию внимательно.');
    expect(sanitizeForSpeech('Иди на https://example.com сейчас.'))
      .toBe('Иди на ссылка сейчас.');
  });

  it('снимает markdown-разметку и маркеры списков', () => {
    const out = sanitizeForSpeech('## Заголовок\n- **важный** пункт\n> цитата');
    expect(out).not.toMatch(/[#*>]/);
    expect(out).toContain('Заголовок');
    expect(out).toContain('важный пункт');
    expect(out).toContain('цитата');
  });

  it('снимает нумерацию списка — иначе «1.» уходит отдельным куском на синтез', () => {
    const out = sanitizeForSpeech('1. Первый пункт\n2) Второй пункт');
    expect(out).toBe('Первый пункт\nВторой пункт');
    expect(splitSentences(out)).toEqual(['Первый пункт', 'Второй пункт']);
  });

  it('обрезает длинный текст и предупреждает про экран', () => {
    const long = Array.from({ length: 200 }, (_, i) => `Предложение номер ${i}.`).join(' ');
    const out = sanitizeForSpeech(long);
    expect(out.length).toBeLessThan(long.length);
    expect(out.length).toBeLessThanOrEqual(1600);
    expect(out).toContain('Дальше смотри на экране');
  });

  it('пустой ввод даёт пустую строку', () => {
    expect(sanitizeForSpeech('')).toBe('');
    expect(sanitizeForSpeech('   \n  ')).toBe('');
  });
});

describe('splitSentences', () => {
  it('режет по знакам конца предложения', () => {
    expect(splitSentences('Первое. Второе! Третье?'))
      .toEqual(['Первое.', 'Второе!', 'Третье?']);
  });

  it('не рвёт на сокращениях', () => {
    const parts = splitSentences('Возьми т.е. вот это. И всё.');
    expect(parts).toHaveLength(2);
    expect(parts[0]).toBe('Возьми т.е. вот это.');
  });

  it('многоточие и «?!» остаются одним куском', () => {
    expect(splitSentences('Ну как же так?! Вот так... Понятно.'))
      .toEqual(['Ну как же так?!', 'Вот так...', 'Понятно.']);
  });

  it('не даёт пустых кусков', () => {
    const parts = splitSentences('Раз.  \n\n  Два.   ');
    expect(parts).toEqual(['Раз.', 'Два.']);
    expect(parts.every(p => p.trim().length > 0)).toBe(true);
  });

  it('точка внутри числа не рвёт предложение', () => {
    expect(splitSentences('Это 3.14 примерно.')).toEqual(['Это 3.14 примерно.']);
  });

  it('пустой ввод — пустой список', () => {
    expect(splitSentences('')).toEqual([]);
    expect(splitSentences('   ')).toEqual([]);
  });
});

// Промис озвучки — контракт режима разговора: микрофон открывается ровно по нему,
// поэтому «резолв раньше, чем синтезатор замолчал» = петля слышит собственный голос
describe('speak / stopSpeaking', () => {
  // Что происходит с созданным Audio: держим ссылки, чтобы дёргать onended вручную
  const played: { onended: (() => void) | null; onerror: (() => void) | null; paused: boolean }[] = [];
  // Реплики браузерного синтезатора — тот же приём
  const uttered: { onend: (() => void) | null; onerror: (() => void) | null }[] = [];

  beforeEach(() => {
    vi.resetModules();
    played.length = 0;
    uttered.length = 0;
    hoisted.requestImpl = () => Promise.resolve(new Blob(['x']));

    class FakeAudio {
      onended: (() => void) | null = null;
      onerror: (() => void) | null = null;
      paused = false;
      src = '';
      constructor() { played.push(this); }
      play() { return Promise.resolve(); }
      pause() { this.paused = true; }
    }
    class FakeUtterance {
      onend: (() => void) | null = null;
      onerror: (() => void) | null = null;
      lang = '';
      voice: unknown = null;
      constructor(public text: string) { uttered.push(this); }
    }
    Object.assign(globalThis, {
      Audio: FakeAudio,
      SpeechSynthesisUtterance: FakeUtterance,
      speechSynthesis: {
        speaking: false,
        pending: false,
        speak: () => {},
        cancel: () => { for (const u of uttered) u.onerror?.(); },
        getVoices: () => [],
        addEventListener: () => {},
        removeEventListener: () => {},
      },
    });
    globalThis.URL.createObjectURL = () => 'blob:test';
    globalThis.URL.revokeObjectURL = () => {};
  });

  afterEach(() => {
    delete (globalThis as Record<string, unknown>).Audio;
    delete (globalThis as Record<string, unknown>).SpeechSynthesisUtterance;
    delete (globalThis as Record<string, unknown>).speechSynthesis;
  });

  // Промис не считается выполненным, пока его не резолвили: гонка с маркером
  const settled = async (p: Promise<void>) =>
    await Promise.race([p.then(() => true), new Promise<boolean>(r => setTimeout(() => r(false), 20))]);

  it('резолвится после проигрывания последнего куска', async () => {
    const { speak } = await import('../tts');
    const p = speak('Первое. Второе.');
    await new Promise(r => setTimeout(r, 5));
    expect(await settled(p)).toBe(false); // первый кусок ещё играет
    played[0].onended?.();
    await new Promise(r => setTimeout(r, 5));
    expect(played).toHaveLength(2);
    played[1].onended?.();
    expect(await settled(p)).toBe(true);
  });

  it('stopSpeaking резолвит висящий промис сразу, не дожидаясь запроса синтеза', async () => {
    const { speak, stopSpeaking } = await import('../tts');
    hoisted.requestImpl = () => new Promise(() => {}); // запрос завис (в жизни — до 45 с)
    const p = speak('Ответ.');
    expect(await settled(p)).toBe(false);
    stopSpeaking();
    expect(await settled(p)).toBe(true);
  });

  it('браузерный фолбэк: промис не резолвится раньше onend', async () => {
    const { speak } = await import('../tts');
    // 503 not_configured — самый частый путь отказа, именно здесь раньше был канал эха
    hoisted.requestImpl = () => Promise.reject(Object.assign(new Error('no tts'), {
      status: 503, body: { reason: 'not_configured' },
    }));
    const p = speak('Ответ голосом браузера.');
    await new Promise(r => setTimeout(r, 5));
    expect(uttered).toHaveLength(1);
    expect(await settled(p)).toBe(false); // синтезатор ещё говорит
    uttered[0].onend?.();
    expect(await settled(p)).toBe(true);
  });
});
