import { useState, useRef, useEffect, useCallback } from 'react';
import { showToast } from '../lib/toast';
import {
  isMicKeyboardFallback, setMicKeyboardFallback,
  describeSpeechError, isSilentSpeechError, MIC_FALLBACK_TEXT,
} from '../lib/voiceInput';

// Сколько ждём первый звук от движка распознавания, прежде чем счесть его мёртвым.
// 2.5с не хватало планшетам: холодный старт облачного распознавания медленнее, чем на телефоне,
// и живой движок ошибочно попадал в клавиатурный фоллбэк.
const MIC_WATCHDOG_MS = 6000;

// Минимальная форма Web Speech API: стандартных типов в lib.dom нет
// (движок с вендорным префиксом, состав событий различается по браузерам)
interface SpeechRecognitionLike {
  lang: string;
  interimResults: boolean;
  continuous: boolean;
  maxAlternatives: number;
  start(): void;
  stop(): void;
  abort(): void;
  onstart: (() => void) | null;
  onaudiostart: (() => void) | null;
  onsoundstart: (() => void) | null;
  onspeechstart: (() => void) | null;
  onresult: ((e: SpeechResultEventLike) => void) | null;
  onend: (() => void) | null;
  onerror: ((e: { error?: string }) => void) | null;
}

// Результат распознавания: из вариантов берём только финальный транскрипт
interface SpeechResultEventLike {
  results: {
    length: number;
    [i: number]: { isFinal?: boolean; [i: number]: { transcript?: string } };
  };
}

export interface VoiceInputOptions {
  // Распознанный кусок текста — вызывающий сам решает, куда его дописать
  onResult: (chunk: string) => void;
  // Движок распознавания недоступен: диктовать нужно системным голосовым вводом
  // клавиатуры, поэтому просто ставим фокус в поле
  onKeyboardFallback: () => void;
}

export interface VoiceInput {
  // Есть ли Web Speech в браузере — по нему решается, показывать ли кнопку микрофона
  hasSpeech: boolean;
  isListening: boolean;
  recSeconds: number;
  startMic: () => void;
  // confirm=true — остановить и отдать распознанное; false — отменить без вставки
  stopMic: (confirm: boolean) => void;
}

