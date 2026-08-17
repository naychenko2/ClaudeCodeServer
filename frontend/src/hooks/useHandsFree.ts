// Режим разговора (hands-free): сказал → пауза со звуковым сигналом и окном отмены →
// отправка → ход → озвучка ответа → снова слушаю.
//
// Автомат вынесен ЧИСТЫМ редьюсером (handsFreeReducer): компонентных тестов в репе нет,
// и это единственный способ покрыть петлю юнитами. Обёртка useHandsFree держит только
// эффекты — таймеры, микрофон, wake lock, сигнал и реплики самого автомата.
//
// Главный дефект этой фичи — ЭХО: микрофон, открытый под играющую озвучку, слышит
// собственный голос и гонит петлю по кругу. Поэтому микрофон открыт ровно в двух фазах
// (listening/pending), а выход из ожидания идёт только через фазу озвучки или страховку.

import { useCallback, useEffect, useReducer, useRef } from 'react';
import { speak, stopSpeaking, isSpeaking } from '../lib/tts';
import { beep, primeBeep, closeBeep, startThinking, stopThinking, startListening, stopListening, NEED_ANSWER_DURATION_MS } from '../lib/beep';
import { requestWakeLock, releaseWakeLock } from '../lib/wakeLock';
import { showToast } from '../lib/toast';
import { describeSpeechError } from '../lib/voiceInput';

export type HandsFreePhase = 'off' | 'listening' | 'pending' | 'sending' | 'waiting' | 'speaking';

// Реплики автомата о себе. Произносятся тем же speak() и ТОЛЬКО там, где озвучки ответа
// заведомо нет (Р18): иначе speak() внутри себя зовёт stopSpeaking() и обрежет ответ
export type HandsFreeNotice = 'stillThere' | 'needDecision' | 'idleOff' | 'micDead' | 'voiceOff';

export interface HandsFreeState {
  phase: HandsFreePhase;
  // Распознанное в петле копится ЗДЕСЬ, а не в поле композера: иначе появление текста
  // превращает кнопку режима в «Отправить» и мусорит в черновике
  buffer: string;
  // Подряд идущие бесплодные циклы распознавания — и тишина, и ошибки (Р9)
  barren: number;
  warned: boolean;
  // Растёт на каждый вход в фазу: обёртке это сигнал перевзвести таймеры даже тогда,
  // когда фаза формально не изменилась (listening → pending → listening)
  seq: number;
  // Реплика, которую обёртка обязана произнести и погасить событием noticeSaid
  notice: HandsFreeNotice | null;
  // Говорит сама петля («Ты ещё здесь?»), а не ответ модели: по концу реплики
  // возвращаемся слушать, НЕ обнуляя счётчик бесплодных циклов
  noticeSpeech: boolean;
  // Вопрос модели пришёл посреди озвучки: выходим не сразу, а дочитав ответ
  pendingExit: boolean;
}

export type HandsFreeEvent =
  | { type: 'toggle' }
  | { type: 'recognized'; text: string }
  | { type: 'cycleEnded' }
  | { type: 'cycleError'; code: string }
  | { type: 'pendingElapsed' }
  | { type: 'turnStarted' }
  | { type: 'speechWillStart' }
  | { type: 'speechFinished' }
  // Ход кончился, а озвучка так и не заявилась — страховка Р13 (1.5 с)
  | { type: 'speechSkipped' }
  | { type: 'needsDecision' }
  // Отправка не состоялась (взведённая механика, пустой ход) — ждать хода нечего
  | { type: 'sendFailed' }
  | { type: 'offline' }
  | { type: 'idleTimeout' }
  | { type: 'micDead' }
  | { type: 'noticeSaid' };

export const HANDS_FREE_INITIAL: HandsFreeState = {
  phase: 'off', buffer: '', barren: 0, warned: false, seq: 0,
  notice: null, noticeSpeech: false, pendingExit: false,
};

// 3 бесплодных цикла подряд → «ещё здесь?», ещё 2 → выключаемся (Р9)
export const BARREN_WARN = 3;
export const BARREN_OFF = 5;

function stop(s: HandsFreeState, notice: HandsFreeNotice | null = null): HandsFreeState {
  return { ...HANDS_FREE_INITIAL, seq: s.seq + 1, notice };
}

