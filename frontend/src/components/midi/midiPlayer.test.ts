import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as ToneNs from 'tone';
import type { MidiDoc, MidiTrackView } from './midiModel';
import { getState, pause, play, seek, setToneLoaderForTests, stop, subscribe, type MidiPlayerState } from './midiPlayer';

// Фейковый Tone: записывает созданные узлы, разборы и запланированные ноты

type FakeNode = { kind: string; disposed: boolean };

function makeFakeTone() {
  const nodes: FakeNode[] = [];
  const parts: { values: unknown[]; disposed: boolean }[] = [];
  const transport = {
    seconds: 0,
    state: 'stopped' as string,
    start: vi.fn(() => { transport.state = 'started'; }),
    pause: vi.fn(() => { transport.state = 'paused'; }),
    stop: vi.fn(() => { transport.state = 'stopped'; transport.seconds = 0; }),
    cancel: vi.fn(),
  };

  class Node {
    disposed = false;
    volume = { value: 0 };
    constructor(public kind: string) { nodes.push(this); }
    connect() { return this; }
    triggerAttackRelease() { return this; }
    dispose() { this.disposed = true; }
  }

  const Tone = {
    start: vi.fn(async () => {}),
    getDestination: () => ({ kind: 'destination' }),
    getTransport: () => transport,
    Synth: class {},
    PolySynth: class extends Node { constructor() { super('poly'); } },
    Filter: class extends Node { constructor() { super('filter'); } },
    MembraneSynth: class extends Node { constructor() { super('membrane'); } },
    NoiseSynth: class extends Node { constructor() { super('noise'); } },
    Part: class {
      disposed = false;
      constructor(_cb: unknown, public values: unknown[]) { parts.push(this); }
      start() { return this; }
      dispose() { this.disposed = true; }
    },
  };
  return { Tone: Tone as unknown as typeof ToneNs, nodes, parts, transport };
}

function track(index: number, family: MidiTrackView['family'], pitches: number[]): MidiTrackView {
  return {
    index,
    name: `t${index}`,
    channel: family === 'drums' ? 9 : index,
    isDrums: family === 'drums',
    program: 0,
    family,
    notes: pitches.map((midi, i) => ({ midi, time: i * 0.5, duration: 0.5, velocity: 0.8 })),
  };
}

function doc(tracks: MidiTrackView[]): MidiDoc {
  return { durationSec: 4, bpm: 120, timeSig: [4, 4], bars: [0, 2], tracks, minPitch: 36, maxPitch: 72, noteCount: 0 };
}

let rafQueue: (() => void)[] = [];
function flushRaf() {
  const q = rafQueue;
  rafQueue = [];
  for (const cb of q) cb();
}

let fake: ReturnType<typeof makeFakeTone>;
let loader: ReturnType<typeof vi.fn>;

beforeEach(() => {
  rafQueue = [];
  vi.stubGlobal('requestAnimationFrame', (cb: () => void) => rafQueue.push(cb));
  vi.stubGlobal('cancelAnimationFrame', () => { rafQueue = []; });
  fake = makeFakeTone();
  loader = vi.fn(async () => fake.Tone);
  setToneLoaderForTests(loader as unknown as () => Promise<typeof ToneNs>);
});

afterEach(() => {
  setToneLoaderForTests(null);
  vi.unstubAllGlobals();
});

