import type * as ToneNs from 'tone';
import type { MidiDoc, MidiNote } from './midiModel';
import { createSynths, disposeSynths, playNote, type SynthBank } from './synthBank';

// Модульный стор «кто играет»: Tone.Transport один на страницу, поэтому и
// играющий владелец (просмотрщик) один. Tone грузится только по первому play.

type Tone = typeof ToneNs;
type ToneLoader = () => Promise<Tone>;

export type MidiPlayerState = { ownerId: string | null; playing: boolean; positionSec: number };
export type PlayOptions = { fromSec?: number; muted?: Set<number> };

type Current = {
  ownerId: string;
  doc: MidiDoc;
  bank: SynthBank;
  parts: { dispose(): unknown }[];
};

const IDLE: MidiPlayerState = { ownerId: null, playing: false, positionSec: 0 };

let loader: ToneLoader = () => import('tone');
let tone: Tone | null = null;
let current: Current | null = null;
let state: MidiPlayerState = IDLE;
let rafId: number | null = null;
// Каждая команда двигает счётчик: play, дождавшийся Tone после stop, — устарел
let seq = 0;
let pendingOwner: string | null = null;
const listeners = new Set<(s: MidiPlayerState) => void>();

function setState(next: MidiPlayerState): void {
  state = next;
  for (const fn of listeners) fn(state);
}

async function loadTone(): Promise<Tone> {
  if (!tone) tone = await loader();
  return tone;
}

function stopRaf(): void {
  if (rafId !== null) {
    globalThis.cancelAnimationFrame?.(rafId);
    rafId = null;
  }
}

function tick(): void {
  rafId = null;
  if (!tone || !current || !state.playing) return;
  const pos = tone.getTransport().seconds;
  if (pos >= current.doc.durationSec) {
    finish();
    return;
  }
  setState({ ...state, positionSec: pos });
  rafId = globalThis.requestAnimationFrame(tick);
}

function clearParts(): void {
  if (!current) return;
  for (const p of current.parts) p.dispose();
  current.parts = [];
}

// Конец трека: владелец и синты остаются, позиция в ноль
function finish(): void {
  stopRaf();
  if (tone) {
    const tr = tone.getTransport();
    tr.stop();
    tr.cancel(0);
  }
  clearParts();
  setState({ ownerId: current?.ownerId ?? null, playing: false, positionSec: 0 });
}

function teardown(): void {
  stopRaf();
  if (tone) {
    const tr = tone.getTransport();
    tr.stop();
    tr.cancel(0);
  }
  if (current) {
    clearParts();
    disposeSynths(current.bank);
    current = null;
  }
}

function schedule(Tone: Tone, cur: Current, muted: Set<number> | undefined): void {
  for (const track of cur.doc.tracks) {
    if (muted?.has(track.index) || track.notes.length === 0) continue;
    const family = track.family;
    const part = new Tone.Part<MidiNote>((time, note) => playNote(cur.bank, family, note, time), track.notes);
    part.start(0);
    cur.parts.push(part);
  }
}

export async function play(ownerId: string, doc: MidiDoc, opts: PlayOptions = {}): Promise<void> {
  const my = ++seq;
  pendingOwner = ownerId;
  const Tone = await loadTone();
  await Tone.start();
  if (my !== seq) return;
  pendingOwner = null;

  const tr = Tone.getTransport();
  const sameOwner = current?.ownerId === ownerId;
  const resumeFrom = sameOwner && current?.doc === doc ? state.positionSec : 0;

  if (current && (!sameOwner || current.doc !== doc)) {
    teardown();
  } else {
    stopRaf();
    tr.pause();
    tr.cancel(0);
    clearParts();
  }

  if (!current) {
    const families = doc.tracks.map(t => t.family);
    current = { ownerId, doc, bank: createSynths(Tone, families), parts: [] };
  }
  schedule(Tone, current, opts.muted);

  const from = Math.max(0, Math.min(opts.fromSec ?? resumeFrom, doc.durationSec));
  tr.seconds = from;
  tr.start();
  setState({ ownerId, playing: true, positionSec: from });
  rafId = globalThis.requestAnimationFrame(tick);
}

export function pause(ownerId: string): void {
  if (!tone || current?.ownerId !== ownerId || !state.playing) return;
  seq++;
  stopRaf();
  const tr = tone.getTransport();
  tr.pause();
  setState({ ownerId, playing: false, positionSec: tr.seconds });
}

export function stop(ownerId: string): void {
  // Без current владелец может ещё ждать загрузки Tone — гасим и такой play
  if (current ? current.ownerId !== ownerId : pendingOwner !== ownerId) return;
  seq++;
  pendingOwner = null;
  teardown();
  setState(IDLE);
}

export function seek(ownerId: string, sec: number): void {
  if (!tone || current?.ownerId !== ownerId) return;
  const pos = Math.max(0, Math.min(sec, current.doc.durationSec));
  tone.getTransport().seconds = pos;
  setState({ ...state, positionSec: pos });
}

export function getState(): MidiPlayerState {
  return state;
}

export function subscribe(fn: (s: MidiPlayerState) => void): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}

// Подменяет import('tone') и сбрасывает стор; null — вернуть настоящий загрузчик
export function setToneLoaderForTests(next: ToneLoader | null): void {
  teardown();
  seq++;
  pendingOwner = null;
  loader = next ?? (() => import('tone'));
  tone = null;
  state = IDLE;
  listeners.clear();
}
