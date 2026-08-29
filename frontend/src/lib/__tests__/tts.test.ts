import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { sanitizeForSpeech, splitSentences, verbalizeIdentifiers, takeSpeakableChunk, MAX_STREAM_CHUNK, packSentences, PACK_LIMIT } from '../tts';

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

  it('имена файлов из backtick-кода расшифровываются на слух', () => {
    const out = sanitizeForSpeech('Правлю `SessionManager.cs`, потом `useHandsFree.ts`.');
    expect(out).not.toContain('SessionManager.cs');
    expect(out).not.toContain('useHandsFree.ts');
    expect(out).toContain('Session Manager це шарп');
    expect(out).toContain('use Hands Free тайпскрипт');
  });

  it('пути режутся на сегменты вместо слитной каши', () => {
    const out = sanitizeForSpeech('Смотри frontend/src/lib/tts.ts целиком.');
    expect(out).toContain('frontend src lib tts тайпскрипт');
  });

  it('незнакомое расширение отбрасывается, файл не теряется', () => {
    expect(verbalizeIdentifiers('config.xyzabc')).toBe('config');
  });

  it('версия не разваливается на буквы', () => {
    expect(verbalizeIdentifiers('обновил до v1.2.3')).toBe('обновил до v1 2 3');
  });

  it('известные акронимы читаются по-русски, а не «мкп»', () => {
    expect(verbalizeIdentifiers('подключи MCP сервер')).toBe('подключи эм си пи сервер');
    expect(verbalizeIdentifiers('чиню API')).toBe('чиню апи');
  });

  it('русская речь не трогается', () => {
    expect(verbalizeIdentifiers('Обычный текст без идентификаторов.'))
      .toBe('Обычный текст без идентификаторов.');
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
// Упаковка предложений в пакеты: синтез v3 тарифицируется за ЗАПРОС, поэтому цена ошибки
// здесь — деньги (мелкая нарезка) либо задержка первого звука (крупная)
describe('packSentences', () => {
  const s = (len: number, word = 'слово') => Array(Math.ceil(len / (word.length + 1)))
    .fill(word).join(' ').slice(0, len).trim();

  it('первое предложение уходит отдельным пакетом — это разгон звука', () => {
    const packs = packSentences(['Раз.', 'Два.', 'Три.']);
    expect(packs[0]).toBe('Раз.');
  });

  it('следующие предложения клеятся в пакеты под лимит', () => {
    const packs = packSentences(['Раз.', s(100), s(100), s(100)]);
    expect(packs[0]).toBe('Раз.');
    expect(packs.length).toBeLessThan(4); // без упаковки было бы 4 запроса
    for (const p of packs) expect(p.length).toBeLessThanOrEqual(PACK_LIMIT);
  });

  it('ни один пакет не длиннее лимита запроса', () => {
    const packs = packSentences(Array(30).fill(s(80)));
    for (const p of packs) expect(p.length).toBeLessThanOrEqual(PACK_LIMIT);
  });

  it('текст не теряется и не переставляется', () => {
    const parts = ['Первое.', 'Второе.', 'Третье.', 'Четвёртое.', 'Пятое.'];
    expect(packSentences(parts).join(' ')).toBe(parts.join(' '));
  });

  it('короткий хвост приклеивается к предыдущему пакету, а не едет огрызком', () => {
    // Огрызок тарифицируется как полный запрос — отдельным пакетом он ехать не должен
    const packs = packSentences(['Раз.', s(200), s(200), 'Ага.']);
    expect(packs[packs.length - 1]).not.toBe('Ага.');
    expect(packs[packs.length - 1].endsWith('Ага.')).toBe(true);
  });

  it('из двух пакетов хвост не приклеивается — разгонный не удлиняем', () => {
    const packs = packSentences(['Раз.', 'Ага.']);
    expect(packs).toEqual(['Раз.', 'Ага.']);
  });

  // Тезисы выжимки «Коротко» приходят без точек (так просит промпт — точка в конце пункта
  // засоряет плашку на экране), а пакет уезжает в синтез одной строкой. Склеенные пробелом,
  // они читались вслух слитно, без пауз между пунктами
  it('фразы без терминального знака склеиваются точкой, а не пробелом', () => {
    const packs = packSentences(['Главный вывод.', 'собрал бэкенд', 'риск обрыва связи']);
    expect(packs[1]).toBe('собрал бэкенд. риск обрыва связи');
  });

  it('готовая пунктуация на стыке не дублируется', () => {
    const packs = packSentences(['Раз.', 'Два!', 'три', 'четыре…', 'пять']);
    expect(packs[1]).toBe('Два! три. четыре… пять');
  });

  it('точка на стыке не выталкивает пакет за лимит запроса', () => {
    // Фразы без точек: склейка длиннее на символ, и лимит легко переехать на 250
    const packs = packSentences(Array(40).fill(s(60)));
    for (const p of packs) expect(p.length).toBeLessThanOrEqual(PACK_LIMIT);
  });

  it('пустые куски отбрасываются', () => {
    expect(packSentences(['', '   ', 'Раз.'])).toEqual(['Раз.']);
    expect(packSentences([])).toEqual([]);
  });

  // Ради этого весь переход на v3 (speechkit-pricing.md §4): запрос стоит одинаково
  // независимо от длины, поэтому число ЗАПРОСОВ — это и есть счёт. Выигрыш зависит от
  // длины предложений: короткие набиваются в пакет плотно, длинные — по два-три
  it('короткие фразы голосового режима: шесть предложений едут тремя запросами', () => {
    // 353 символа: без упаковки это 6 запросов (0,98 ₽) против 0,49 ₽ тремя пакетами
    const sentences = Array.from({ length: 6 }, (_, i) => `${s(55)} ${i}.`);
    const packs = packSentences(sentences);
    expect(packs.length).toBe(3);
    for (const p of packs) expect(p.length).toBeLessThanOrEqual(PACK_LIMIT);
  });

  it('длинные предложения: ответ на 600+ символов едет четырьмя запросами вместо шести', () => {
    // Пакеты не набиваются под завязку осознанно: предложения не режем пополам,
    // поэтому 101+203+203+101, а не три полных пакета
    const sentences = Array.from({ length: 6 }, (_, i) => `${s(98)} номер ${i}.`);
    expect(sentences.join(' ').length).toBeGreaterThan(600);
    expect(packSentences(sentences).length).toBe(4);
  });
});

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

  // --- Поточная озвучка (StreamSpeech) — те же заглушки, тот же скоуп ---

  const settled20 = async (p: Promise<void>) =>
    await Promise.race([p.then(() => true), new Promise<boolean>(r => setTimeout(() => r(false), 20))]);

  it('стрим: озвучивает куски по порядку, done — по концу ПОСЛЕДНЕГО куска', async () => {
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    s.enqueue('Первое.');
    s.enqueue('Второе.');
    s.end();
    await new Promise(r => setTimeout(r, 5));
    expect(played).toHaveLength(1); // играет первый, второй синтезируется
    played[0].onended?.();
    await new Promise(r => setTimeout(r, 5));
    expect(played).toHaveLength(2);
    expect(await settled20(p)).toBe(false); // второй ещё играет — done не резолвится
    played[1].onended?.();
    expect(await settled20(p)).toBe(true);
  });

  it('стрим: пустая очередь без end() — done ждёт, это не EOF', async () => {
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    await new Promise(r => setTimeout(r, 10));
    expect(await settled20(p)).toBe(false);
    s.stop();
    expect(await settled20(p)).toBe(true);
  });

  it('стрим: enqueue после end() — no-op, поздние дельты не читаются', async () => {
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    s.enqueue('Единственный.');
    s.end();
    s.enqueue('Поздний хвост.');
    await new Promise(r => setTimeout(r, 5));
    expect(played).toHaveLength(1);
    played[0].onended?.();
    expect(await settled20(s.done)).toBe(true);
    expect(played).toHaveLength(1); // поздний кусок не проигрался
  });

  it('стрим: пустой и whitespace enqueue игнорируются', async () => {
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    s.enqueue('');
    s.enqueue('   ');
    s.enqueue('Нормальный кусок.');
    s.end();
    await new Promise(r => setTimeout(r, 5));
    expect(played).toHaveLength(1);
    played[0].onended?.();
    expect(await settled20(s.done)).toBe(true);
  });

  it('стрим: stop() посреди очереди резолвит done и отменяет хвост', async () => {
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    s.enqueue('Первый.');
    s.enqueue('Второй.');
    await new Promise(r => setTimeout(r, 5));
    expect(played).toHaveLength(1);
    s.stop();
    expect(await settled20(p)).toBe(true); // немедленно, не дожидаясь хвоста
    const count = played.length;
    await new Promise(r => setTimeout(r, 10));
    expect(played).toHaveLength(count); // хвост очереди не играет
  });

  it('стрим: глобальный stopSpeaking() гасит активный стрим', async () => {
    const { startStreamSpeak, stopSpeaking } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    s.enqueue('Кусок.');
    await new Promise(r => setTimeout(r, 5));
    stopSpeaking();
    expect(await settled20(p)).toBe(true);
    const count = played.length;
    await new Promise(r => setTimeout(r, 10));
    expect(played).toHaveLength(count);
  });

  it('стрим: следующий speak() отбирает звук у стрима (общий токен)', async () => {
    const { speak, startStreamSpeak, stopSpeaking } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    s.enqueue('Стримовый кусок.');
    s.end();
    await new Promise(r => setTimeout(r, 5));
    const p2 = speak('Обычная озвучка.');
    expect(await settled20(p)).toBe(true); // стрим убит — done закрыт
    // доигрываем очередь speak, чтобы не течь в следующие тесты
    await new Promise(r => setTimeout(r, 5));
    for (const a of played) a.onended?.();
    stopSpeaking();
    void p2;
  });

  it('стрим: done не резолвится, пока последний кусок не ДОИГРАЛ', async () => {
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    s.enqueue('Долгий кусок.');
    s.end();
    await new Promise(r => setTimeout(r, 5));
    // Кусок встал в очередь и даже играет — но ещё не закончился
    expect(await settled20(p)).toBe(false);
    played[0].onended?.();
    expect(await settled20(p)).toBe(true);
  });

  it('стрим: фолбэк на браузерный голос при 503 — кусок доозвучивается, очередь живёт', async () => {
    hoisted.requestImpl = () => Promise.reject(Object.assign(new Error('no tts'), {
      status: 503, body: { reason: 'not_configured' },
    }));
    const { startStreamSpeak } = await import('../tts');
    const s = startStreamSpeak();
    const p = s.done;
    s.enqueue('Первый кусок.');
    await new Promise(r => setTimeout(r, 5));
    expect(uttered).toHaveLength(1);
    uttered[0].onend?.();
    s.enqueue('Второй кусок.'); // сервер уже помечен недоступным — сразу в браузер
    await new Promise(r => setTimeout(r, 5));
    expect(uttered).toHaveLength(2);
    uttered[1].onend?.();
    s.end();
    expect(await settled20(p)).toBe(true);
  });
});

