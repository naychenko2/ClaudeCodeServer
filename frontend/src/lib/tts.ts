// Озвучка ответов в голосовом режиме чата.
//
// Основной путь — mp3 с бэкенда (POST /api/tts, Yandex SpeechKit) через HTMLAudioElement:
// он продолжает играть при погашенном экране телефона, а speechSynthesis глохнет — для
// сценария «разговор на прогулке» это решающее. speechSynthesis остаётся фолбэком.
//
// Текст режется на предложения и синтезируется очередью: следующий кусок уходит на синтез,
// пока играет текущий, — иначе между фразами повисали бы паузы на круг до сервера.

import { request, subscribeOnline, isOnline } from './offline';
import { talkMark } from './talkDiag';

// Максимум символов, который отдаём на озвучку: лимит контроллера — 3000, но слушать
// простыню всё равно никто не станет — режем раньше и честно предупреждаем
const MAX_SPEAK_CHARS = 1500;
const TRUNCATION_NOTICE = ' Дальше смотри на экране.';

// Синтеза на сервере нет (503 not_configured) — постоянное состояние, запоминаем на сессию.
// Сбрасывается при возврате связи в online: 503 отдаёт и реверс-прокси в момент рестарта
// боевого инстанса, а не только «ключ не задан».
let serverTtsUnavailable = false;
let fallbackNoticeShown = false;
let connectionWatcherAttached = false;

function watchConnection() {
  if (connectionWatcherAttached || typeof window === 'undefined') return;
  connectionWatcherAttached = true;
  subscribeOnline(() => {
    if (isOnline()) serverTtsUnavailable = false;
  });
}

// --- Санитайзер: убираем из markdown всё, что нельзя прочитать вслух ---

// Расширения файлов — по-русски: «tts.ts» превращается в «tts тайпскрипт», а не «тстс».
// Незнакомые расширения отбрасываем: на слух они смысла не несут
const FILE_EXTENSIONS: Record<string, string> = {
  cs: 'це шарп', csproj: 'це шарп проект', sln: 'солюшн',
  ts: 'тайпскрипт', tsx: 'ти эс икс', js: 'джей эс', jsx: 'джей эс икс',
  json: 'джейсон', yml: 'яэмэл', yaml: 'яэмэл', xml: 'эм эл',
  py: 'пайтон', md: 'маркдаун', html: 'эйч ти эм эл', css: 'си эс эс',
  sql: 'эс ку эл', sh: 'шелл', ps1: 'поуэршелл', go: 'гоу', rs: 'раст',
  java: 'джава', php: 'пэха пэ', cshtml: 'се шарп эйч ти эм эл',
};

// Акронимы, которые синтезатор читает слитно («мкп») вместо побуквенно
const ACRONYMS: Record<string, string> = {
  MCP: 'эм си пи', API: 'апи', TTS: 'ти ти эс', STT: 'эс ти ти',
  CI: 'си ай', CD: 'си ди', UI: 'ю ай', UX: 'ю икс', PR: 'пи ар',
  HTTP: 'эйч ти ти пи', HTTPS: 'эйч ти ти пи эс', URL: 'у эр эл',
  JSON: 'джейсон', XML: 'эм эл', SQL: 'эс ку эл', CSS: 'си эс эс',
  JWT: 'джей ти', SDK: 'эс ди кей', LLM: 'эл эл эм', AI: 'а и',
};