describe('midiPlayer', () => {
  it('без play Tone не загружается', () => {
    pause('a');
    stop('a');
    seek('a', 1);
    getState();
    subscribe(() => {})();
    expect(loader).not.toHaveBeenCalled();
  });

  it('play другого владельца останавливает первого', async () => {
    // Один и тот же документ: смену владельца должен ловить именно ownerId
    const d = doc([track(0, 'piano', [60, 62])]);
    await play('a', d);
    const firstBank = fake.nodes.slice();
    expect(getState()).toMatchObject({ ownerId: 'a', playing: true });

    await play('b', d);
    expect(firstBank.length).toBeGreaterThan(0);
    expect(firstBank.every(n => n.disposed)).toBe(true);
    expect(fake.transport.stop).toHaveBeenCalled();
    expect(getState()).toMatchObject({ ownerId: 'b', playing: true });
    expect(loader).toHaveBeenCalledTimes(1);
  });

  it('stop разбирает синты', async () => {
    await play('a', doc([track(0, 'strings', [60]), track(1, 'drums', [36, 38])]));
    expect(fake.nodes.map(n => n.kind).sort()).toEqual(['filter', 'membrane', 'noise', 'poly']);

    stop('a');
    expect(fake.nodes.every(n => n.disposed)).toBe(true);
    expect(fake.parts.every(p => p.disposed)).toBe(true);
  });

  it('заглушённые дорожки не планируются', async () => {
    const d = doc([track(0, 'piano', [60, 64]), track(3, 'lead', [72]), track(5, 'drums', [36])]);
    await play('a', d, { muted: new Set([3]) });
    const planned = fake.parts.flatMap(p => p.values);
    expect(planned).toEqual([...d.tracks[0].notes, ...d.tracks[2].notes]);
  });

  // Tone.start ждёт ручного release; startCalled — play дошёл до Tone.start
  function deferToneStart() {
    let release!: () => void;
    let started!: () => void;
    const startCalled = new Promise<void>(r => { started = r; });
    fake.Tone.start = vi.fn(() => {
      started();
      return new Promise<void>(r => { release = r; });
    }) as unknown as typeof fake.Tone.start;
    return { startCalled, release: () => release() };
  }

  it('stop во время ожидания Tone.start отменяет play', async () => {
    const gate = deferToneStart();
    const playing = play('a', doc([track(0, 'piano', [60])]));
    stop('a');
    await gate.startCalled;
    gate.release();
    await playing;

    expect(loader).toHaveBeenCalledTimes(1);
    expect(fake.parts).toEqual([]);
    expect(fake.nodes).toEqual([]);
    expect(fake.transport.start).not.toHaveBeenCalled();
    expect(getState()).toEqual({ ownerId: null, playing: false, positionSec: 0 });
  });

  it('stop чужого владельца не отменяет ожидающий play', async () => {
    const gate = deferToneStart();
    const playing = play('a', doc([track(0, 'piano', [60])]));
    stop('b');
    await gate.startCalled;
    gate.release();
    await playing;

    expect(fake.transport.start).toHaveBeenCalledTimes(1);
    expect(getState()).toEqual({ ownerId: 'a', playing: true, positionSec: 0 });
  });

  it('другой документ того же владельца играет с начала, а не с паузы', async () => {
    const docA = doc([track(0, 'piano', [60])]);
    const docB = doc([track(0, 'piano', [62])]);

    await play('a', docA);
    fake.transport.seconds = 2;
    pause('a');
    await play('a', docB);
    expect(getState()).toEqual({ ownerId: 'a', playing: true, positionSec: 0 });

    // Контраст: тот же документ после паузы продолжает с места
    fake.transport.seconds = 2;
    pause('a');
    await play('a', docB);
    expect(getState()).toEqual({ ownerId: 'a', playing: true, positionSec: 2 });
  });

  it('getState и subscribe отражают play, pause и stop', async () => {
    const seen: MidiPlayerState[] = [];
    const unsubscribe = subscribe(s => seen.push(s));
    const d = doc([track(0, 'keys', [60])]);

    await play('a', d, { fromSec: 1 });
    expect(seen.at(-1)).toEqual({ ownerId: 'a', playing: true, positionSec: 1 });

    fake.transport.seconds = 1.5;
    flushRaf();
    expect(getState()).toEqual({ ownerId: 'a', playing: true, positionSec: 1.5 });

    fake.transport.seconds = 2;
    pause('a');
    expect(seen.at(-1)).toEqual({ ownerId: 'a', playing: false, positionSec: 2 });
    expect(fake.transport.pause).toHaveBeenCalled();

    // Продолжение после паузы — с той же позиции
    await play('a', d);
    expect(getState()).toEqual({ ownerId: 'a', playing: true, positionSec: 2 });

    // Конец трека: автостоп и позиция 0
    fake.transport.seconds = d.durationSec;
    flushRaf();
    expect(getState()).toEqual({ ownerId: 'a', playing: false, positionSec: 0 });

    stop('a');
    expect(seen.at(-1)).toEqual({ ownerId: null, playing: false, positionSec: 0 });

    const count = seen.length;
    unsubscribe();
    await play('a', d);
    expect(seen.length).toBe(count);
  });
});
