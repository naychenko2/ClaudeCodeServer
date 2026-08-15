// Короткий звуковой сигнал режима разговора: им отмечается момент «речь распозналась,
// пошло окно отмены» — на прогулке экран не виден, и это единственный внятный признак,
// что сейчас уйдёт отправка.
//
// Тон синтезируем WebAudio, а не проигрываем файл: 40 мс писка не стоят сетевого запроса
// и не должны конкурировать за HTMLAudioElement с озвучкой ответа.

let ctx: AudioContext | null = null;

type AudioContextCtor = new () => AudioContext;

function ctor(): AudioContextCtor | null {
  if (typeof window === 'undefined') return null;
  const w = window as Window & { AudioContext?: AudioContextCtor; webkitAudioContext?: AudioContextCtor };
  return w.AudioContext ?? w.webkitAudioContext ?? null;
}

// «Разогрев» из пользовательского жеста: политика autoplay даёт AudioContext состояние
// suspended, пока не было явного действия. Зовётся в том же клике, что primeAudio/startMic —
// синхронно, без await, иначе жест «рвётся» и браузер контекст не пускает.
export function primeBeep(): void {
  try {
    const Ctor = ctor();
    if (!Ctor) return;
    ctx ??= new Ctor();
    if (ctx.state === 'suspended') void ctx.resume().catch(() => { /* не дал — попробуем на следующем жесте */ });
  } catch { /* WebAudio недоступен — сигнала просто не будет, это не повод падать */ }
}

// Один тон с мягкой огибающей. Щелчок от резкого обрыва неприятнее самого сигнала,
// поэтому и атака, и спад идут рампой.
function tone(freq: number, peak: number, durSec: number, type: OscillatorType = 'sine', delaySec = 0): void {
  try {
    if (!ctx) { primeBeep(); }
    if (!ctx || ctx.state !== 'running') return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = type;
    osc.frequency.value = freq;
    const now = ctx.currentTime + delaySec;
    gain.gain.setValueAtTime(0.0001, now);
    gain.gain.exponentialRampToValueAtTime(peak, now + 0.008);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + durSec);
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start(now);
    osc.stop(now + durSec + 0.01);
  } catch { /* noop */ }
}

// Сигнал ~40 мс. Тихая деградация: нет WebAudio или контекст не разбужен — молчим.
export function beep(): void {
  tone(880, 0.12, 0.04);
}

// Фон на время ожидания ответа: на прогулке экран не виден, и пауза в полминуты
// неотличима от «всё зависло». Вариант «мягкая нота» (референс
// .cc-attachments/sounds/thinking-1-current.wav) с разрежённым до 4 секунд шагом:
// низкий тон слышно в кармане лучше высокого, а редкий период не надоедает.
const THINKING_PERIOD_MS = 4000;
let thinkingTimer: ReturnType<typeof setInterval> | null = null;

function tick(): void {
  tone(330, 0.10, 0.09);
}

export function startThinking(): void {
  if (thinkingTimer !== null) return; // уже тикаем — второй таймер дал бы частокол
  tick(); // первый тик сразу: подтверждение, что сообщение ушло в работу
  thinkingTimer = setInterval(tick, THINKING_PERIOD_MS);
}

export function stopThinking(): void {
  if (thinkingTimer === null) return;
  clearInterval(thinkingTimer);
  thinkingTimer = null;
}

// «Нужно твоё решение»: модель задала вопрос или просит разрешение, а человек смотрит
// не на экран. Тройной пинг (референс .cc-attachments/sounds/waiting-2-triple.wav) —
// заметно настойчивее одиночного сигнала, но без тревожной интонации. Звучит один раз:
// следом идёт голосовая фраза, а петля выходит из режима.
const NEED_ANSWER_GAP_SEC = 0.16;
export const NEED_ANSWER_DURATION_MS = 560;

export function needAnswer(): void {
  for (let i = 0; i < 3; i++) tone(780, 0.22, 0.08, 'sine', i * NEED_ANSWER_GAP_SEC);
}

// «Микрофон открыт, говори» (референс .cc-attachments/sounds/listen-3-double.wav).
// Двойной тик вверх, 30 мс каждый: звучит каждый круг разговора, поэтому короче и тише
// прочих сигналов. Двойной намеренно — одиночный писк уже занят отправкой, и на слух
// эти два события должны различаться без раздумий.
export const MIC_READY_DURATION_MS = 120;

export function micReady(): void {
  tone(520, 0.12, 0.03);
  tone(700, 0.12, 0.03, 'sine', 0.09);
}

// Пока микрофон открыт, тик повторяется: одного сигнала при открытии мало — на ходу
// не помнишь, слушают тебя сейчас или ответ ещё читается. Период редкий (5.5 с), чтобы
// напоминание не превратилось в метроном; при первом же распознанном слове фаза уходит
// в окно отправки, и тиканье прекращается само.
const LISTENING_PERIOD_MS = 5500;
let listeningTimer: ReturnType<typeof setInterval> | null = null;

// playNow=false — продолжение речи в окне отмены: человек уже говорит, лишний тик
// в этот момент только мешает
export function startListening(playNow = true): void {
  if (listeningTimer !== null) return;
  if (playNow) micReady();
  listeningTimer = setInterval(micReady, LISTENING_PERIOD_MS);
}

export function stopListening(): void {
  if (listeningTimer === null) return;
  clearInterval(listeningTimer);
  listeningTimer = null;
}

// Закрытие контекста при выходе из режима (Р19): висящий AudioContext держит аудиосессию
// устройства и на телефоне заметен по индикатору
export function closeBeep(): void {
  try {
    stopThinking(); // иначе таймеры продолжат дёргать закрытый контекст
    stopListening();
    const c = ctx;
    ctx = null;
    void c?.close().catch(() => { /* уже закрыт */ });
  } catch { /* noop */ }
}
