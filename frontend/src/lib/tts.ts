// Озвучка ответов в голосовом режиме чата.
//
// Основной путь — mp3 с бэкенда (POST /api/tts, Yandex SpeechKit) через HTMLAudioElement:
// он продолжает играть при погашенном экране телефона, а speechSynthesis глохнет — для
// сценария «разговор на прогулке» это решающее. speechSynthesis остаётся фолбэком.
//
// Текст режется на предложения и синтезируется очередью: следующий кусок уходит на синтез,
// пока играет текущий, — иначе между фразами повисали бы паузы на круг до сервера.

import { request, subscribeConnectionState, getConnectionState } from './offline';

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
  subscribeConnectionState(() => {
    if (getConnectionState() === 'online') serverTtsUnavailable = false;
  });
}

// --- Санитайзер: убираем из markdown всё, что нельзя прочитать вслух ---

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

export function splitSentences(text: string): string[] {
  const clean = (text ?? '').trim();
  if (!clean) return [];

  const out: string[] = [];
  let start = 0;
  for (let i = 0; i < clean.length; i++) {
    const ch = clean[i];
    if (ch !== '.' && ch !== '!' && ch !== '?' && ch !== '…' && ch !== '\n') continue;

    // Хвостовая пачка знаков («?!», «...») не рвётся
    while (i + 1 < clean.length && '.!?…'.includes(clean[i + 1])) i++;

    if (ch === '.') {
      // Сокращение перед точкой — предложение не закончилось. Одиночная буква тоже:
      // в «т.е.» перед ПЕРВОЙ точкой стоит просто «т», и по списку его не поймать
      const before = clean.slice(start, i).trimEnd();
      const lastWord = before.split(/[\s(]/).pop()?.toLowerCase() ?? '';
      if (ABBREVIATIONS.includes(lastWord) || /^\p{L}$/u.test(lastWord)) continue;
      // Точка внутри числа («3.14») тоже не конец
      if (i + 1 < clean.length && /\d/.test(clean[i + 1]) && /\d/.test(clean[i - 1] ?? '')) continue;
    }

    const piece = clean.slice(start, i + 1).trim();
    if (piece) out.push(piece);
    start = i + 1;
  }
  const tail = clean.slice(start).trim();
  if (tail) out.push(tail);
  return out;
}

// --- Проигрывание ---

let currentAudio: HTMLAudioElement | null = null;
let currentUrl: string | null = null;
// Токен текущего сеанса озвучки: старая очередь, дожившая до нового вызова, себя прекращает
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
  const parts = splitSentences(text);
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
    void audio.play().catch(() => {
      // Autoplay не пустил — дальше нет смысла играть очередь
      if (token === speakToken) speakToken++;
      done();
    });
  });
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