describe('takeSpeakableChunk', () => {
  it('отдаёт предложение по каждому знаку конца', () => {
    let r = takeSpeakableChunk('Первое. Второе! Третье?', 0);
    expect(r).toEqual({ chunk: 'Первое.', cursor: 7, hitMarkup: false });
    r = takeSpeakableChunk('Первое. Второе! Третье?', r.cursor);
    expect(r.chunk).toBe('Второе!');
    r = takeSpeakableChunk('Первое. Второе! Третье?', r.cursor);
    expect(r.chunk).toBe('Третье?');
    // Всё озвучено: хвост пуст
    expect(takeSpeakableChunk('Первое. Второе! Третье?', r.cursor).chunk).toBeNull();
  });

  it('не рвёт на сокращениях и точке в числе', () => {
    expect(takeSpeakableChunk('Возьми т.е. вот это.', 0).chunk).toBe('Возьми т.е. вот это.');
    expect(takeSpeakableChunk('Это 3.14 примерно.', 0).chunk).toBe('Это 3.14 примерно.');
  });

  it('пачка знаков «?!» и «...» — одним куском', () => {
    expect(takeSpeakableChunk('Ну как же так?! Дальше.', 0).chunk).toBe('Ну как же так?!');
    expect(takeSpeakableChunk('Вот так... Понятно.', 0).chunk).toBe('Вот так...');
  });

  it('перенос строки — граница предложения', () => {
    expect(takeSpeakableChunk('Первая строка\nВторая строка.', 0).chunk).toBe('Первая строка');
  });

  it('нет пунктуации — null, курсор не двигается', () => {
    expect(takeSpeakableChunk('Предложение без конца', 0))
      .toEqual({ chunk: null, cursor: 0, hitMarkup: false });
    // Хвост без терминальной пунктуации не отдаётся и в конце текста — его закроет result
    expect(takeSpeakableChunk('Первое. Хвост без точки', 7))
      .toEqual({ chunk: null, cursor: 7, hitMarkup: false });
  });

  it('форс-рез по 400: по пробелу, не посреди слова', () => {
    const word = 'а'.repeat(50);
    const text = Array.from({ length: 12 }, (_, i) => `${word}${i}`).join(' ');
    const r = takeSpeakableChunk(text, 0);
    expect(r.chunk).not.toBeNull();
    expect(r.chunk!.length).toBeGreaterThan(0);
    expect(r.chunk!.length).toBeLessThanOrEqual(MAX_STREAM_CHUNK);
    // Рез ровно по пробелу: кусок — целые слова
    expect(r.chunk!.endsWith(' ')).toBe(false);
    expect(text.startsWith(r.chunk!)).toBe(true);
  });

  it('форс-рез по 400 без пробела вовсе — по лимиту насильно', () => {
    const text = 'б'.repeat(MAX_STREAM_CHUNK + 50);
    const r = takeSpeakableChunk(text, 0);
    expect(r.chunk).toBe('б'.repeat(MAX_STREAM_CHUNK));
    expect(r.cursor).toBe(MAX_STREAM_CHUNK);
  });

  it('код-блок впереди — hitMarkup, курсор не двигается', () => {
    const text = 'Смотри пример:\n```ts\nconst x = 1;\n```\nГотово.';
    expect(takeSpeakableChunk(text, 0)).toEqual({ chunk: null, cursor: 0, hitMarkup: true });
    // Текст ДО разметки отдаётся до самого markup: дальше курсор упирается в блок
    expect(takeSpeakableChunk('Сначала скажу.\nПотом код.', 0).chunk).toBe('Сначала скажу.');
    const after = takeSpeakableChunk('Сначала скажу.\n```код```', 13);
    expect(after.hitMarkup).toBe(true);
    expect(after.chunk).toBeNull();
  });

  it('строка таблицы впереди — hitMarkup', () => {
    expect(takeSpeakableChunk('Итог:\n| a | b |\nГотово.', 0)).toEqual({ chunk: null, cursor: 0, hitMarkup: true });
  });

  it('списки и заголовки — НЕ стоп-сигнал', () => {
    // \n — граница предложения (как в splitSentences): заголовок уходит куском сам,
    // а пункты списка ниже озвучиваются следующими кусками
    const r = takeSpeakableChunk('## Заголовок с точкой. пункт', 0);
    expect(r.hitMarkup).toBe(false);
    expect(r.chunk).toBe('## Заголовок с точкой.');
  });

  it('повторный вызов с новым cursor продолжает с места остановки', () => {
    const text = 'Раз. Два. Три.';
    let cursor = 0;
    const got: string[] = [];
    for (let i = 0; i < 5; i++) {
      const r = takeSpeakableChunk(text, cursor);
      if (!r.chunk) break;
      got.push(r.chunk);
      cursor = r.cursor;
    }
    expect(got).toEqual(['Раз.', 'Два.', 'Три.']);
  });

  it('эмодзи и пустой хвост — штатно', () => {
    // «!» завершает кусок; эмодзи в хвосте уедет следующим куском или на result
    const r = takeSpeakableChunk('Готово! 🔥', 0);
    expect(r.chunk).toBe('Готово!');
    expect(takeSpeakableChunk('', 0).chunk).toBeNull();
    expect(takeSpeakableChunk('   ', 0).chunk).toBeNull();
  });
});

