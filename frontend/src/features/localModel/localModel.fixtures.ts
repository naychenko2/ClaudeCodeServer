// Фикстуры «Локальные модели»: моки состояний для тестов и витрины (не рантайм —
// USE_MOCK=false, живые вызовы идут через lib/api.ts). Данные — с макета
// (docs/mockups/local-model-stats-panel.html): имена, скорости, температуры и
// мощность — те же, чтобы скриншоты сравнивались один в один.

import type { LocalModelMedia, LocalModelStats } from '../../lib/api';

// 'working' — движок жив, модель загружена, сосед в очереди (дефолт);
// 'busy' — занята: очередь, вытеснения, горячие карты (состояние макета «Занята + вытеснения»);
// 'down' — стенд не отвечает: последние известные данные, downSince;
// 'off' — «не подключена»: провайдер в настройках есть (configured: true — иначе
// панель была бы скрыта целиком), но движок не поднят — пустое состояние с
// кнопкой в «Поставщики моделей».
export type LocalModelMockState = 'working' | 'busy' | 'down' | 'off';

// Переключатель состояний для проверки глазами: ?lmMock=busy|down|off (дефолт — working).
// window.location — только в браузере; вне DOM (тесты, SSR) — дефолт.
export function mockStateFromQuery(): LocalModelMockState {
  try {
    const v = new URLSearchParams(window.location.search).get('lmMock');
    if (v === 'busy' || v === 'down' || v === 'off') return v;
  } catch { /* не-DOM-окружение */ }
  return 'working';
}

export function mockLocalModelStats(state: LocalModelMockState = 'working'): LocalModelStats {
  if (state === 'off') {
    // configured: true — провайдер задан в настройках, иначе панель была бы скрыта
    // целиком (isLocalModelPanelVisible требует configured); статус неConnected —
    // движок не поднят, пустое состояние с кнопкой в «Поставщики моделей»
    return { configured: true, status: 'notConnected' };
  }
  // working: цифры макета «Заполненное: работает» (2 идёт, 0 ждут, 38/1240 ток/с)
  const base: LocalModelStats = {
    configured: true,
    engine: 'vLLM',
    model: 'Qwen3.5-32B',
    status: 'working',
    running: 2,
    waiting: 0,
    othersCount: 1,
    speed: { genPerStream: 38, prefillPerStream: 1240 },
    // доли 0..1: 0.61 = 61 %, 0.39 = 39 % (11,8/30 ГиБ из макета)
    kv: { usagePerc: 0.61, ramTierPerc: 0.39, prefixHitPerc: 0.87, ramTierHitPerc: 0.52, preemptions15m: 0 },
    gpus: [
      { index: 0, name: 'RTX 3090', utilPerc: 71, memUsedMb: 21934, memTotalMb: 24576, tempC: 68, powerW: 176, powerLimitW: 220 },
      { index: 1, name: 'RTX 3090', utilPerc: 69, memUsedMb: 21710, memTotalMb: 24576, tempC: 64, powerW: 171, powerLimitW: 220 },
    ],
    history: [],
    chats: [
      { own: true, title: 'Рефакторинг сторожа', projectId: 'proj-ai-home', phase: 'generating', genTps: 38 },
      { own: false, title: 'Чат: тесты редактора картинок', phase: 'reading' },
    ],
  };
  if (state === 'working') return base;
  if (state === 'busy') {
    // цифры макета «Занята + вытеснения» (3 идёт, 2 ждут, 21/860, 94 %, 7 вытеснений)
    return {
      ...base,
      status: 'busy',
      running: 3,
      waiting: 2,
      othersCount: 2,
      speed: { genPerStream: 21, prefillPerStream: 860 },
      // 0.91 = 91 % (27,4/30 ГиБ из макета)
      kv: { usagePerc: 0.94, ramTierPerc: 0.91, prefixHitPerc: 0.64, ramTierHitPerc: 0.48, preemptions15m: 7 },
      gpus: [
        { index: 0, name: 'RTX 3090', utilPerc: 98, memUsedMb: 23654, memTotalMb: 24576, tempC: 81, powerW: 219, powerLimitW: 220 },
        { index: 1, name: 'RTX 3090', utilPerc: 96, memUsedMb: 23347, memTotalMb: 24576, tempC: 78, powerW: 216, powerLimitW: 220 },
      ],
      chats: [
        { own: true, title: 'Рефакторинг сторожа', projectId: 'proj-ai-home', phase: 'generating', genTps: 38 },
        { own: false, title: 'Батарея II — прогон 4', phase: 'generating', genTps: 24 },
        { own: false, title: 'Чат: тесты редактора картинок', phase: 'reading' },
        { own: false, title: 'Отчёт по продажам за сентябрь', phase: 'waiting' },
      ],
    };
  }
  // down: стенд лёг — последние известные данные (в макете «14:32» = 32 мин назад)
  return {
    ...base,
    status: 'down',
    downSince: Date.now() - 32 * 60_000, // unix-мс: 32 мин назад
    running: 0,
    waiting: 0,
    othersCount: 0,
    chats: [],
  };
}

