import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node: localStorage и window — минимальные заглушки
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
const dispatched: Event[] = [];
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = { dispatchEvent: (e: Event) => { dispatched.push(e); return true; } };

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { audioApi, type AudioModelInfo, type AudioOp, type AudioParamField, type AudioProvider, type AudioQuote, type AudioThread } from '../api';
import { TO_END } from '../player/selection';
import { __resetAudioStore, getSelection, requestOperation, setSelection } from '../thread/threadStore';
import { DEFAULT_INPUTS, type PanelInputs } from './inputs';
import { modelOptions, nextSettings, panelOps, runReason, type PanelState, type ReasonInput } from './model';
import { heavyWarning, instrumentalBlocked, insertSection, licenseWarning, lyricsToSend, musicReason, YUE2 } from './music';
import { MusicFields } from './MusicFields';
import type { OpFieldsProps } from './OpFields';
import { __resetOperationRequests, pendingOperation, takeOperation } from './opRequest';
import { commitPiece, pieceFromText, pieceText } from './piece';
import { PieceField } from './PieceField';
import { jobInput, runPanel, trimSteps, trimWithPiece } from './run';

const caps = (ops: AudioOp[], extra: Partial<AudioModelInfo['caps']> = {}): AudioModelInfo['caps'] => ({
  ops, languages: ['ru', 'en'], voiceKinds: [], producesFiles: ['audio'], license: { label: 'MIT', kind: 'permissive' },
  priceUnit: 'free', maxTextChars: 5000, minDurationSec: 10, maxDurationSec: 240, ...extra,
});
const ACE: AudioModelInfo = {
  id: 'ace-step-1.5-xl', label: 'ACE-Step 1.5 XL',
  caps: caps(['song', 'cover', 'repaint', 'extract', 'lego', 'complete'], { languages: ['ru', 'en', 'de'], heavyOps: ['extract', 'lego', 'complete'] }),
};
const YUE: AudioModelInfo = {
  id: YUE2, label: 'YuE2-3B', caps: caps(['song', 'cover'], { license: { label: 'CC BY-NC 4.0', kind: 'nonCommercial' } }),
};
const STABLE: AudioModelInfo = { id: 'stable', label: 'Stable Audio', caps: caps(['song'], { languages: [], languageNeutral: true }) };
const LOCAL: AudioProvider = { key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null, models: [ACE, YUE] };

const TRACKS = ['vocals', 'backing_vocals', 'drums', 'bass', 'guitar', 'keyboard', 'percussion', 'strings', 'synth', 'fx', 'brass', 'woodwinds'];
const F: Record<string, AudioParamField> = {
  bpm: { key: 'bpm', type: 'integer', title: 'Темп', default: 120, min: 40, max: 220 },
  key: { key: 'key', type: 'string', title: 'Тональность', default: 'C major', enum: ['C major', 'A minor'] },
  strength: { key: 'strength', type: 'number', default: 0.6, min: 0, max: 1 },
  track: { key: 'track', type: 'string', default: 'vocals', enum: TRACKS },
  tracks: { key: 'tracks', type: 'array', default: ['drums', 'bass'], items: { key: 'item', type: 'string', enum: TRACKS } },
  abc: { key: 'abc', type: 'string', maxLength: 20000 },
};

const st = (op: AudioOp, model: AudioModelInfo | null = ACE, fields: Record<string, unknown> = {}): PanelState => ({
  mode: 'music', op, provider: LOCAL, model, count: 1, providerKey: 'local', modelId: model?.id ?? 'auto', fields,
});

const inputs = (over: Partial<PanelInputs> = {}): PanelInputs => ({ ...DEFAULT_INPUTS, ...over });

const thread = (): AudioThread => ({
  id: 't1', file: 'song.mp3', lineage: [], draftFolder: null, createdAt: '', versions: [], currentVersionId: 'v1', launches: [], settings: null,
});

const PIECE = { sessionId: 's1', threadId: 't1', versionId: 'v1' };

function render(op: AudioOp, model: AudioModelInfo | null, main: AudioParamField[], over: Partial<OpFieldsProps> = {}) {
  const props: OpFieldsProps = {
    state: st(op, model), personal: false, main, values: {}, setField: () => {}, inputs: inputs(), setInputs: () => {},
    reference: null, setReference: () => {}, onOp: () => {}, isMobile: false, piece: PIECE, ...over,
  };
  return renderToStaticMarkup(createElement(MusicFields, props));
}