// Расшифровка имён файлов и идентификаторов для синтеза речи. Слитная латиница
// («SessionManager.cs», «use-hands-free») один путь до ушей: неразборчивым словом-кашей.
// Чистая функция без состояния — под юнит-тестом.
export function verbalizeIdentifiers(text: string): string {
  // Токен с расширением файла: «SessionManager.cs» → «Session Manager» + «це шарп»
  text = text.replace(
    /([A-Za-z][\w.+-]*?)\.([A-Za-z]\w{0,6})\b/g,
    (_whole, base: string, ext: string) => {
      const spoken = FILE_EXTENSIONS[ext.toLowerCase()];
      return spoken ? `${base} ${spoken}` : base;
    },
  );

  // Разделители внутри идентификатора и сегменты пути → пробел: snake_case, kebab-case,
  // точки, слэши. Lookaround вместо захвата соседа: захват съедает символ, и в цепочке
  // «v1.2.3» резалась бы каждая вторая точка
  text = text.replace(/(?<=[A-Za-z\d])[-_.\/](?=[A-Za-z\d])/g, ' ');

  // CamelCase → пробел между словами («SessionManager» → «Session Manager»).
  // Версия «3.14» уже защищена порядком: точка между цифрами разрезана правилом выше,
  // а тут цифры не трогаем
  text = text.replace(/([a-z])([A-Z])/g, '$1 $2');

  // Известные акронимы — побуквенно/по-русски, иначе «MCP» читается «мкп»
  for (const [abbr, spoken] of Object.entries(ACRONYMS)) {
    text = text.replace(new RegExp(`\\b${abbr}\\b`, 'g'), spoken);
  }
  return text;
}