function enter(s: HandsFreeState, phase: HandsFreePhase, patch: Partial<HandsFreeState> = {}): HandsFreeState {
  return { ...s, phase, seq: s.seq + 1, ...patch };
}

function append(buffer: string, chunk: string): string {
  const t = chunk.trim();
  if (!t) return buffer;
  return buffer ? `${buffer} ${t}` : t;
}

// Голосовая команда выхода из разговора. Сравнение ТОЧНОЕ по всему чанку (после
// нормализации регистра и пунктуации): «стоп, а теперь расскажи…» — обычная речь,
// не команда, и улетать в чат обязана. Синонимы покрывают очевидные «замолчать и
// выйти»: короткие слова работают и при шуме, составные — реже, зато без ложных
// срабатываний на «хватит» в середине мысли
const STOP_COMMANDS = new Set([
  'стоп', 'хватит', 'отбой', 'конец связи', 'выключись',
  'выключи разговор', 'достаточно', 'прекрати', 'хватит говорить',
]);

// Нормализация: нижний регистр, срез пунктуации вокруг и внутри (движки распознавания
// любят «Стоп.» и «стоп,»), схлопывание пробелов
function normalizeSpeech(text: string): string {
  return text.toLowerCase().replace(/[.,!?;:«»"'—–-]+/g, ' ').replace(/\s+/g, ' ').trim();
}

// Публично для тестов: командой считается точное совпадение всего чанка
export function isStopCommand(text: string): boolean {
  return STOP_COMMANDS.has(normalizeSpeech(text));
}

// Бесплодный цикл: движок отработал вхолостую. Считается ровно один раз за цикл —
// по cycleEnded, который приходит всегда (в том числе следом за onerror)
function barrenTick(s: HandsFreeState): HandsFreeState {
  const barren = s.barren + 1;
  if (barren >= BARREN_OFF) return stop(s, 'idleOff');
  // Предупреждение — это РЕЧЬ петли, а значит фаза speaking: иначе микрофон остаётся
  // открытым и синтез слышит сам себя (эхо). Счётчик при этом сохраняется, иначе
  // автовыключение по бесплодности не наступит никогда
  if (barren >= BARREN_WARN && !s.warned)
    return enter(s, 'speaking', { barren, warned: true, notice: 'stillThere', noticeSpeech: true });
  return { ...s, barren };
}

export function handsFreeReducer(s: HandsFreeState, e: HandsFreeEvent): HandsFreeState {
  const active = s.phase !== 'off';

  switch (e.type) {
    case 'toggle':
      return active ? stop(s) : enter(HANDS_FREE_INITIAL, 'listening', { seq: s.seq + 1 });

    case 'offline':
      // Офлайн выключает петлю СОВСЕМ, а не ставит на паузу: композер в офлайне
      // подменяется заглушкой, и выключить петлю было бы нечем
      return active ? stop(s) : s;

    case 'micDead':
      return active ? stop(s, 'micDead') : s;

    case 'idleTimeout':
      return active ? stop(s, 'idleOff') : s;

    case 'needsDecision':
      if (!active) return s;
      // Посреди озвучки не рвём ответ — выходим, когда он дочитан (Р18)
      if (s.phase === 'speaking') return { ...s, pendingExit: true };
      return stop(s, 'needDecision');

    case 'recognized':
      // Голосовая команда выхода: работает в микрофонных фазах (listening/pending),
      // где единственный способ сказать «хватит» без касаний. Точное совпадение —
      // см. isStopCommand; буфер при этом НЕ отправляется
      if ((s.phase === 'listening' || s.phase === 'pending') && isStopCommand(e.text))
        return stop(s, 'voiceOff');
      if (s.phase === 'listening')
        return enter(s, 'pending', { buffer: append(s.buffer, e.text), barren: 0, warned: false });
      // Речь продолжилась в окне отмены: буфер дописан, окно снято, слушаем дальше
      if (s.phase === 'pending')
        return enter(s, 'listening', { buffer: append(s.buffer, e.text), barren: 0, warned: false });
      return s;

    case 'cycleEnded':
      // Человек замолчал, а сказанное уже в буфере — взводим окно отправки
      if (s.phase === 'listening') return s.buffer ? enter(s, 'pending') : barrenTick(s);
      return s;

    case 'cycleError':
      if (s.phase !== 'listening') return s;
      if (e.code === 'aborted') return s; // прервали мы сами (смена фазы, выключение)
      // Счётчик здесь НЕ трогаем: движок на пустом цикле шлёт onerror('no-speech')
      // и следом onend — иначе один цикл прибавлял бы к нему двойку
      return s.buffer ? enter(s, 'pending') : s;

    case 'pendingElapsed':
      if (s.phase !== 'pending') return s;
      // Пустой буфер до отправки не доходит: молчание просто возвращает в слушание
      return s.buffer.trim() ? enter(s, 'sending') : enter(s, 'listening', { buffer: '' });

    case 'turnStarted':
      return active ? enter(s, 'waiting', { buffer: '' }) : s;

    case 'speechWillStart':
      // Из listening тоже: если страховка успела открыть микрофон, а озвучка всё же
      // началась — закрываем его немедленно, эхо дороже потерянной полусекунды
      if (s.phase === 'waiting' || s.phase === 'listening') return enter(s, 'speaking');
      return s;

    case 'speechFinished':
      if (s.phase !== 'speaking') return s;
      if (s.pendingExit) return stop(s, 'needDecision');
      // Своя реплика петли ответом модели не считается: счётчик бесплодных циклов
      // и отметка «уже предупредили» переживают её
      if (s.noticeSpeech) return enter(s, 'listening', { noticeSpeech: false });
      return enter(s, 'listening', { buffer: '', barren: 0, warned: false, pendingExit: false });

    case 'speechSkipped':
      return s.phase === 'waiting' ? enter(s, 'listening', { buffer: '' }) : s;

    case 'sendFailed':
      return s.phase === 'waiting' ? enter(s, 'listening', { buffer: '' }) : s;

    case 'noticeSaid':
      return s.notice ? { ...s, notice: null } : s;

    default:
      return s;
  }
}

// --- Обёртка с эффектами ---

export type SpeechPhase = 'idle' | 'willSpeak' | 'speaking';

export interface HandsFreeOptions {
  // Ход идёт (isWaiting родителя)
  isGenerating: boolean;
  // Модель ждёт решения человека (permission_request / ask_question) — выход из петли
  awaitingResponse: boolean;
  // Фаза озвучки ответа, ведёт ChatPanel (владелец speak)
  speechPhase: SpeechPhase;
  offline: boolean;
  // Микрофон живёт в useVoiceInput композера — петля им только управляет
  isListening: boolean;
  startMic: () => void;
  stopMic: (confirm: boolean) => void;
  // Отправка накопленного текста мимо поля ввода (handleSend(overrideText)).
  // false в ответе = ход не ушёл: петля вернётся слушать, не дожидаясь сторожа
  onSend: (text: string) => void | boolean | Promise<boolean | void>;
  // Прерывание убежавшего хода при выключении петли в фазе ожидания
  onStop: () => void;
  // Петлю погасила ГОЛОСОВАЯ команда выхода («стоп»): автомат уже выключен,
  // здесь композер доделывает хвост тапа по кнопке — страховочное прерывание хода
  // и PUT voiceMode=false (инвариант Р3: выход из петли гасит и режим). Звать
  // handsFree.stop() из него НЕЛЬЗЯ — toggle при выключенной петле её бы запустил
  onVoiceExit?: () => void;
  // Зеркало «петля активна» для синхронного чтения из колбэков движка распознавания
  activeRef: React.RefObject<boolean>;
}

export interface HandsFree {
  phase: HandsFreePhase;
  active: boolean;
  buffer: string;
  // Тап по кнопке режима: старт петли (синхронная часть жеста — на стороне композера)
  start: () => void;
  // Выключение: в фазе ожидания заодно прерывает ход
  stop: () => void;
  // Аварийный выход с сообщением (провал PUT voiceMode, мёртвый движок)
  abort: (message: string) => void;
  onRecognized: (text: string) => void;
  onCycleEnd: () => void;
  onCycleError: (code: string) => void;
}

// Окно отмены после распознанной фразы
const PENDING_MS = 2000;
// Ход кончился, а willSpeak не пришёл — значит озвучки не будет (Р13)
const SPEECH_WAIT_MS = 1500;
// Нет хода, нет звука, нет прогресса — выключаем разговор (Р13). Считаем именно
// бездействие: ход с инструментами идёт минутами, а озвучка 1500 символов — полторы
const IDLE_MS = 60_000;
// Перезапуск распознавания не чаще раза в полторы секунды: ошибочные циклы прилетают
// мгновенно, и без дебаунса получился бы горячий цикл start→error→start (Р9)
const RESTART_MS = 1500;
// Демпфер тостов об ошибках движка (Р17)
const ERROR_TOAST_MS = 30_000;
// Как часто перепроверять «звук ещё играет» перед открытием микрофона
const SPEECH_POLL_MS = 250;
// Ход завис (инструмент не отвечает, обрыв) — дальше держать экран телефона незачем
const WAITING_WAKE_MS = 5 * 60_000;

const NOTICE_TEXT: Record<HandsFreeNotice, string> = {
  stillThere: 'Ты ещё здесь?',
  needDecision: 'Нужно твоё решение, посмотри на экран.',
  idleOff: 'Выключаю разговор.',
  micDead: 'Распознавание недоступно, выключаю разговор.',
  voiceOff: 'Выключаю разговор.',
};

export function useHandsFree(opts: HandsFreeOptions): HandsFree {
  const [state, dispatch] = useReducer(handsFreeReducer, HANDS_FREE_INITIAL);
  const { phase, seq, notice, buffer } = state;
  const active = phase !== 'off';

  // Свежие значения для эффектов и стабильных колбэков
  const o = useRef(opts);
  const bufferRef = useRef(buffer);
  useEffect(() => {
    o.current = opts;
    bufferRef.current = buffer;
  });
  const lastStartRef = useRef(0);
  const lastErrorToastRef = useRef(0);
  // Компонент ещё жив. Ответ onSend приходит уже после смены фазы (эффект отправки
  // к этому моменту перезапущен), поэтому отменять колбэк можно только размонтированием
  const mountedRef = useRef(true);
  // Wake lock и аудиоконтекст сигнала — модули с ГЛОБАЛЬНЫМ состоянием на вкладку.
  // Гасит их только тот экземпляр, который их и поднял: иначе в раскладке «чаты на
  // стене» закрытие соседнего чата обесточило бы живую петлю в другом композере
  const ownsGlobalsRef = useRef(false);
  // Была ли петля активна на прошлом проходе эффекта микрофона: по переходу true→false
  // гасим микрофон один раз, дальше он принадлежит человеку
  const prevActiveRef = useRef(false);

  // Зеркало фазы: колбэки движка распознавания (onResult/onEnd) должны синхронно знать,
  // писать в буфер петли или в поле композера
  const { activeRef } = opts;
  useEffect(() => { activeRef.current = active; }, [active, activeRef]);

  // Экран не должен гаснуть, пока идёт разговор: вместе с ним встаёт распознавание
  useEffect(() => {
    if (!active) return;
    requestWakeLock();
    return () => releaseWakeLock();
  }, [active]);

  // Пока ход идёт, человек смотрит не на экран, а под ноги: тихая пульсация — его
  // единственный признак, что вопрос принят и ответ готовится. Молчащая пауза в полминуты
  // неотличима от «связь отвалилась». Гаснет сама при переходе в speaking: перебивать
  // собственную озвучку фоном незачем
  useEffect(() => {
    if (phase !== 'sending' && phase !== 'waiting') return;
    startThinking();
    return () => stopThinking();
  }, [phase]);

  // Застрявший ход: фаза ожидания живёт сколько угодно, и держать экран телефона все
  // эти минуты незачем. Петлю не выключаем — только отпускаем блокировку; возврат
  // в любую другую фазу берёт её заново
  useEffect(() => {
    if (!active) return;
    if (phase !== 'waiting') { requestWakeLock(); return; }
    const id = setTimeout(() => releaseWakeLock(), WAITING_WAKE_MS);
    return () => clearTimeout(id);
  }, [active, phase, seq]);

  // Микрофон открыт РОВНО в двух фазах. В остальных (отправка, ход, озвучка) он закрыт —
  // это и есть первая линия защиты от эха
  useEffect(() => {
    // Микрофон гасим, пока петля жива (эхо-защита) и один раз на выходе из неё. Когда
    // петля давно выключена, микрофон не наш: раньше условие смотрело только на фазу,
    // и выключенная петля душила обычную диктовку — человек жал микрофон, эффект видел
    // «фаза не listening, а микрофон открыт» и тут же его закрывал
    const wasActive = prevActiveRef.current;
    prevActiveRef.current = active;
    const micWanted = active && (phase === 'listening' || phase === 'pending');
    if (!micWanted) {
      if ((active || wasActive) && o.current.isListening) o.current.stopMic(false);
      return;
    }
    if (o.current.isListening) return;
    let id: ReturnType<typeof setTimeout>;
    const arm = (wait: number) => {
      id = setTimeout(() => {
        // Вторая линия защиты от эха: звук ещё идёт (хвост очереди озвучки, реплика
        // петли) — микрофон под него не открываем, пробуем позже
        if (isSpeaking()) { arm(SPEECH_POLL_MS); return; }
        lastStartRef.current = Date.now();
        o.current.startMic();
      }, wait);
    };
    arm(Math.max(0, RESTART_MS - (Date.now() - lastStartRef.current)));
    return () => clearTimeout(id);
  }, [phase, seq, opts.isListening]);

  // «Слушаю»: звук только там, где человек ждёт очереди говорить — на старте разговора и
  // после ответа. При продолжении речи (pending → listening) и на рестартах цикла молчим:
  // человек и так говорит, а сигнал каждые несколько секунд превратился бы в тиканье.
  // Успевает отзвучать до открытия микрофона: тот стартует с дебаунсом (RESTART_MS),
  // а сигнал длится 120 мс
  const prevPhaseRef = useRef<HandsFreePhase>('off');
  useEffect(() => {
    const prev = prevPhaseRef.current;
    prevPhaseRef.current = phase;
    if (phase !== 'listening') return;
    // Из окна отмены вернулись потому, что человек продолжил говорить — тик в этот
    // момент лёг бы прямо на его речь, поэтому только заводим повтор
    startListening(prev !== 'pending');
    return () => stopListening();
  }, [phase, seq]);

  // Окно отмены: сигнал + 2 секунды. Тап по кнопке в этот момент — это выключение петли
  useEffect(() => {
    if (phase !== 'pending') return;
    beep();
    const id = setTimeout(() => dispatch({ type: 'pendingElapsed' }), PENDING_MS);
    return () => clearTimeout(id);
  }, [phase, seq]);

  // Отправка накопленного мимо поля ввода: черновик пользователя не трогаем.
  // Композер может ход и не отправить (механику «Команды» успели взвести уже внутри
  // петли) — тогда ждать нечего, возвращаемся слушать, а не стоим 60 с до сторожа
  useEffect(() => {
    if (phase !== 'sending') return;
    const text = bufferRef.current.trim();
    void Promise.resolve(o.current.onSend(text)).then((sent) => {
      // Ответ штатно приходит уже в фазе waiting (эффект перезапущен turnStarted) —
      // никакой отмены по смене фазы здесь быть не должно, иначе ветка мертва
      if (!mountedRef.current || sent !== false) return;
      if (!o.current.activeRef.current) return; // петлю успели выключить руками
      showToast('Разговор', 'Сообщение не ушло — слушаю дальше');
      dispatch({ type: 'sendFailed' });
    });
    dispatch({ type: 'turnStarted' });
  }, [phase, seq]);

  // Страховка Р13: ход снялся, а озвучка не заявилась за 1.5 с — значит её не будет.
  // Прямого перехода «ход кончился → слушаю» нет сознательно: эффекты композера идут
  // раньше родительского эффекта озвучки, и в том кадре willSpeak физически ещё нет
  useEffect(() => {
    if (phase !== 'waiting') return;
    if (opts.isGenerating || opts.speechPhase !== 'idle') return;
    const id = setTimeout(() => dispatch({ type: 'speechSkipped' }), SPEECH_WAIT_MS);
    return () => clearTimeout(id);
  }, [phase, seq, opts.isGenerating, opts.speechPhase]);

  // Фаза озвучки от родителя → события автомата
  const prevSpeechRef = useRef<SpeechPhase>('idle');
  useEffect(() => {
    const prev = prevSpeechRef.current;
    prevSpeechRef.current = opts.speechPhase;
    if (!activeRef.current) return;
    if (opts.speechPhase !== 'idle' && prev === 'idle') dispatch({ type: 'speechWillStart' });
    if (opts.speechPhase === 'idle' && prev !== 'idle') dispatch({ type: 'speechFinished' });
  }, [opts.speechPhase, activeRef]);

  // Вопрос модели или запрос разрешения — озвучиваем и выходим из петли
  useEffect(() => {
    if (!active || !opts.awaitingResponse) return;
    dispatch({ type: 'needsDecision' });
  }, [active, opts.awaitingResponse]);

  useEffect(() => {
    if (!active || !opts.offline) return;
    dispatch({ type: 'offline' });
    showToast('Разговор', 'Связь пропала — разговор выключен');
  }, [active, opts.offline]);

  // Сторож бездействия: ничего не происходит минуту — выключаемся, чтобы не жечь
  // батарею микрофоном и wake lock'ом
  useEffect(() => {
    if (!active) return;
    if (opts.isGenerating || opts.speechPhase !== 'idle') return;
    const id = setTimeout(() => dispatch({ type: 'idleTimeout' }), IDLE_MS);
    return () => clearTimeout(id);
  }, [active, phase, seq, opts.isGenerating, opts.speechPhase, opts.isListening]);

  // Реплики автомата о себе
  useEffect(() => {
    if (!notice) return;
    dispatch({ type: 'noticeSaid' });
    // Вопрос модели: сигнал играет ChatPanel (он звучит и вне петли), здесь только фраза —
    // и с задержкой, иначе слова легли бы на пинг
    if (notice === 'needDecision') {
      const id = setTimeout(() => { void speak(NOTICE_TEXT[notice]); }, NEED_ANSWER_DURATION_MS);
      return () => clearTimeout(id);
    }
    const said = speak(NOTICE_TEXT[notice]);
    // «Ты ещё здесь?» звучит внутри живой петли (фаза speaking, микрофон закрыт) —
    // по концу реплики возвращаемся слушать. Остальные реплики произносятся уже
    // после выключения, и автомату о них знать нечего
    if (notice === 'stillThere') void said.then(() => dispatch({ type: 'speechFinished' }));
    // Голосовой выход: автомат уже погашен, композеру осталось доделать хвост тапа
    // по кнопке (страховочное прерывание хода и PUT voiceMode=false)
    if (notice === 'voiceOff') o.current.onVoiceExit?.();
  }, [notice]);

  // Размонтирование (в т.ч. смена чата — Composer стоит с key по сессии): микрофон,
  // wake lock и аудиоконтекст обязаны уйти вместе с компонентом
  useEffect(() => {
    // Присваивание в теле обязательно: StrictMode в dev монтирует дважды, и cleanup
    // первого прохода погасил бы флаг навсегда — вместе с веткой «отправка не ушла»
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      try { o.current.stopMic(false); } catch { /* noop */ }
      if (!ownsGlobalsRef.current) return;
      releaseWakeLock();
      closeBeep();
    };
  }, []);

  const start = useCallback(() => {
    ownsGlobalsRef.current = true;
    primeBeep();
    dispatch({ type: 'toggle' });
  }, []);

  const stop = useCallback(() => {
    // Ход уже идёт — выключение петли обязано его прервать, иначе он дочитается вслух
    // уже «в пустоту»
    if (o.current.isGenerating) o.current.onStop();
    stopSpeaking();
    dispatch({ type: 'toggle' });
  }, []);

  const abort = useCallback((message: string) => {
    stopSpeaking();
    dispatch({ type: 'offline' }); // тот же выход, что и по потере связи: без реплики
    showToast('Разговор', message);
  }, []);

  const onRecognized = useCallback((text: string) => dispatch({ type: 'recognized', text }), []);
  const onCycleEnd = useCallback(() => dispatch({ type: 'cycleEnded' }), []);
  const onCycleError = useCallback((code: string) => {
    if (code === 'mic-dead') { dispatch({ type: 'micDead' }); return; }
    dispatch({ type: 'cycleError', code });
    // no-speech в петле — это просто тишина (Р9), остальное тостим с демпфером (Р17)
    if (code === 'no-speech' || code === 'aborted') return;
    const now = Date.now();
    if (now - lastErrorToastRef.current < ERROR_TOAST_MS) return;
    lastErrorToastRef.current = now;
    showToast('Разговор', `Распознавание: ${describeSpeechError(code)}`);
  }, []);

  return { phase, active, buffer, start, stop, abort, onRecognized, onCycleEnd, onCycleError };
}
