// Барж-ин: перебивание озвучки голосом в режиме разговора (P1 среза
// docs/research/voice-mode-benchmark.md).
//
// Под играющую озвучку открыт ОТДЕЛЬНЫЙ слушающий канал: getUserMedia (эхо гасит
// браузерный AEC) + Silero VAD в аудио-ворк-лете (@ricky0123/vad-web, ONNX).
// Перебивание ДВУХСТУПЕНЧАТОЕ (решает lib/bargeDetect по сырым кадрам): речь ~300 мс →
// озвучка приглушается, речь продолжилась до ~550 мс → обрыв всерьёз; смолкла раньше →
// громкость возвращается, будто ничего не было. Так ложное срабатывание (реплика рядом,
// телевизор) стоит полусекунды тишины, а не потерянного ответа. Распознавание остаётся
// Web Speech — он включается уже ПОСЛЕ обрыва (полумера первого шага; полный переезд на
// потоковый STT — P1.2).
//
// Дисциплина микрофона: трек живёт ТОЛЬКО пока канал явно включён (фаза speaking
// петли). stopBargeVad() отпускает треки немедленно (pauseStream у vad-web зовёт
// track.stop()) — к открытию Web Speech в фазе listening второго захвата уже нет.
// Это критично: на WebKit (iPhone/iPad/Safari) параллельный захват убивает
// SpeechRecognition — такие платформы отсечены тем же гейтом, что у амплитуды сияния
// (isAmpUnsafePlatform), барж-ина там нет вовсе.
//
// Ассеты (модель, worklet, WASM) раздаются со СВОЕГО хоста из /vad/
// (vite-plugin-static-copy): CDN здесь означал бы «фича молча не работает» при
// DPI-блокировках. Модель греется один раз на вкладку (MicVAD со startOnLoad:false),
// повторный вход в фазу озвучки — только новый getUserMedia.

import type { MicVAD } from '@ricky0123/vad-web';
import { talkDiag } from './talkDiag';
import { isAmpUnsafePlatform } from '../hooks/useMicLevel';
import { createBargeDetector, BARGE_DEFAULTS, type BargeDetector } from './bargeDetect';

// Решение о перебивании принимает СВОЙ детектор (lib/bargeDetect) по сырым кадрам, а не
// события vad-web: нам нужны две ступени (приглушить → оборвать) и гейт по громкости,
// которых у библиотеки нет. Её собственные пороги ставим широкими — фильтрует детектор.
const RAW_POSITIVE = 0.5;
const RAW_NEGATIVE = 0.35;

// Приглушение первой ступени: слышно, что ответ ещё идёт, но говорить он больше не мешает
export const DUCK_VOLUME = 0.2;

export interface BargeHandlers {
  // Речь началась — приглушить озвучку (обратимо)
  onDuck: () => void;
  // Ложная тревога: речь смолкла, громкость обратно
  onRelease: () => void;
  // Речь продолжается — перебивание всерьёз
  onCut: () => void;
}

let vad: MicVAD | null = null;
let initPromise: Promise<void> | null = null;
// Инициализация провалилась (нет прав, WASM не загрузился) — тихая деградация:
// до перезагрузки вкладки канал не поднимаем, петля живёт как без барж-ина
let initFailed = false;
// Канал должен слушать прямо сейчас. Отдельно от vad.listening: init ленивый, и флаг
// решает, открывать ли микрофон по его завершении; заодно гейтит кадры, доехавшие
// из воркета уже после stopBargeVad()
let wanted = false;
let handlers: BargeHandlers | null = null;
let detector: BargeDetector | null = null;

// Сводка кадров раз в ~2 секунды. Когда перебивание НЕ сработало, лог молчит, и причину
// («речи не видно» против «речь видно, но гейт громкости её не пустил») различить нечем —
// а крутить пороги вслепую мы уже пробовали. Пишем максимумы за окно и сам гейт
const LOG_EVERY = 63; // кадров v5 по 32 мс ≈ 2 с
let frames = 0;
let maxProb = 0;
let maxRms = 0;

function traceFrame(prob: number, level: number): void {
  frames++;
  if (prob > maxProb) maxProb = prob;
  if (level > maxRms) maxRms = level;
  if (frames < LOG_EVERY) return;
  const gate = Math.max(BARGE_DEFAULTS.minRms, (detector?.background() ?? 0) * BARGE_DEFAULTS.bgFactor);
  talkDiag(`barge: кадры за 2с — речь до ${maxProb.toFixed(2)}, громкость до ${maxRms.toFixed(3)}, порог ${gate.toFixed(3)}`);
  frames = 0; maxProb = 0; maxRms = 0;
}

