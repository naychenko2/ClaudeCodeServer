// Амплитуда голоса + пульс звуков для aurora-сияния композера (режим разговора).
//
// ДВА источника дыхания света, оба стекаются в CSS-переменную --amp (0..1),
// которую пишем прямо в DOM-узел сияния — React state не трогаем:
//   1. Честная амплитуда голоса: собственный getUserMedia-поток рядом с
//      SpeechRecognition петли → AnalyserNode → RMS.
//   2. Пульс звуковых сигналов (lib/auroraPulse): тик ожидания, «нужен ответ»,
//      micReady, бип отправки — свет вспыхивает в такт там, где амплитуда
//      недоступна (ход, озвучка, псевдо-режим).
//
// Отдельно: active крутит rAF-луп дыхания (всё время, пока сияние смонтировано),
// micActive открывает поток (только фазы listening/pending — та же эхо-дисциплина
// петли). Луп не зависит от потока: без него дышит псевдо-синусом + пульсами.
//
// Второй захват микрофона на части платформ (WebKit: iPhone/iPad/Safari) убивает
// сессию SpeechRecognition — то есть весь разговор, а не только сияние. Тройная
// защита прежняя: гейт платформ (Apple — сразу псевдо), канарейка (micDead при
// открытом потоке → псевдо), псевдо-режим как безопасный fallback. Вердикт о
// конфликте запоминается на устройстве, а не на вкладке (lib/ampConflict).

import { useEffect, useRef, useCallback, useState } from 'react';
import { takeAuroraPulse, onAuroraWake } from '../lib/auroraPulse';
import { AmpConflictDetector, isAmpUnsafeDevice, markAmpUnsafeDevice } from '../lib/ampConflict';
import { talkDiag } from '../lib/talkDiag';

// Окно канарейки: смерть движка распознавания в это время после открытия нашего
// потока считается конфликтом захватов
const CANARY_MS = 10_000;
// Сглаживание: attack быстрый (речь/вспышка мгновенны), release медленный (гаснет плавно)
const ATTACK = 0.4;
const RELEASE = 0.06;

// Кэш вкладки поверх памяти устройства (lib/ampConflict): вердикт переживает
// перезагрузку страницы, иначе каждое новое открытие вкладки платило бы за урок
// первым циклом слушания целиком
let ampUnsafeSession = false;

function isAmpUnsafe(): boolean {
  return ampUnsafeSession || isAmpUnsafeDevice();
}

function markAmpUnsafe(): void {
  ampUnsafeSession = true;
  markAmpUnsafeDevice();
}

// Apple-платформы: WebKit-реализация SpeechRecognition не терпит параллельного
// getUserMedia — там конфликт убивает распознавание. Считаем по UA: iOS-браузеры
// (включая Chrome на iPhone — он WebKit) и Safari macOS. Риск потерять весь
// разговор дороже честной амплитуды. Ложный негатив ловит канарейка, ложный
// позитив — деградация до псевдо, что безопасно.
//
// ВАЖНО: этим гейтом закрыт и VAD-канал барж-ина (lib/bargeVad.ts), а он живёт в
// фазе озвучки, когда Web Speech ЗАКРЫТ. Расширять эту функцию под «просто мобилу»
// нельзя — перебивание голосом выключилось бы там, где никакого конфликта нет.
// Для одновременного захвата есть отдельный гейт ниже
export function isAmpUnsafePlatform(): boolean {
  if (typeof navigator === 'undefined') return true;
  const ua = navigator.userAgent;
  return /iPhone|iPad|iPod/i.test(ua)
    || (/Safari/i.test(ua) && !/Chrome|Chromium|Edg|OPR/i.test(ua));
}

// Платформы, где нельзя держать свой поток ОДНОВРЕМЕННО с открытым распознаванием.
// Это строго про честную амплитуду: только у неё захват идёт параллельно Web Speech.
//
// Кроме Apple сюда попадают мобильные вообще: замер на Android-планшете показал
// тихую глухоту — движок формально жив (audiostart пришёл), но аудио достаётся
// нашему потоку, и первая фраза разговора пропадает целиком. Обучение по факту
// конфликта тут не спасает: урок стоит ~7 секунд и приходится ровно на первую
// фразу, когда человек уже говорит. Честная амплитуда остаётся десктопу
export function isParallelCaptureUnsafe(): boolean {
  if (typeof navigator === 'undefined') return true;
  return isAmpUnsafePlatform() || /Android|Mobile|Tablet/i.test(navigator.userAgent);
}