// Голосовой ввод. На устройствах с рабочим Web Speech (телефоны) распознаём сами.
// Где движок «мёртвый» (например, Huawei без Google-сервисов) — отдаём управление
// вызывающему через onKeyboardFallback, чтобы надиктовать клавиатурой.
export function useVoiceInput({ onResult, onKeyboardFallback }: VoiceInputOptions): VoiceInput {
  const [isListening, setIsListening] = useState(false);
  const [recSeconds, setRecSeconds] = useState(0);
  const recognitionRef = useRef<SpeechRecognitionLike | null>(null);
  const recCancelRef = useRef(false);
  const micWatchdogRef = useRef<number | null>(null); // детект «мёртвого» Web Speech (нет признаков жизни)

  // Колбэки держим в ref: пересоздание обработчиков движка на каждый рендер
  // роняло бы активное распознавание
  const onResultRef = useRef(onResult);
  const onFallbackRef = useRef(onKeyboardFallback);
  onResultRef.current = onResult;
  onFallbackRef.current = onKeyboardFallback;

  const hasSpeech = typeof window !== 'undefined' &&
    ('SpeechRecognition' in window || 'webkitSpeechRecognition' in window);

  // При размонтировании гасим watchdog вместе с распознаванием, иначе таймер
  // дёрнет состояние уже после ухода компонента
  useEffect(() => () => {
    if (micWatchdogRef.current !== null) clearTimeout(micWatchdogRef.current);
    try { recognitionRef.current?.abort(); } catch { /* noop */ }
  }, []);

  // Таймер записи голоса
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- таймер секунд записи на время слушания
    if (!isListening) { setRecSeconds(0); return; }
    setRecSeconds(0);
    const id = setInterval(() => setRecSeconds(s => s + 1), 1000);
    return () => clearInterval(id);
  }, [isListening]);

  const startMic = useCallback(() => {
    if (isListening) return;

    // Web Speech отсутствует или ранее выяснили, что он не работает → сразу клавиатура.
    if (!hasSpeech || isMicKeyboardFallback()) {
      onFallbackRef.current();
      return;
    }

    const w = window as Window & {
      SpeechRecognition?: new () => SpeechRecognitionLike;
      webkitSpeechRecognition?: new () => SpeechRecognitionLike;
    };
    const SpeechRecognitionCtor = w.SpeechRecognition ?? w.webkitSpeechRecognition;
    // Проверка hasSpeech выше уже проходила, но свойство может отсутствовать —
    // тогда клавиатурный фоллбэк вместо падения на `new undefined`
    if (!SpeechRecognitionCtor) { onFallbackRef.current(); return; }
    const rec = new SpeechRecognitionCtor();
    rec.lang = 'ru-RU';
    rec.interimResults = true;
    rec.continuous = false;
    rec.maxAlternatives = 1;
    recCancelRef.current = false;

    let gotAudio = false;
    const clearWatchdog = () => {
      if (micWatchdogRef.current !== null) { clearTimeout(micWatchdogRef.current); micWatchdogRef.current = null; }
    };

    // Живым считаем движок по ЛЮБОМУ признаку жизни, а не только по audiostart:
    // часть браузеров (Android, WebView) его не эмитит, хотя распознавание работает —
    // и watchdog убивал вполне рабочий движок.
    const alive = () => { gotAudio = true; clearWatchdog(); };

    rec.onstart = alive;
    rec.onaudiostart = alive;
    rec.onsoundstart = alive;
    rec.onspeechstart = alive;

    rec.onresult = (e: SpeechResultEventLike) => {
      alive();
      let last = '';
      for (let i = 0; i < e.results.length; i++) {
        const r = e.results[i];
        if (r.isFinal && r[0]?.transcript) last = r[0].transcript;
      }
      if (recCancelRef.current) return; // отменено — не вставляем
      if (last) onResultRef.current(last);
    };

    rec.onend = () => { clearWatchdog(); setIsListening(false); };
    rec.onerror = (e: { error?: string }) => {
      clearWatchdog();
      setIsListening(false);
      // Причина сбоя — прямо в тост: без неё на устройстве не понять, что именно не так
      const code = String(e?.error ?? 'unknown');
      if (isSilentSpeechError(code)) return;
      showToast('Голосовой ввод', `Не удалось: ${describeSpeechError(code)}`);
    };

    recognitionRef.current = rec;
    try {
      rec.start();
      setIsListening(true);
      // Детектор «мёртвого» движка: если за MIC_WATCHDOG_MS не пришёл audiostart —
      // распознавания в браузере нет (нет Google-сервисов). Переходим на клавиатурный
      // ввод и запоминаем выбор, чтобы впредь сразу открывать клавиатуру.
      micWatchdogRef.current = window.setTimeout(() => {
        if (gotAudio) return;
        micWatchdogRef.current = null;
        try { rec.abort(); } catch { /* noop */ }
        setIsListening(false);
        setMicKeyboardFallback();
        showToast('Голосовой ввод', MIC_FALLBACK_TEXT);
      }, MIC_WATCHDOG_MS);
    } catch {
      setIsListening(false);
    }
  }, [isListening, hasSpeech]);

  const stopMic = useCallback((confirm: boolean) => {
    recCancelRef.current = !confirm;
    if (micWatchdogRef.current !== null) { clearTimeout(micWatchdogRef.current); micWatchdogRef.current = null; }
    setIsListening(false); // фикс: закрываем режим записи сразу, не дожидаясь onend (его может не быть)
    try {
      if (confirm) recognitionRef.current?.stop();
      else recognitionRef.current?.abort();
    } catch { /* noop */ }
  }, []);

  return { hasSpeech, isListening, recSeconds, startMic, stopMic };
}