beforeEach(() => {
  dispatched.length = 0;
  localStorage.clear();
  __resetAudioStore();
  __resetOperationRequests();
});

describe('музыка: операции и их поля', () => {
  it('пилюли режима — песня, кавер, кусок, продолжение, дорожки, доаранжировка, эффект', () => {
    expect(panelOps('music').map(o => o.op)).toEqual(['song', 'cover', 'repaint', 'outpaint', 'extract', 'lego', 'complete', 'sfx']);
  });

  it('песня у ACE: слова с секциями, длительность, поля схемы; язык вокала — в «Ещё настройки»', () => {
    const html = render('song', ACE, [F.bpm, F.key]);
    expect(html).toContain('data-field="lyrics"');
    expect(html).toContain('[Verse]');
    expect(html).toContain('[Chorus]');
    expect(html).not.toContain('data-field="language"');
    expect(html).toContain('data-field="duration"');
    expect(html).toContain('как у модели · 10–240');
    expect(html).toContain('data-param="bpm"');
    expect(html).toContain('data-param="key"');
    expect(html).not.toContain('data-field="piece"');
  });

  it('песня у YuE2: «Инструментал» серый с причиной, без языка вокала, с подсказкой про ABC', () => {
    const html = render('song', YUE, [F.abc]);
    expect(html).toMatch(/data-opt="lyrics:off"[^>]*data-disabled="true"/);
    expect(html).toContain('YuE2 поёт только со словами');
    expect(html).not.toContain('data-field="language"');
    expect(html).toContain('data-param="abc"');
    expect(html).toContain('партитуру .abc');
  });

  it('инструментальная модель — без слов вовсе', () => {
    expect(render('song', STABLE, [])).not.toContain('data-field="lyrics"');
  });

  it('кавер — сила и необязательные слова; у YuE2 слова обязательны', () => {
    const html = render('cover', ACE, [F.strength]);
    expect(html).toContain('data-param="strength"');
    expect(html).toContain('Слова (необязательно)');
    expect(html).not.toContain('data-field="duration"');
    expect(render('cover', YUE, [])).toContain('поются на мелодию исходника');
  });

  it('перегенерировать кусок — поле «Кусок» и слова для куска; без нити — подсказка', () => {
    const html = render('repaint', ACE, []);
    expect(html).toContain('data-field="piece"');
    expect(html).toContain('Слова для куска');
    expect(render('repaint', ACE, [], { piece: null })).not.toContain('data-field="piece"');
  });

  it('вытащить и дописать дорожку — 12 пилюль, одна выбрана; доаранжировать — набор, по умолчанию барабаны и бас', () => {
    const one = render('extract', ACE, [F.track]);
    expect(one.match(/data-opt="track:/g)).toHaveLength(12);
    expect(one).toMatch(/data-opt="track:vocals" data-on="true"/);
    expect(one).toContain('Обработка → Стемы');
    expect(render('lego', ACE, [F.track])).toMatch(/data-opt="track:vocals" data-on="true"/);
    const many = render('complete', ACE, [F.tracks]);
    expect(many.match(/data-on="true"/g)).toHaveLength(2);
    expect(many).toMatch(/data-opt="track:drums" data-on="true"/);
    expect(many).toMatch(/data-opt="track:bass" data-on="true"/);
    expect(many).not.toContain('data-param="tracks"');
  });

  it('слова и длительность доезжают до запуска; инструментал слов не шлёт, у YuE2 «инструментал» не действует', () => {
    const base = { scope: 'p1', sessionId: 's1', thread: thread(), fields: {}, reference: null, text: 'synthwave', piece: null };
    const song = jobInput({ ...base, state: st('song'), inputs: inputs({ lyrics: '[Verse]\nПривет', durationSec: 60 }) }, 'q1');
    expect(song).toMatchObject({ prompt: 'synthwave', lyrics: '[Verse]\nПривет', durationSec: 60 });
    expect(jobInput({ ...base, state: st('song'), inputs: inputs({ lyrics: 'слова', instrumental: true }) }, 'q1').lyrics).toBeNull();
    expect(lyricsToSend('song', YUE, { lyrics: 'слова', instrumental: true, durationSec: null })).toBe('слова');
    // Длительность — только там, где её задают
    expect(jobInput({ ...base, state: st('cover'), inputs: inputs({ durationSec: 60 }) }, 'q1').durationSec).toBeNull();
  });

  it('причины отказа: YuE2 без слов, кусок не задан, длина вне пределов, пустой набор инструментов', () => {
    const none = { lyrics: '', instrumental: false, durationSec: null };
    expect(musicReason({ op: 'song', model: YUE, inputs: none, piece: null, fields: {} })).toMatch(/YuE2 поёт по словам/);
    expect(musicReason({ op: 'song', model: ACE, inputs: none, piece: null, fields: {} })).toBeNull();
    expect(musicReason({ op: 'repaint', model: ACE, inputs: none, piece: null, fields: {} })).toMatch(/Задайте кусок/);
    expect(musicReason({ op: 'repaint', model: ACE, inputs: none, piece: { start: 1, end: 3 }, fields: {} })).toBeNull();
    expect(musicReason({ op: 'song', model: ACE, inputs: { ...none, durationSec: 300 }, piece: null, fields: {} })).toBe('Длительность — от 10 до 240 с');
    expect(musicReason({ op: 'complete', model: ACE, inputs: none, piece: null, fields: { tracks: [] } })).toMatch(/инструмент/);
    // Причина доезжает до низа панели
    const r: ReasonInput = {
      sessionId: 's1', thread: thread(), state: st('repaint'), provider: { key: 'local', label: 'Локально', unit: '', disabled: false, reason: null, locked: false },
      text: '', hasReference: false, hasVoiceModel: false, clips: 0, replicas: 0, trimReady: false, pieces: 0, quoteError: null,
      music: none, piece: null,
    };
    expect(runReason(r)).toMatch(/Задайте кусок/);
  });

  it('секция вставляется с новой строки', () => {
    expect(insertSection('', '[Verse]')).toBe('[Verse]\n');
    expect(insertSection('строка\n', '[Chorus]')).toBe('строка\n\n[Chorus]\n');
  });
});

describe('музыка: лицензия и тяжёлая операция — до запуска', () => {
  it('у YuE2 видно CC BY-NC: значком модели и предупреждением', () => {
    expect(licenseWarning(YUE)).toMatch(/CC BY-NC 4\.0: только некоммерческое/);
    expect(licenseWarning(ACE)).toBeNull();
    expect(modelOptions(LOCAL, 'song', 'auto').find(m => m.id === YUE2)?.license).toBe('CC BY-NC 4.0');
    expect(instrumentalBlocked('song', YUE)).not.toBeNull();
    expect(instrumentalBlocked('song', ACE)).toBeNull();
  });

  it('тяжёлая — только у операций из heavyOps модели', () => {
    expect(heavyWarning('extract', ACE)).toMatch(/одна за раз/);
    expect(heavyWarning('song', ACE)).toBeNull();
  });
});

describe('кусок ⇄ волна', () => {
  it('поле → волна: вписанное время становится выделением нити', () => {
    commitPiece('s1', 't1', 'v1', { start: '1:02.5', end: '70', toEnd: false });
    expect(getSelection('s1', 't1')).toEqual({ start: 62.5, end: 70, versionId: 'v1' });
    commitPiece('s1', 't1', 'v1', { start: '3', end: '', toEnd: true });
    expect(getSelection('s1', 't1')).toEqual({ start: 3, end: TO_END, versionId: 'v1' });
    // Недобор при наборе выделение не трогает, пустые поля — снимают
    commitPiece('s1', 't1', 'v1', { start: '1:', end: '', toEnd: false });
    expect(getSelection('s1', 't1')).toEqual({ start: 3, end: TO_END, versionId: 'v1' });
    commitPiece('s1', 't1', 'v1', { start: '', end: '', toEnd: false });
    expect(getSelection('s1', 't1')).toBeNull();
  });

  it('волна → поле: выделение на волне показывается в «Куске»', () => {
    setSelection('s1', 't1', { start: 4.2, end: 9.8, versionId: 'v1' });
    expect(pieceText(getSelection('s1', 't1'))).toEqual({ start: '4.2', end: '9.8', toEnd: false });
    const html = renderToStaticMarkup(createElement(PieceField, { binding: PIECE }));
    expect(html).toContain('value="4.2"');
    expect(html).toContain('value="9.8"');
    expect(html).toContain('data-piece-linked');
    expect(pieceFromText({ start: '5', end: '2', toEnd: false })).toBeUndefined();
  });

  it('кусок уходит в запуск: repaint — секундами (до конца = −1), обрезка — без конца', () => {
    const sel = { start: 2, end: TO_END };
    const base = { scope: 'p1', sessionId: 's1', thread: thread(), fields: {}, reference: null, text: 'гитара', inputs: inputs() };
    expect(jobInput({ ...base, state: st('repaint'), piece: sel }, 'q1')).toMatchObject({ startSec: 2, endSec: TO_END });
    expect(jobInput({ ...base, state: st('song'), piece: sel }, 'q1').startSec).toBeUndefined();
    expect(trimSteps(trimWithPiece(DEFAULT_INPUTS.trim, { start: 1, end: 4 }))[0]).toMatchObject({ op: 'trim', startSec: 1, endSec: 4 });
    expect(trimSteps(trimWithPiece(DEFAULT_INPUTS.trim, sel))[0]).toMatchObject({ startSec: 2, endSec: null });
    expect(trimSteps(trimWithPiece({ ...DEFAULT_INPUTS.trim, start: 7, end: 9 }, null))).toEqual([]);
  });
});

describe('просьба карточки к панели', () => {
  it('«Перегенерировать кусок» отрабатывается ровно один раз; новая просьба — снова', () => {
    requestOperation('s1', 't1', 'repaint');
    expect(pendingOperation('s1')).toMatchObject({ op: 'repaint', threadId: 't1' });
    expect(pendingOperation('s1')).not.toBeNull();
    expect(takeOperation('s1')).toMatchObject({ op: 'repaint', seq: 1 });
    expect(takeOperation('s1')).toBeNull();
    expect(pendingOperation('s1')).toBeNull();
    requestOperation('s1', 't1', 'trim');
    expect(takeOperation('s1')).toMatchObject({ op: 'trim', seq: 2 });
    expect(takeOperation('s1')).toBeNull();
    expect(takeOperation('s2')).toBeNull();
  });

  it('операция из другого режима переключает режим вместе с ней', () => {
    const voice: PanelState = { ...st('song'), mode: 'voice', op: 'speak' };
    expect(nextSettings(voice, { mode: 'music', operation: 'repaint' })).toMatchObject({ mode: 'music', operation: 'repaint' });
    expect(nextSettings(voice, { mode: 'process', operation: 'trim' })).toMatchObject({ mode: 'process', operation: 'trim' });
    // Чужая режиму операция не протаскивается
    expect(nextSettings(voice, { mode: 'music', operation: 'trim' })).toMatchObject({ mode: 'music', operation: 'song' });
  });
});

describe('котировка запуска', () => {
  // Сервер сверяет запуск с котировкой: подводка, слова и длительность обязаны совпасть, иначе отказ
  it('песня котируется тем же, что уходит в задачу', async () => {
    const quote = vi.spyOn(audioApi, 'quote').mockResolvedValue({ quoteId: 'q1' } as AudioQuote);
    const start = vi.spyOn(audioApi, 'startJob').mockResolvedValue({ jobId: 'j1' });
    const ok = await runPanel({
      scope: 'p1', sessionId: 's1', thread: thread(), fields: {}, reference: null, text: ' synthwave ', piece: null,
      state: st('song'), inputs: inputs({ lyrics: '[Verse]\nПривет', durationSec: 60 }),
    });
    expect(ok).toBe(true);
    const q = quote.mock.calls[0][2];
    const job = start.mock.calls[0][2];
    expect(job.quoteId).toBe('q1');
    expect({ text: q.text, prompt: q.prompt, lyrics: q.lyrics, durationSec: q.durationSec })
      .toEqual({ text: job.text, prompt: job.prompt, lyrics: job.lyrics, durationSec: job.durationSec });
    expect(q).toMatchObject({ prompt: 'synthwave', lyrics: '[Verse]\nПривет', durationSec: 60 });
    vi.restoreAllMocks();
  });
});