// Общий слот анализатора между двумя эффектами хука: эффект потока наполняет,
// луп дыхания читает. Обычный объект в ref — переиспользуется между запусками
type AnalyserSlot = { analyser: AnalyserNode | null };

export interface MicLevelOptions {
  // Крутить луп дыхания: пока сияние смонтировано (петля ИЛИ озвучка вне петли)
  active: boolean;
  // Открывать поток микрофона: только фазы listening/pending петли
  micActive: boolean;
  // Играет озвучка ответа (фаза speaking петли / speechPhase вне петли): микрофон
  // закрыт эхо-дисциплиной, честной амплитуды нет — включаем «речевой» псевдо-
  // паттерн (быстрая вибрация с медленной огибающей), чтобы сияние вибрировало
  // в такт речи модели, а не стояло статикой на тихом фоне
  speechActive: boolean;
  // Узел сияния: сюда пишется --amp на каждом кадре rAF
  targetRef: React.RefObject<HTMLElement | null>;
  // Ранний вердикт конфликта: мы слышим голос, движок молчит дольше порога.
  // Поток к этому моменту уже погашен и устройство помечено — вызывающему
  // остаётся перезапустить распознавание и сказать человеку, что не расслышали
  onEarlyConflict?: () => void;
}

export interface MicLevel {
  // Петля сообщила о смерти движка распознавания (событие автомата micDead).
  // Если наш поток был открыт недавно — конфликт признан, второй захват на этой
  // вкладке запрещается до перезагрузки
  reportMicDead: () => void;
  // Конец цикла распознавания; barren — движок не отдал ни слова за цикл.
  // Прокидывается в детектор конфликта: голос в нашей амплитуде при глухом
  // движке = второй захват перехватил микрофон
  reportCycleEnd: (barren: boolean) => void;
  // Движок подал признак слуха (soundstart/speechstart/результат): звук до него
  // доходит, ранний вердикт в этом цикле снимается
  reportEngineHeard: () => void;
}