// === Вкладка «Медиа» (ComfyUI) ===
// Состояния для тестов и витрины (USE_MOCK=false — в рантайм не участвуют).
// run — работает, идёт своя задача, очередь 2; heavy — занят: 4 из 4, тяжёлая;
// idle — карта свободна (нет ни идущего, ни очереди); models — узел сообщает
// несколько загруженных моделей (loadedModels: 5, длинные — обрезка + «ещё N»);
// down — не отвечает; nomem — ComfyUI жив, но gpus==null (узел не сообщает карту);
// off — выключен.
export type LocalModelMediaMockState = 'run' | 'heavy' | 'idle' | 'models' | 'down' | 'nomem' | 'off';

// Переключатель состояний для проверки глазами: ?lmMedia=heavy|idle|models|down|nomem|off.
export function mediaMockStateFromQuery(): LocalModelMediaMockState {
  try {
    const v = new URLSearchParams(window.location.search).get('lmMedia');
    if (v === 'heavy' || v === 'idle' || v === 'models' || v === 'down' || v === 'nomem' || v === 'off') return v;
  } catch { /* не-DOM-окружение */ }
  return 'run';
}

export function mockLocalModelMedia(state: LocalModelMediaMockState = 'run'): LocalModelMedia {
  const now = Date.now();
  const base: LocalModelMedia = {
    configured: true,
    comfy: { status: 'working', downSince: null, version: '0.4' },
    slots: { used: 1, limit: 4 },
    gpus: [
      { uuid: 'gpu-media-0', index: 0, name: 'RTX 3090', roles: ['media'], utilPerc: 97, memUsedMb: 22464, memTotalMb: 24576, tempC: 74, powerW: 331, powerLimitW: 350, mediaMemMb: 22464 },
    ],
    // Узел сообщает: модель идущего прогона лежит в его памяти
    loadedModels: ['MiniMax H3'],
    running: [
      { kind: 'video', op: 'Видео 5 с · MiniMax H3', heavy: false, own: true, sessionId: 's-media-1', projectId: 'proj-ai-home', title: null, foreign: null, steps: { step: 12, total: 25, stage: null }, percent: 48, percentExact: true, etaSeconds: 120, remainingSeconds: 120, startedAt: now - 300_000 },
    ],
    queue: [
      { position: 1, kind: 'image', op: 'Картинка · Qwen-Image 2.1', heavy: false, own: true, sessionId: 's-media-2', projectId: 'proj-ai-home', title: null, foreign: null, etaSeconds: 120, startInSeconds: 120 },
      { position: 2, kind: 'audio', op: 'Стемы · 4 дорожки', heavy: false, own: false, sessionId: null, projectId: null, title: 'Чат другого пользователя', foreign: null, etaSeconds: null, startInSeconds: null },
    ],
    lastHour: [
      { kind: 'video', op: 'Видео 5 с · MiniMax H3', own: true, outcome: 'completed', runSeconds: 340, etaSeconds: 330, finishedAt: now - 12 * 60_000 },
      { kind: 'image', op: 'Картинка · Qwen-Image 2.1', own: true, outcome: 'completed', runSeconds: 47, etaSeconds: 45, finishedAt: now - 18 * 60_000 },
      { kind: 'audio', op: 'Песня 2 мин · ACE-Step', own: false, outcome: 'completed', runSeconds: 151, etaSeconds: 90, finishedAt: now - 23 * 60_000 },
      { kind: 'video', op: 'Видео 10 с · MiniMax H3', own: true, outcome: 'failed', runSeconds: 72, etaSeconds: null, finishedAt: now - 30 * 60_000 },
    ],
    unsupported: [],
  };

  switch (state) {
    case 'run':
      return base;
    case 'heavy':
      return {
        ...base,
        comfy: { status: 'busy', downSince: null, version: '0.4' },
        slots: { used: 4, limit: 4 },
        gpus: base.gpus!.map(g => ({ ...g, utilPerc: 100, tempC: 79, powerW: 342 })),
        running: [
          { kind: 'video', op: 'Увеличение до 1440p · MiniMax H3', heavy: true, own: true, sessionId: 's-media-1', projectId: 'proj-ai-home', title: null, foreign: null, steps: { step: 5, total: 40, stage: 1 }, percent: 12, percentExact: true, etaSeconds: 720, remainingSeconds: 720, startedAt: now - 60_000 },
        ],
        queue: [
          { position: 1, kind: 'image', op: 'Картинка · Qwen-Image 2.1', heavy: false, own: true, sessionId: 's-media-2', projectId: 'proj-ai-home', title: null, foreign: null, etaSeconds: 720, startInSeconds: 720 },
          { position: 2, kind: 'audio', op: 'Речь · Qwen3-TTS', heavy: false, own: false, sessionId: null, projectId: null, title: 'Чат другого пользователя', foreign: null, etaSeconds: 780, startInSeconds: 780 },
          { position: 3, kind: 'audio', op: 'Обучение голоса · RVC', heavy: true, own: false, sessionId: null, projectId: null, title: 'Чат другого пользователя', foreign: null, etaSeconds: null, startInSeconds: null },
        ],
      };
    case 'idle':
      return {
        ...base,
        slots: { used: 0, limit: 4 },
        gpus: base.gpus!.map(g => ({ ...g, utilPerc: 0, tempC: 41, powerW: 28 })),
        running: [],
        queue: [],
      };
    case 'models':
      // Узел сообщает 5 загруженных моделей: строка — не более 3 + «ещё N»,
      // длинные имена — с многоточием (по ширине)
      return {
        ...base,
        slots: { used: 0, limit: 4 },
        loadedModels: [
          'MiniMax H3',
          'qwen_image.safetensors',
          'ACE-Step-v10-large.safetensors',
          'Stable-Cascade-bb-3B.safetensors',
          'RVC-base',
        ],
        running: [],
        queue: [],
      };
    case 'down':
      return {
        ...base,
        comfy: { status: 'down', downSince: now - 32 * 60_000, version: '0.4' },
      };
    case 'nomem':
      // Узел не сообщает: ни карты, ни моделей
      return {
        ...base,
        gpus: null,
        loadedModels: null,
      };
    case 'off':
      return {
        configured: false,
        comfy: { status: 'disabled', downSince: null, version: null },
        slots: { used: 0, limit: 4 },
        gpus: null,
        loadedModels: null,
        running: [],
        queue: [],
        lastHour: [],
        unsupported: [],
      };
  }
}