// Громкость кадра. Гейт детектора работает по ней: дальний источник (телевизор за
// стеной, разговор в другой комнате) тише речи в полуметре на порядок
function rms(frame: Float32Array): number {
  let sum = 0;
  for (let i = 0; i < frame.length; i++) sum += frame[i] * frame[i];
  return Math.sqrt(sum / (frame.length || 1));
}

// Применить решение детектора. Вынесено из колбэка кадра: та же развилка нужна и при
// закрытии канала (reset), где приглушение обязано сняться
function apply(action: ReturnType<BargeDetector['push']>): void {
  if (action === 'none' || !handlers) return;
  if (action === 'duck') {
    talkDiag(`barge: речь под озвучкой — приглушаю (фон ${detector?.background().toFixed(3) ?? '?'})`);
    handlers.onDuck();
    return;
  }
  // Набранное к сбросу пишем всегда: по нему видно, дотянула ли речь до обрыва и
  // насколько промахнулась — иначе пороги подбираются вслепую
  const said = detector?.lastSpeechMs() ?? 0;
  if (action === 'release') {
    talkDiag(`barge: речь смолкла (набрано ${said}мс) — громкость обратно`);
    handlers.onRelease();
    return;
  }
  talkDiag(`barge: речь продолжается (${said}мс) — перебиваю`);
  handlers.onCut();
}

export function bargeVadSupported(): boolean {
  return typeof navigator !== 'undefined'
    && !!navigator.mediaDevices?.getUserMedia
    && !isAmpUnsafePlatform()
    && !initFailed;
}

// Ленивый прогрев: модуль (отдельный чанк) + модель ONNX. Один раз на вкладку
async function ensure(): Promise<void> {
  if (vad || initFailed) return;
  initPromise ??= (async () => {
    talkDiag('barge: загружаю VAD (модуль + модель)');
    const { MicVAD } = await import('@ricky0123/vad-web');
    vad = await MicVAD.new({
      model: 'v5',
      baseAssetPath: '/vad/',
      onnxWASMBasePath: '/vad/',
      startOnLoad: false,
      positiveSpeechThreshold: RAW_POSITIVE,
      negativeSpeechThreshold: RAW_NEGATIVE,
      // Однопоточный WASM: threaded требует crossOriginIsolated, которого у продукта нет
      ortConfig: (ort) => {
        ort.env.wasm.numThreads = 1;
        ort.env.logLevel = 'error';
      },
      onFrameProcessed: (probs, frame) => {
        if (!wanted || !detector) return; // кадр доехал после выключения канала
        const level = rms(frame);
        traceFrame(probs.isSpeech, level);
        apply(detector.push(probs.isSpeech, level));
      },
    });
    talkDiag('barge: VAD готов');
  })().catch((e: unknown) => {
    initFailed = true;
    initPromise = null;
    vad = null;
    talkDiag('barge: init провален, канал выключен до перезагрузки —',
      e instanceof Error ? e.message : String(e));
  });
  await initPromise;
}

// Включить канал (вход петли в фазу озвучки). Fire-and-forget: при первом вызове
// уходит в прогрев, и микрофон откроется по его завершении — если фаза ещё длится
export function startBargeVad(h: BargeHandlers): void {
  if (!bargeVadSupported()) return;
  wanted = true;
  handlers = h;
  detector = createBargeDetector({ ...BARGE_DEFAULTS });
  frames = 0; maxProb = 0; maxRms = 0;
  void ensure().then(() => {
    if (!wanted || !vad || vad.listening) return;
    talkDiag('barge: открываю микрофон VAD');
    void vad.start().then(() => {
      // Канал успели выключить, пока getUserMedia был в полёте (первый старт ставит
      // listening только ПОСЛЕ резолва — stopBargeVad в этом окне pause не звал):
      // без перепроверки трек остался бы открытым навсегда, с горящим индикатором
      if (!wanted) void vad?.pause().catch(() => { /* канал и так закрывается */ });
    }).catch((e: unknown) => {
      // Вечный отказ — только на запрете прав (иначе каждый вход в озвучку дёргал бы
      // браузерный промпт); транзиентное («устройство занято» и т.п.) — пропускаем
      // только этот вход
      if (e instanceof DOMException && e.name === 'NotAllowedError') initFailed = true;
      talkDiag('barge: микрофон не открылся —', e instanceof Error ? e.message : String(e));
    });
  });
}

// Выключить канал: треки отпускаются сразу, к открытию Web Speech второго захвата нет
export function stopBargeVad(): void {
  wanted = false;
  // Канал закрывается приглушённым (перебивание не дозрело до обрыва) — вернуть
  // громкость обязаны здесь: озвучка доиграет остаток в полный голос
  if (detector) apply(detector.reset());
  detector = null;
  handlers = null;
  if (vad?.listening) {
    talkDiag('barge: отпускаю микрофон VAD');
    void vad.pause().catch(() => { /* канал и так закрывается */ });
  }
}