export function useMicLevel({ active, micActive, speechActive, targetRef, onEarlyConflict }: MicLevelOptions): MicLevel {
  const slotRef = useRef<AnalyserSlot>({ analyser: null });
  // Детектор конфликта захватов: наша амплитуда голос слышит, движок — нет.
  // Живёт между фазами петли (слушание → ожидание → слушание), поэтому ref
  const conflictRef = useRef(new AmpConflictDetector());
  // Тик принудительного перезапуска эффекта потока: конфликт обнаружен посреди
  // живого захвата — гасим его немедленно, не дожидаясь смены фазы петли
  const [streamKick, setStreamKick] = useState(0);
  // Момент открытия real-потока (0 — не открыт). Нужен канарейке: reportMicDead
  // приходит извне, после факта
  const openedAtRef = useRef(0);
  // Колбэк раннего вердикта в ref: rAF-луп живёт весь период active и замкнул бы
  // устаревшую версию
  const earlyRef = useRef(onEarlyConflict);
  useEffect(() => { earlyRef.current = onEarlyConflict; });
  // Зеркало speechActive: эффект лупа живёт весь период active (deps [active]),
  // а фаза озвучки приходит/уходит по ходу — читаем актуальное значение из ref,
  // иначе замыкание навсегда запоминает speechActive рендера запуска
  const speechRef = useRef(speechActive);
  useEffect(() => { speechRef.current = speechActive; }, [speechActive]);
  // Момент старта ТЕКУЩЕЙ озвучки: от него считаем фазу огибающей «фраза/пауза»,
  // чтобы каждая реплика начиналась с «вдоха», а не с рандомной точки цикла
  const speechStartRef = useRef(0);
  // Троттлинг диагностического лога громкости (раз в 2 с)
  const lastVoiceLogRef = useRef(0);

  // --- Луп дыхания: весь период active, источник амплитуды подключается ниже ---
  useEffect(() => {
    if (!active) return;
    const target = targetRef.current;
    if (!target) return;

    let amp = 0;
    let raf = 0;
    let cancelled = false;
    const slot = slotRef.current;
    slot.analyser = null;
    const startTs = performance.now();

    const setAmp = (v: number) => {
      target.style.setProperty('--amp', v.toFixed(3));
    };

    // Луп дыхания, три режима:
    //   - голос человека (микрофонные фазы, есть analyser): честная амплитуда RMS;
    //   - озвучка ответа (speechActive): «речевой» псевдо-паттерн — вибрация ~4 Гц
    //     с медленной огибающей (~0.25 Гц), читается как говорящий свет; честную
    //     амплитуду с TTS-аудио брать нельзя (перехват вывода/нет потока у
    //     speechSynthesis), поэтому паттерн детерминированный;
    //   - ход модели / псевдо-фолбэк: ровное тихое «дыхание» (волна 5 с) — фон,
    //     поверх которого вспыхивают тики.
    // Пульсы звуковых сигналов поверх всех режимов — с МГНОВЕННОЙ атакой:
    // вспышка встаёт в кадр звука, а не выезжает сглаживанием
    const loop = () => {
      if (cancelled) return;
      const speaking = speechRef.current;
      // Старт озвучки ловим по фронту: огибающая «фраза/пауза» начинается с вдоха
      if (speaking && !speechStartRef.current) speechStartRef.current = performance.now();
      if (!speaking) speechStartRef.current = 0;

      let raw: number;
      const t = (performance.now() - startTs) / 1000;
      if (slot.analyser) {
        const buf = new Uint8Array(slot.analyser.fftSize);
        slot.analyser.getByteTimeDomainData(buf);
        let sum = 0;
        for (let i = 0; i < buf.length; i++) {
          const v = (buf[i] - 128) / 128;
          sum += v * v;
        }
        const rms = Math.sqrt(sum / buf.length);
        // Нормировка с запасом: бытовая речь даёт RMS ~0.05-0.3
        raw = Math.min(1, rms * 4);
        // Корм детектора конфликта: речь в нашей амплитуде — это RMS заметно выше
        // фонового шума (~0.2 после нормировки; порог ниже пиковой речи, но выше
        // дыхания/шума комнаты)
        if (raw > 0.2) {
          conflictRef.current.noteVoice();
          // Ранний вердикт: голос идёт, движок молчит. Ждать конца цикла нельзя —
          // он тянется 5-6 секунд, и всё сказанное за них пропадает
          if (conflictRef.current.earlyConflict()) {
            talkDiag('amp: РАННИЙ КОНФЛИКТ — движок глух под нашим захватом');
            markAmpUnsafe();
            conflictRef.current.cycleStart(); // вердикт разовый: не повторяем каждый кадр
            setStreamKick(k => k + 1);
            earlyRef.current?.();
          }
          // Диагностика: фиксируем первый громкий кадр цикла (не спамим каждый кадр).
          // Латиница в значении — чтобы не путаться в похожих кириллических логах
          if (performance.now() - lastVoiceLogRef.current > 2000) {
            lastVoiceLogRef.current = performance.now();
            talkDiag(`amp: voice raw=${raw.toFixed(2)}`);
          }
        }
      } else if (speaking) {
        // Озвучка: одна плавная волна от старта реплики — подъём ~1.2 с, спад ~1.2 с,
        // затем мягкий повтор (0.3 Гц: цикл ~3.3 с). Без вибрации: частые синусы
        // поверх огибающей выглядели рандомной тряской. Возврат в [0.25..0.9]:
        // заметно, но спокойно; волну ведём напрямую (сглаживание её исказит)
        const ts = (performance.now() - speechStartRef.current) / 1000;
        raw = 0.25 + 0.65 * (0.5 - 0.5 * Math.cos(ts * 2 * Math.PI * 0.3));
      } else {
        // Тихий фон (0.10..0.50, период 5 с): тики вспыхивают ПОВЕРХ него и всегда
        // читаются — при громком фоне (раньше до 0.85) вспышка 0.6 тонула в волне
        raw = 0.3 + 0.2 * Math.sin(t * 2 * Math.PI * 0.2);
      }
      const pulse = takeAuroraPulse();
      if (speaking) {
        amp = Math.max(raw, pulse);
      } else {
        amp += (raw - amp) * (raw > amp ? ATTACK : RELEASE);
        if (pulse > amp) amp = pulse;    // в такт звуку: вспышка минует сглаживание
      }
      setAmp(amp);
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    // Будим луп по пульсу (страховка: луп зациклен сам, гард !raf не даст дубля)
    const offWake = onAuroraWake(() => { if (!cancelled && !raf) raf = requestAnimationFrame(loop); });

    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
      offWake();
      target.style.removeProperty('--amp');
    };
    // targetRef — ref-объект, стабилен между рендерами
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active]);

  // --- Поток честной амплитуды: только при micActive (эхо-дисциплина петли) ---
  useEffect(() => {
    if (!micActive) return;

    // Копия слота в замыкание эффекта: к моменту cleanup ref мог быть перезаписан
    // (StrictMode double-mount), гасить надо тот анализатор, что положили здесь
    const slot = slotRef.current;
    let stream: MediaStream | null = null;
    let ctx: AudioContext | null = null;
    let cancelled = false;

    conflictRef.current.setStream(true);
    if (isAmpUnsafe()) {
      talkDiag('amp: поток не открываем — вкладка помечена ampUnsafe (конфликт/канарейка)');
    } else if (isParallelCaptureUnsafe()) {
      talkDiag('amp: поток не открываем — платформа не терпит параллельный захват');
    } else {
      talkDiag('amp: открываю getUserMedia для амплитуды');
      // real-путь с деградацией: отказ — тихо остаёмся в псевдо, разговор не трогаем.
      // Эффект микрофона петли уже открыл SpeechRecognition — условие канарейки
      void navigator.mediaDevices?.getUserMedia({ audio: { echoCancellation: true } })
        .then((s) => {
          if (cancelled) { s.getTracks().forEach(t => t.stop()); return; }
          stream = s;
          const AC = window.AudioContext
            ?? (window as Window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
          if (!AC) { talkDiag('amp: AudioContext недоступен — псевдо'); return; }
          ctx = new AC();
          const analyser = ctx.createAnalyser();
          analyser.fftSize = 512;
          ctx.createMediaStreamSource(s).connect(analyser);
          slot.analyser = analyser;
          openedAtRef.current = Date.now();
          talkDiag('amp: поток открыт, анализатор подключен');
        })
        .catch((e) => {
          talkDiag('amp: getUserMedia отказ — остаёмся в псевдо', e instanceof Error ? e.message : e);
        });
    }

    return () => {
      cancelled = true;
      conflictRef.current.setStream(false);
      stream?.getTracks().forEach(t => t.stop());
      void ctx?.close().catch(() => { /* уже закрыт */ });
      slot.analyser = null;
      openedAtRef.current = 0;
    };
    // streamKick: конфликт захватов гасит живой поток немедленно — эффект
    // перезапускается, но real-путь уже закрыт вердиктом ampUnsafe
  }, [micActive, streamKick]);

  const reportMicDead = useCallback(() => {
    const opened = openedAtRef.current;
    if (opened && Date.now() - opened <= CANARY_MS) markAmpUnsafe();
  }, []);

  const reportCycleEnd = useCallback((barren: boolean) => {
    const verdict = conflictRef.current.cycleEnd(barren);
    talkDiag('amp: конец цикла', { barren, verdict });
    if (!verdict) return;
    // Конфликт подтверждён: голос был у нас, движок глух. Честная амплитуда
    // отключается до перезагрузки вкладки — разговор дороже сияния. Немедленный
    // перезапуск эффекта гасит живой захват, не дожидаясь смены фазы петли
    talkDiag('amp: КОНФЛИКТ ЗАХВАТОВ — вырубаю честную амплитуду (запомнено на устройстве)');
    markAmpUnsafe();
    setStreamKick(k => k + 1);
  }, []);

  const reportEngineHeard = useCallback(() => conflictRef.current.noteEngineHeard(), []);
  return { reportMicDead, reportCycleEnd, reportEngineHeard };
}