export function sanitizeForSpeech(md: string): string {
  if (!md) return '';
  let text = md;

  // Блоки кода целиком (```...```) и однострочный код `...`
  text = text.replace(/```[\s\S]*?```/g, ' ');
  text = text.replace(/~~~[\s\S]*?~~~/g, ' ');
  text = text.replace(/`([^`]*)`/g, '$1');

  // Строки таблиц и ASCII-рамок: markdown-таблица («| a | b |»), разделители,
  // строки из +-|= (схемы). Диктовать их бессмысленно
  text = text
    .split('\n')
    .filter((line) => {
      const t = line.trim();
      if (!t) return true;
      if (t.startsWith('|')) return false;
      if (/^[+\-=|_\s]{4,}$/.test(t)) return false;
      return true;
    })
    .join('\n');

  // Ссылки: [текст](url) → текст; голый url → «ссылка»
  text = text.replace(/!\[[^\]]*\]\([^)]*\)/g, ' ');
  text = text.replace(/\[([^\]]*)\]\([^)]*\)/g, '$1');
  text = text.replace(/https?:\/\/\S+/g, 'ссылка');

  // Разметка: заголовки, маркеры списков, цитаты, выделения
  text = text.replace(/^#{1,6}\s*/gm, '');
  text = text.replace(/^\s{0,3}[-*+]\s+/gm, '');
  // Нумерация списка («1. », «2) ») — отдельный кусок «1.» после нарезки на предложения:
  // лишний запрос к синтезу и произнесённое «один» посреди фразы
  text = text.replace(/^\s{0,3}\d+[.)]\s+/gm, '');
  text = text.replace(/^\s{0,3}>\s?/gm, '');
  text = text.replace(/\*\*([^*]+)\*\*/g, '$1');
  text = text.replace(/\*([^*]+)\*/g, '$1');
  text = text.replace(/__([^_]+)__/g, '$1');
  text = text.replace(/~~([^~]+)~~/g, '$1');

  // Эмодзи и прочие пиктограммы: синтезатор читает их названиями или спотыкается
  text = text.replace(/[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}\u{2190}-\u{21FF}]/gu, ' ');

  // Латиница-каша из имён файлов и идентификаторов — по словам: расшифровка после
  // всех markdown-правил, чтобы backtick-код успел развернуться в содержимое
  text = verbalizeIdentifiers(text);

  // Схлопываем пробелы и пустые строки
  text = text.replace(/[ \t]+/g, ' ').replace(/\n{2,}/g, '\n').trim();

  if (text.length > MAX_SPEAK_CHARS) {
    // Режем по границе предложения, чтобы фраза не обрывалась на полуслове
    const cut = text.slice(0, MAX_SPEAK_CHARS);
    const lastStop = Math.max(cut.lastIndexOf('.'), cut.lastIndexOf('!'), cut.lastIndexOf('?'));
    text = (lastStop > MAX_SPEAK_CHARS / 2 ? cut.slice(0, lastStop + 1) : cut.trim()) + TRUNCATION_NOTICE;
  }
  return text;
}

// --- Нарезка на предложения ---

// Сокращения, после которых точка не заканчивает предложение
const ABBREVIATIONS = ['т.е', 'т.к', 'т.д', 'т.п', 'др', 'см', 'рис', 'стр', 'г', 'гг', 'руб', 'проф', 'акад'];

// Длина (в символах, ПОСЛЕ курсора) первого законченного предложения, или -1, если
// его нет. Правила — общие для полной нарезки (splitSentences) и поточной резки
// дельт (takeSpeakableChunk): одно место, одни сокращения.
function sentenceBoundary(text: string): number {
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch !== '.' && ch !== '!' && ch !== '?' && ch !== '…' && ch !== '\n') continue;

    // Хвостовая пачка знаков («?!», «...») не рвётся
    while (i + 1 < text.length && '.!?…'.includes(text[i + 1])) i++;

    if (ch === '.') {
      // Сокращение перед точкой — предложение не закончилось. Одиночная буква тоже:
      // в «т.е.» перед ПЕРВОЙ точкой стоит просто «т», и по списку его не поймать
      const before = text.slice(0, i).trimEnd();
      const lastWord = before.split(/[\s(]/).pop()?.toLowerCase() ?? '';
      if (ABBREVIATIONS.includes(lastWord) || /^\p{L}$/u.test(lastWord)) continue;
      // Точка внутри числа («3.14») тоже не конец
      if (i + 1 < text.length && /\d/.test(text[i + 1]) && /\d/.test(text[i - 1] ?? '')) continue;
    }

    return i + 1;
  }
  return -1;
}

export function splitSentences(text: string): string[] {
  const clean = (text ?? '').trim();
  if (!clean) return [];

  const out: string[] = [];
  let start = 0;
  for (;;) {
    const end = sentenceBoundary(clean.slice(start));
    if (end < 0) break;
    const piece = clean.slice(start, start + end).trim();
    if (piece) out.push(piece);
    start += end;
  }
  const tail = clean.slice(start).trim();
  if (tail) out.push(tail);
  return out;
}

// --- Упаковка предложений в пакеты под лимит одного запроса синтеза ---

// Лимит запроса SpeechKit v3 — 249 символов (250 уже 400 «Too long text»).
export const PACK_LIMIT = 249;

// Хвост короче этого приклеиваем к предыдущему пакету: запрос тарифицируется целиком,
// и огрызок в пять символов стоит столько же, сколько полный пакет
const MIN_TAIL = 40;

// Предложения → пакеты. Синтез v3 берёт деньги ЗА ЗАПРОС, а не за символы (точка
// безубыточности против v1 — 121 символ, разбор в docs/research/speechkit-pricing.md §4),
// поэтому слать каждое предложение отдельно — значит платить втрое. Но первое предложение
// уходит В ОДИНОЧКУ: звук обязан пойти сразу, а не после того, как наберётся полный пакет.
// Чистая функция без состояния — под юнит-тестом.
export function packSentences(sentences: string[], limit = PACK_LIMIT): string[] {
  const packs: string[] = [];
  let buf = '';
  const flush = () => { if (buf) { packs.push(buf); buf = ''; } };

  for (const raw of sentences) {
    const s = raw.trim();
    if (!s) continue;
    if (packs.length === 0 && !buf) { packs.push(s); continue; } // разгонный пакет
    if (buf && buf.length + 1 + s.length > limit) flush();
    buf = buf ? `${buf} ${s}` : s;
    if (buf.length >= limit) flush();
  }
  flush();

  // Огрызок в конце приклеиваем к предыдущему — но только когда пакетов уже больше двух:
  // иначе склейка удлинит разгонный пакет и отложит первый звук
  if (packs.length >= 3) {
    const last = packs[packs.length - 1];
    const prev = packs[packs.length - 2];
    if (last.length < MIN_TAIL && prev.length + 1 + last.length <= limit) {
      packs.splice(packs.length - 2, 2, `${prev} ${last}`);
    }
  }
  return packs;
}

// --- Поточная резка нарастающего текста хода (режим разговора) ---

// Незакрытое предложение длиннее лимита режем принудительно: модель пишет без точек —
// иначе до result висела бы тишина на весь кусок
export const MAX_STREAM_CHUNK = 400;

export interface SpeakableChunk {
  chunk: string | null; // готовый к озвучке кусок; null — ждать следующих дельт
  cursor: number;       // новый курсор (за отданным куском); не двигается при null
  hitMarkup: boolean;   // впереди код-блок/таблица — стриминг на этом ходу выключается
}

// Код-блок (``` / ~~~) или строка таблицы (| в начале строки): вслух не читается,
// остаток хода закрывает обычный путь на result (санитайзер вырежет разметку)
const CODE_BLOCK_AHEAD = /(^|\n)\s*(?:```|~~~)/;
const TABLE_ROW_AHEAD = /(^|\n)\s*\|/;

// Взять следующий озвучиваемый кусок из нарастающего текста хода. Курсор абсолютный,
// вызов повторяется на каждой text_delta: текст хода — конкатенация элементов, и
// резка продолжается сама, в том числе после tool_use (новый text-элемент).
export function takeSpeakableChunk(text: string, cursor: number): SpeakableChunk {
  const rest = text.slice(cursor);
  if (!rest.trim()) return { chunk: null, cursor, hitMarkup: false };

  if (CODE_BLOCK_AHEAD.test(rest) || TABLE_ROW_AHEAD.test(rest))
    return { chunk: null, cursor, hitMarkup: true };

  const end = sentenceBoundary(rest);
  if (end > 0) return { chunk: rest.slice(0, end).trim(), cursor: cursor + end, hitMarkup: false };

  // Предложения нет, но кусок разросся: рез по последнему пробелу до лимита (не
  // посреди слова); пробела нет вовсе — по лимиту насильно: обрыв фразы лучше
  // полминуты тишины
  if (rest.length > MAX_STREAM_CHUNK) {
    const window = rest.slice(0, MAX_STREAM_CHUNK);
    const cut = window.lastIndexOf(' ');
    const at = cut > 0 ? cut : MAX_STREAM_CHUNK;
    const chunk = rest.slice(0, at).trim();
    if (chunk) return { chunk, cursor: cursor + at, hitMarkup: false };
  }

  // Хвост без терминальной пунктуации не отдаём из дельт — его закроет result
  return { chunk: null, cursor, hitMarkup: false };
}

// --- Проигрывание ---

let currentAudio: HTMLAudioElement | null = null;
let currentUrl: string | null = null;
// Токен текущего сеанса озвучки: старая очередь, дожившая до нового вызова, себя прекращает.
// Проверяется перед КАЖДЫМ куском и в speak()/startStreamSpeak() — это общий выключатель
// всех очередей (в т.ч. поточной): чужой токен = молча закончиться
let speakToken = 0;
// Ожидающие конца озвучки: stopSpeaking() резолвит их НЕМЕДЛЕННО, не дожидаясь висящего
// запроса синтеза (там таймаут 45 с). Ждущий петли разговора иначе стоял бы всё это время.
// AbortController в request не прокидываем сознательно: offline.ts перезатирает внешний
// signal своим, а обход увёл бы приложение в degraded/offline на каждом stopSpeaking().
// Висящий запрос просто отбрасывается по токену — это дёшево и безопасно.
let speechWaiters: (() => void)[] = [];

function releaseWaiters() {
  const list = speechWaiters;
  speechWaiters = [];
  for (const done of list) done();
}

// Играет ли что-то прямо сейчас — вторая линия защиты от эха в режиме разговора
// (микрофон и озвучка не должны пересекаться никогда)
export function isSpeaking(): boolean {
  if (currentAudio && !currentAudio.paused) return true;
  if (typeof speechSynthesis !== 'undefined' && (speechSynthesis.speaking || speechSynthesis.pending)) return true;
  return false;
}
// Громкость озвучки: 1 — обычная, меньше — приглушение под барж-ин (первая ступень
// перебивания, см. lib/bargeDetect). Приглушение обратимо и потому дёшево: чужая реплика
// рядом стоит полусекундной тишины, а не потерянного ответа.
let speechVolume = 1;

// Ставится на играющий кусок И на все следующие в очереди: между кусками элемент
// пересоздаётся, и без общего значения приглушение слетало бы на первой же точке
export function setSpeechVolume(v: number): void {
  speechVolume = Math.min(1, Math.max(0, v));
  if (currentAudio) currentAudio.volume = speechVolume;
  // У speechSynthesis громкость на лету не меняется (она свойство реплики, а не движка) —
  // приглушаем паузой: для фолбэка это тот же смысл «замолчи, но не насовсем»
  if (typeof speechSynthesis === 'undefined') return;
  try {
    if (speechVolume < 1) speechSynthesis.pause();
    else speechSynthesis.resume();
  } catch { /* движок не поддержал — озвучка просто доиграет как есть */ }
}

// Показать разовое сообщение о фолбэке (Р4): «на прогулке замолчало» не должно выглядеть багом
let toastFn: ((text: string) => void) | null = null;

// Родитель (ChatPanel) отдаёт свой showToast — модулю незачем знать про UI
export function setSpeechToast(fn: (text: string) => void) {
  toastFn = fn;
}

// «Разогрев» аудио из пользовательского жеста: политика autoplay в мобильных браузерах
// разрешает воспроизведение только после явного действия. Без этого первое воспроизведение
// молча не стартует.
export function primeAudio() {
  if (typeof Audio === 'undefined') return;
  try {
    const a = new Audio();
    a.muted = true;
    // Крошечный тихий wav: 44 байта заголовка без сэмплов
    a.src = 'data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=';
    void a.play().catch(() => { /* браузер не дал — попробуем на следующем жесте */ });
  } catch { /* Audio недоступен — озвучки не будет, это не повод падать */ }
}

export function stopSpeaking() {
  speakToken++;
  // Приглушение живёт ровно в пределах одной озвучки: следующая начинается в полный голос
  speechVolume = 1;
  if (currentAudio) {
    currentAudio.pause();
    currentAudio.src = '';
    currentAudio = null;
  }
  if (currentUrl) {
    URL.revokeObjectURL(currentUrl);
    currentUrl = null;
  }
  if (typeof speechSynthesis !== 'undefined') speechSynthesis.cancel();
  releaseWaiters();
}

// Озвучить текст ответа. Санитайзер + нарезка + очередь; при отказе сервера — голос браузера.
// Промис резолвится по опустошении очереди (и никогда не реджектится): режим разговора
// открывает микрофон именно по нему, поэтому «резолв раньше конца звука» = эхо.
export function speak(rawText: string): Promise<void> {
  watchConnection();
  stopSpeaking();
  const token = ++speakToken;
  return new Promise<void>((resolve) => {
    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      speechWaiters = speechWaiters.filter(w => w !== finish);
      resolve();
    };
    speechWaiters.push(finish);
    void runSpeak(rawText, token).then(finish, finish);
  });
}

async function runSpeak(rawText: string, token: number): Promise<void> {
  const text = sanitizeForSpeech(rawText);
  if (!text) return;
  const parts = packSentences(splitSentences(text));
  if (parts.length === 0) return;

  // Сервер уже сказал «синтеза нет» — не долбим его на каждое предложение
  if (serverTtsUnavailable) {
    await speakWithBrowser(text, token);
    return;
  }

  // Очередь: следующий кусок синтезируется, пока играет текущий
  let next: Promise<Blob | null> | null = synthesize(parts[0]);
  for (let i = 0; i < parts.length; i++) {
    const blobPromise = next;
    next = i + 1 < parts.length ? synthesize(parts[i + 1]) : null;

    let blob: Blob | null;
    try {
      blob = await blobPromise;
    } catch (e) {
      // Классы отказа разведены: постоянный (нет ключа) от временного (Яндекс отказал)
      const status = (e as { status?: number }).status;
      const reason = (e as { body?: { reason?: string } }).body?.reason;
      if (status === undefined) return; // OfflineError/RequestTimeoutError — связи нет, молчим
      if (status === 503 && reason === 'not_configured') {
        serverTtsUnavailable = true;
        notifyFallbackOnce();
      }
      // «Хвост» очереди уже улетел на сервер — гасим его reject, иначе получим
      // необработанное отклонение промиса
      void next?.catch(() => null);
      // и постоянный, и временный отказ: дочитываем остаток голосом браузера.
      // Именно с await: без него промис озвучки резолвился бы, пока синтезатор
      // ещё говорит — прямой канал эха на самом частом пути отказа
      if (token === speakToken) await speakWithBrowser(parts.slice(i).join(' '), token);
      return;
    }

    // Озвучку прервали (новый ход, смена чата). Хвост очереди уже улетел на сервер —
    // гасим его reject, иначе упавший запрос даст необработанное отклонение промиса
    if (token !== speakToken) { void next?.catch(() => null); return; }
    if (!blob) continue;
    await playBlob(blob, token);
    if (token !== speakToken) { void next?.catch(() => null); return; }
  }
}

function synthesize(text: string): Promise<Blob | null> {
  return request('/tts', {
    method: 'POST',
    body: JSON.stringify({ text }),
    parse: 'blob',
    // Синтез длинной фразы бывает дольше дефолтных 30 с только в патологии,
    // но обрыв не должен трактоваться как «связи нет»
    timeoutMs: 45_000,
  });
}

function playBlob(blob: Blob, token: number): Promise<void> {
  return new Promise((resolve) => {
    if (typeof Audio === 'undefined') { resolve(); return; }
    const url = URL.createObjectURL(blob);
    const audio = new Audio(url);
    audio.volume = speechVolume; // приглушены барж-ином — следующий кусок тоже тихий
    currentAudio = audio;
    currentUrl = url;

    const done = () => {
      if (currentUrl === url) {
        URL.revokeObjectURL(url);
        currentUrl = null;
      }
      if (currentAudio === audio) currentAudio = null;
      resolve();
    };
    audio.onended = done;
    audio.onerror = done;
    // Метка круга разговора: звук пошёл на самом деле, а не «фаза озвучки выставлена»
    audio.onplaying = () => talkMark('first-audio');
    void audio.play().catch(() => {
      // Autoplay не пустил — дальше нет смысла играть очередь
      if (token === speakToken) speakToken++;
      done();
    });
  });
}

// --- Поточная озвучка хода (режим разговора) ---

// Стрим поверх той же механики, что runSpeak: куски едут по одному, следующий
// синтезируется, пока играет текущий. Отличие от speak() — очередь ОТКРЫТАЯ: куски
// приезжают по мере появления предложений в ленте, конец хода закрывает end().
//
// Токен — общий глобальный speakToken: внешний stopSpeaking() (смена чата, выход из
// разговора, новый speak()) гасит стрим так же, как обычную очередь. Локальный токен
// отрезал бы стрим от этих выключателей.
export interface StreamSpeech {
  enqueue(text: string): void; // кусок (уже предложение) — в очередь
  end(): void;                 // кусков больше не будет
  done: Promise<void>;         // резолв по концу ПОСЛЕДНЕГО куска (onended), не по постановке
  stop(): void;                // немедленно: очередь чистится, done резолвится
}

// onDone — единственный канал «стрим закончился» для владельца фазы (ChatPanel):
// вызывается ровно один раз из finishDone при любом исходе (очередь доиграла,
// stop(), внешний stopSpeaking(), потеря токена). Колбэк-канал вместо нескольких
// done.then в вызывающем коде закрывает гонку «кто первый занулил ref — тот и
// снял фазу»: снятие фазы всегда в одном месте
export function startStreamSpeak(onDone?: () => void): StreamSpeech {
  watchConnection();
  const token = ++speakToken;
  const queue: string[] = [];
  // Копилка предложений перед отправкой: синтез берёт деньги за ЗАПРОС, и слать каждое
  // предложение отдельно втрое дороже (см. packSentences). Копим, только пока в очереди
  // есть чем занять уши, — иначе пакуем в ущерб паузе, ради которой стриминг и писался
  let buffer = '';
  let ended = false;
  let stopped = false;
  let failedToServer = false;
  let resolveDone: () => void;
  const done = new Promise<void>(r => { resolveDone = r; });
  let settledDone = false;
  const finishDone = () => {
    if (settledDone) return;
    settledDone = true;
    resolveDone();
    onDone?.();
  };
  // done резолвится и по стопу/смене владельца звука: ждущий (петля) не должен
  // висеть на убитом стриме. Сам токен проверяется в цикле ниже
  speechWaiters.push(finishDone);

  // Единственный потребитель очереди: enqueue/end лишь подкладывают куски и будят
  // цикл. Без флага каждый вызов порождал ПАРАЛЛЕЛЬНЫЙ цикл — куски расхватывались
  // вперегонки (два аудио сразу), а цикл от end() видел пустую очередь и закрывал
  // done до конца воспроизведения.
  // Prefetch — как в runSpeak: следующий кусок синтезируется, пока играет текущий,
  // иначе между фразами висела бы пауза на круг до сервера (вся суть стриминга).
  // Пока текущий кусок ИГРАЕТ, цикл стоит на await playBlob — prefetch будится
  // отдельным звоном из enqueue.
  let pumping = false;
  let prefetch: Promise<Blob | null> | null = null;
  let prefetchText = '';
  let prefetchFailed = false; // запрос упал — кусок обязан доозвучиться голосом браузера
  const startPrefetch = () => {
    if (prefetch || serverTtsUnavailable || failedToServer) return;
    const next = queue.find(t => t.trim());
    if (next === undefined) return;
    prefetchText = next;
    prefetchFailed = false;
    prefetch = synthesize(next)
      .catch(e => {
        const status = (e as { status?: number }).status;
        const reason = (e as { body?: { reason?: string } }).body?.reason;
        prefetchFailed = true;
        if (status === 503 && reason === 'not_configured') {
          serverTtsUnavailable = true;
          notifyFallbackOnce();
        } else if (status !== undefined) {
          failedToServer = true; // временный отказ сервера — дальше голосом браузера
        }
        return null;
      });
  };
  const dropPrefetch = () => {
    if (!prefetch) return;
    void prefetch.catch(() => null);
    prefetch = null;
    prefetchText = '';
  };

  // Копилка → очередь. Дальше всё как раньше: prefetch подхватит пакет, пока играет текущий
  const flushBuffer = () => {
    if (!buffer) return;
    queue.push(buffer);
    buffer = '';
    startPrefetch();
    void pump();
  };

  const pump = async () => {
    if (pumping) return;
    pumping = true;
    try {
      for (;;) {
        if (token !== speakToken) { finishDone(); return; }
        const text = queue.shift();
        if (text === undefined) {
          // В очереди пусто, но в копилке лежит недособранный пакет — доигрываем его,
          // а не ждём следующей дельты: пауза здесь слышна, экономия неощутима
          if (buffer) { flushBuffer(); continue; }
          if (ended) { finishDone(); return; }
          return; // ждём следующих кусков; pump перезапустится из enqueue/end
        }
        if (!text.trim()) continue;

        if (serverTtsUnavailable || failedToServer) {
          await speakWithBrowser(text, token);
          if (token !== speakToken) { finishDone(); return; }
          continue;
        }

        let blob: Blob | null;
        let fromPrefetch = false;
        if (prefetch && prefetchText === text) {
          blob = await prefetch;
          fromPrefetch = true;
          prefetch = null;
          prefetchText = '';
        } else {
          dropPrefetch();
          try {
            blob = await synthesize(text);
          } catch (e) {
            const status = (e as { status?: number }).status;
            const reason = (e as { body?: { reason?: string } }).body?.reason;
            if (status === undefined) { finishDone(); return; } // связи нет — молчим и закрываем
            if (status === 503 && reason === 'not_configured') {
              serverTtsUnavailable = true;
              notifyFallbackOnce();
            }
            failedToServer = true;
            if (token === speakToken) { await speakWithBrowser(text, token); }
            if (token !== speakToken) { finishDone(); return; }
            continue;
          }
        }

        if (token !== speakToken) { finishDone(); return; }
        // Prefetch упал (503/upstream): сам blob=null, но кусок ещё не звучал —
        // доозвучиваем его голосом браузера, как это делает runSpeak
        if (!blob && fromPrefetch && prefetchFailed && token === speakToken) {
          await speakWithBrowser(text, token);
          if (token !== speakToken) { finishDone(); return; }
          continue;
        }
        if (!blob) continue; // пустой ответ синтеза — к следующему куску
        startPrefetch(); // пока играет этот кусок, следующий уже синтезируется
        await playBlob(blob, token);
        if (token !== speakToken) { finishDone(); return; }
      }
    } finally {
      pumping = false;
    }
  };

  const stream: StreamSpeech = {
    enqueue(text) {
      if (ended || stopped) return; // поздние дельты после end() не читаются
      const piece = text.trim();
      if (!piece) return;
      if (buffer && buffer.length + 1 + piece.length > PACK_LIMIT) flushBuffer();
      buffer = buffer ? `${buffer} ${piece}` : piece;
      // Отдаём накопленное, когда пакет полон ЛИБО когда очередь пуста. Пустая очередь —
      // это либо разгон (ещё ничего не звучало), либо текущий кусок вот-вот кончится:
      // придержать буфер здесь значит получить ровно ту паузу, от которой уходили
      if (buffer.length >= PACK_LIMIT || queue.length === 0) flushBuffer();
    },
    end() {
      // Флаш ДО ended: иначе цикл увидит пустую очередь при ended и закроет done,
      // недоговорив последний пакет
      flushBuffer();
      ended = true;
      void pump();
    },
    stop() {
      if (stopped) return;
      stopped = true;
      queue.length = 0;
      buffer = '';
      finishDone();
      // Оборвать играющий кусок и осиротить стрим: чистящий цикл увидит чужой токен
      stopSpeaking();
    },
    done,
  };
  return stream;
}

// --- Фолбэк: голос браузера ---

// Промис резолвится по onend/onerror последней фразы — то есть когда синтезатор реально
// замолчал. speechSynthesis.cancel() из stopSpeaking() тоже приводит сюда (браузер эмитит
// end/error у прерванной реплики), плюс висящего ждущего снимает releaseWaiters().
function speakWithBrowser(text: string, token: number): Promise<void> {
  return new Promise<void>((resolve) => {
    if (typeof speechSynthesis === 'undefined' || !text) { resolve(); return; }
    const utter = new SpeechSynthesisUtterance(text);
    utter.lang = 'ru-RU';
    let done = false;
    const finish = () => { if (done) return; done = true; resolve(); };
    utter.onend = finish;
    utter.onerror = finish;
    utter.onstart = () => talkMark('first-audio'); // тот же замер, что у серверного пути

    // Список голосов приезжает асинхронно, и к этому моменту озвучку могли прервать —
    // без сверки токена браузер зачитал бы ответ покинутого чата
    const say = () => {
      if (token !== speakToken) { finish(); return; }
      const v = pickRuVoice();
      if (v) utter.voice = v;
      speechSynthesis.speak(utter);
    };

    // Голосов может не быть вовсе (десктопный Linux) — тогда читаем дефолтным
    if (pickRuVoice() || speechSynthesis.getVoices().length > 0) { say(); return; }
    // Ждём voiceschanged, но не дольше секунды: без крайнего срока промис (а с ним и
    // петля разговора) висел бы вечно на движке, который событие не эмитит
    let timer: ReturnType<typeof setTimeout> | null = null;
    const onVoices = () => {
      speechSynthesis.removeEventListener('voiceschanged', onVoices);
      if (timer !== null) clearTimeout(timer);
      say();
    };
    speechSynthesis.addEventListener('voiceschanged', onVoices);
    timer = setTimeout(onVoices, 1000);
  });
}

function pickRuVoice(): SpeechSynthesisVoice | null {
  if (typeof speechSynthesis === 'undefined') return null;
  return speechSynthesis.getVoices().find((v) => v.lang?.toLowerCase().startsWith('ru')) ?? null;
}

function notifyFallbackOnce() {
  if (fallbackNoticeShown) return;
  fallbackNoticeShown = true;
  toastFn?.('Синтез речи не настроен — читаю голосом браузера. При погашенном экране он замолкает.');
}
