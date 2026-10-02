// Моки по контрактам ADR-022 (раздел «Контракты») — для юнит-тестов и спек e2e. В бандл remote не
// попадают: их импортируют только тесты.

import type {
  FilmState, VideoCatalog, VideoClipVersion, VideoPrefs, VideoQuote, VideoScene, VideoThreadsState,
} from './api';

export const NOW = '2026-10-02T15:00:00Z';

export const CATALOG: VideoCatalog = {
  providers: [
    {
      key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true,
      models: [{ id: 'minimax-h3', label: 'MiniMax H3', durations: [5, 10], aspects: ['16:9', '9:16'], sound: true, lastFrame: true }],
    },
    {
      key: 'fal', label: 'fal.ai', priceUnit: 'usd', available: true,
      models: [
        { id: 'veo-3.1', label: 'Veo 3.1', durations: [4, 6, 8], aspects: ['16:9', '9:16'], sound: true, lastFrame: true, license: 'watermark' },
        { id: 'seedance-2.5', label: 'Seedance 2.5', durations: [5, 10], aspects: ['16:9', '1:1'], sound: false, lastFrame: true },
        { id: 'no-last', label: 'Без кадра B', durations: [5], aspects: ['16:9'], sound: false, lastFrame: false },
      ],
    },
    {
      key: 'higgsfield', label: 'Higgsfield', priceUnit: 'credits', available: false, reason: 'Нет доступа к Higgsfield',
      models: [{ id: 'kling3_0', label: 'Kling 3.0', durations: [5, 10], aspects: ['16:9'], sound: false, lastFrame: true, license: 'commercial' }],
    },
  ],
  autoModelId: 'auto',
  maxCount: 4,
  autoProviders: ['local', 'fal', 'higgsfield'],
};

export const PREFS: VideoPrefs = { provider: 'fal', model: 'veo-3.1', durationSec: 8, aspect: '16:9', sound: false, count: 1 };

export const version = (n: number, extra: Partial<VideoClipVersion> = {}): VideoClipVersion => ({
  versionId: `ver-${n}`, number: n, jobId: 'job-7', variant: n - 1, provider: 'fal', model: 'veo-3.1', durationSec: 8,
  sizeBytes: 5_242_880, hasSound: true, license: 'watermark', cost: { currency: 'usd', amount: 1.6 }, initiator: 'human',
  inputs: { text: 'Камера медленно приближается к окну', frameA: 'file:video/утро/кадры/кадр-2.png', frameB: 'file:video/утро/кадры/кадр-3.png' },
  createdAt: NOW, ...extra,
});

export const scene = (id: string, extra: Partial<VideoScene> = {}): VideoScene => ({
  sceneId: id, name: `Сцена ${id.replace(/\D/g, '') || '1'}`, folder: 'video/утро',
  settings: {
    frameA: { kind: 'file', path: 'video/утро/кадры/кадр-2.png' },
    frameB: { kind: 'file', path: 'video/утро/кадры/кадр-3.png' },
    text: 'Камера медленно приближается к окну', provider: 'fal', model: 'veo-3.1', durationSec: 8, aspect: '16:9', sound: true, count: 2,
  },
  versions: [], launches: [], savedFiles: [], createdAt: NOW, ...extra,
});

export const threads = (revision: number, scenes: VideoScene[], focus: VideoThreadsState['focus'] = {}): VideoThreadsState =>
  ({ revision, scenes, focus });

export const QUOTE: VideoQuote = {
  quoteId: 'q-1', provider: 'fal', model: 'veo-3.1', count: 2, durationSec: 8,
  price: { amount: 3.2, unit: 'usd', approx: true, source: 'pricing', eta: 240, queueLength: 1 },
  license: 'watermark', heavy: false, expiresAt: '2026-10-02T15:10:00Z',
};

export const FILM_PATH = 'video/утро/утро.film';

export const film = (extra: Partial<FilmState> = {}): FilmState => ({
  path: FILM_PATH,
  revision: '9f2c',
  document: {
    schema: 1, aspect: '16:9',
    items: [
      { file: 'video/утро/scene-01.mp4', trim: [0, 8], scene: { text: 'Рассвет над горами', frameA: 'a.png', frameB: 'b.png', provider: 'fal', model: 'veo-3.1', durationSec: 8 } },
      { file: 'video/утро/scene-02.mp4', trim: [0, 8], scene: { text: 'Камера у окна', frameA: 'b.png', frameB: 'c.png', provider: 'fal', model: 'veo-3.1', durationSec: 8 } },
      { file: 'video/утро/scene-03.mp4', trim: [0.5, 7.5], scene: { text: 'Выход на террасу', frameA: 'c.png', frameB: 'd.png', provider: 'local', model: 'minimax-h3', durationSec: 10 } },
    ],
    cuts: [{ type: 'butt', sec: 0 }, { type: 'dissolve', sec: 1 }],
    music: { file: 'music/утро.mp3', volume: 60, fadeOut: 4 },
    builds: [],
  },
  spent: { usd: 6.4, credits: 0, gpuSeconds: 300 },
  marks: [{ index: 1, claude: true, updated: false, stale: false }],
  ...extra,
});
