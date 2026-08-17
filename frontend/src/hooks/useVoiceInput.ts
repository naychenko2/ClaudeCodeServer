import { useState, useRef, useEffect, useCallback } from 'react';
import { showToast } from '../lib/toast';
import { talkDiag } from '../lib/talkDiag';
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
  // Цикл распознавания закончился (движок сам остановился). Нужен режиму разговора:
  // по нему открывается следующий круг слушания
  onEnd?: () => void;
  // Код ошибки движка (SpeechRecognitionErrorEvent.error) плюс синтетический
  // 'mic-dead' от watchdog. Режим разговора считает по ним бесплодные циклы
  onError?: (code: string) => void;
  // Тосты об ошибках берёт на себя вызывающий (в петле разговора они демпфируются,
  // а 'no-speech' там вообще норма — просто тишина). Функция, а не флаг: вызывающий
  // читает своё состояние в момент события, не в рендере
  quiet?: () => boolean;
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

function detectSpeechSupport(): boolean {
  return typeof window !== 'undefined' &&
    ('SpeechRecognition' in window || 'webkitSpeechRecognition' in window);
}

// Голосовой ввод. На устройствах с рабочим Web Speech (телефоны) распознаём сами.
// Где движок «мёртвый» (например, Huawei без Google-сервисов) — отдаём управление
// вызывающему через onKeyboardFallback, чтобы надиктовать клавиатурой.
export function useVoiceInput({ onResult, onKeyboardFallback, onEnd, onError, quiet }: VoiceInputOptions): VoiceInput {
  const [isListening, setIsListening] = useState(false);
  const [recSeconds, setRecSeconds] = useState(0);
  const recognitionRef = useRef<SpeechRecognitionLike | null>(null);
  const recCancelRef = useRef(false);
  const micWatchdogRef = useRef<number | null>(null); // детект «мёртвого» Web Speech (нет признаков жизни)
  // Зеркало isListening в ref: startMic обязан быть СТАБИЛЬНЫМ колбэком, иначе режим
  // разговора не сможет перезапустить распознавание из onEnd — в замкнутой там версии
  // startMic ещё стоит isListening = true, и гвард `if (isListening) return` молча съедал
  // бы весь цикл. Ref пишется вместе с состоянием, поэтому виден уже в том же обработчике
  const listeningRef = useRef(false);
  const setListening = useCallback((v: boolean) => {
    listeningRef.current = v;
    setIsListening(v);
  }, []);

  // Колбэки держим в ref: пересоздание обработчиков движка на каждый рендер
  // роняло бы активное распознавание
  const onResultRef = useRef(onResult);
  const onFallbackRef = useRef(onKeyboardFallback);
  const onEndRef = useRef(onEnd);
  const onErrorRef = useRef(onError);
  const quietRef = useRef(quiet);
  useEffect(() => {
    onResultRef.current = onResult;
    onFallbackRef.current = onKeyboardFallback;
    onEndRef.current = onEnd;
    onErrorRef.current = onError;
    quietRef.current = quiet;
  });

  const hasSpeech = detectSpeechSupport();

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
    // Гвард от второго движка поверх живого — но только пока движок ДЕЙСТВИТЕЛЬНО жив.
    // Один флаг залипал: если распознаватель закончился, не позвав onend/onerror (петля
    // разговора гасит его своими эффектами, мобильные движки — при потере фокуса),
    // кнопка микрофона переставала работать до перезагрузки страницы
    if (listeningRef.current && recognitionRef.current) return;
    listeningRef.current = false;

    // Web Speech отсутствует или ранее выяснили, что он не работает → сразу клавиатура.
    if (!detectSpeechSupport() || isMicKeyboardFallback()) {
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

    rec.onstart = () => { talkDiag('engine: start'); alive(); };
    rec.onaudiostart = () => { talkDiag('engine: audiostart'); alive(); };
    rec.onsoundstart = () => { talkDiag('engine: soundstart'); alive(); };
    rec.onspeechstart = () => { talkDiag('engine: speechstart'); alive(); };

    rec.onresult = (e: SpeechResultEventLike) => {
      alive();
      let last = '';
      for (let i = 0; i < e.results.length; i++) {
        const r = e.results[i];
        if (r.isFinal && r[0]?.transcript) last = r[0].transcript;
      }
      talkDiag(`engine: result final="${last}" interim=${e.results.length > 0 && !e.results[e.results.length - 1].isFinal}`);
      if (recCancelRef.current) return; // отменено — не вставляем
      if (last) onResultRef.current(last);
    };

    rec.onend = () => {
      talkDiag('engine: end');
      clearWatchdog();
      // Движок отработал — ссылку гасим: по ней startMic отличает живое распознавание
      // от залипшего флага
      if (recognitionRef.current === rec) recognitionRef.current = null;
      setListening(false);
      onEndRef.current?.();
    };
    rec.onerror = (e: { error?: string }) => {
      const code = String(e?.error ?? 'unknown');
      talkDiag('engine: error', code);
      clearWatchdog();
      setListening(false);
      onErrorRef.current?.(code);
      // Причина сбоя — прямо в тост: без неё на устройстве не понять, что именно не так.
      // В quiet-режиме тосты ведёт вызывающий (петля разговора их демпфирует)
      if (quietRef.current?.() || isSilentSpeechError(code)) return;
      showToast('Голосовой ввод', `Не удалось: ${describeSpeechError(code)}`);
    };

    recognitionRef.current = rec;
    try {
      rec.start();
      talkDiag('engine: start() called');
      setListening(true);
      // Детектор «мёртвого» движка: если за MIC_WATCHDOG_MS не пришёл audiostart —
      // распознавания в браузере нет (нет Google-сервисов). Переходим на клавиатурный
      // ввод и запоминаем выбор, чтобы впредь сразу открывать клавиатуру.
      micWatchdogRef.current = window.setTimeout(() => {
        if (gotAudio) return;
        micWatchdogRef.current = null;
        talkDiag('engine: watchdog — признаков жизни нет, считаем движок мёртвым');
        try { rec.abort(); } catch { /* noop */ }
        setListening(false);
        setMicKeyboardFallback();
        // Синтетический код: режиму разговора мёртвый движок — повод немедленно выйти
        // из петли с сообщением, а не молча уехать в клавиатурный фолбэк
        onErrorRef.current?.('mic-dead');
        if (!quietRef.current?.()) showToast('Голосовой ввод', MIC_FALLBACK_TEXT);
      }, MIC_WATCHDOG_MS);
    } catch {
      setListening(false);
    }
  }, [setListening]);

  const stopMic = useCallback((confirm: boolean) => {
    recCancelRef.current = !confirm;
    if (micWatchdogRef.current !== null) { clearTimeout(micWatchdogRef.current); micWatchdogRef.current = null; }
    setListening(false); // фикс: закрываем режим записи сразу, не дожидаясь onend (его может не быть)
    const rec = recognitionRef.current;
    // Ссылку снимаем здесь же: onend может не прийти вовсе, а по ней startMic решает,
    // живое ли распознавание — иначе следующий тап по микрофону упрётся в гвард
    recognitionRef.current = null;
    try {
      if (confirm) rec?.stop();
      else rec?.abort();
    } catch { /* noop */ }
  }, [setListening]);

  return { hasSpeech, isListening, recSeconds, startMic, stopMic };
}
