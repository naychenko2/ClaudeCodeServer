import { Midi } from '@tonejs/midi';

// Разбор .mid в плоскую модель для просмотрщика: без React и без Tone.js.

export type MidiNote = { midi: number; time: number; duration: number; velocity: number };

export type GmFamily = 'piano' | 'keys' | 'strings' | 'pad' | 'brass' | 'lead' | 'bass' | 'drums';

export type MidiTrackView = {
  index: number;
  name: string;
  channel: number;
  isDrums: boolean;
  program: number;
  family: GmFamily;
  notes: MidiNote[];
};

export type MidiDoc = {
  durationSec: number;
  bpm: number;
  timeSig: [number, number];
  bars: number[];
  tracks: MidiTrackView[];
  minPitch: number;
  maxPitch: number;
  noteCount: number;
};

export class MidiParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'MidiParseError';
  }
}

export function gmFamily(program: number, isDrums: boolean): GmFamily {
  if (isDrums) return 'drums';
  if (program >= 0 && program <= 7) return 'piano';
  if (program >= 8 && program <= 23) return 'keys';
  if ((program >= 24 && program <= 31) || (program >= 40 && program <= 55)) return 'strings';
  if (program >= 32 && program <= 39) return 'bass';
  if (program >= 56 && program <= 79) return 'brass';
  if (program >= 80 && program <= 87) return 'lead';
  if (program >= 88 && program <= 103) return 'pad';
  return 'piano';
}

const DEFAULT_BPM = 120;
const DEFAULT_TIME_SIG: [number, number] = [4, 4];

function hasMidiSignature(bytes: ArrayBuffer): boolean {
  if (bytes.byteLength < 4) return false;
  const head = new Uint8Array(bytes, 0, 4);
  // «MThd»
  return head[0] === 0x4d && head[1] === 0x54 && head[2] === 0x68 && head[3] === 0x64;
}

// Начала тактов в секундах: шагаем по тикам с учётом смен размера до конца нот.
function computeBars(midi: Midi): number[] {
  const { header } = midi;
  const endTicks = midi.durationTicks;
  const sigs = header.timeSignatures.length > 0
    ? header.timeSignatures
    : [{ ticks: 0, timeSignature: DEFAULT_TIME_SIG }];
  const bars: number[] = [];
  let tick = 0;
  let sigIdx = 0;
  while (tick < endTicks) {
    while (sigIdx + 1 < sigs.length && sigs[sigIdx + 1].ticks <= tick) sigIdx++;
    const [num, den] = sigs[sigIdx].timeSignature;
    const ticksPerBar = header.ppq * 4 * num / den;
    if (!(ticksPerBar > 0)) break;
    bars.push(header.ticksToSeconds(tick));
    tick += ticksPerBar;
  }
  return bars;
}

export function parseMidi(bytes: ArrayBuffer): MidiDoc {
  if (!hasMidiSignature(bytes)) throw new MidiParseError('Файл не похож на MIDI');

  let midi: Midi;
  let bars: number[];
  try {
    midi = new Midi(bytes);
    bars = computeBars(midi);
  } catch {
    throw new MidiParseError('Файл MIDI повреждён');
  }

  const tracks: MidiTrackView[] = [];
  let minPitch = Infinity;
  let maxPitch = -Infinity;
  let noteCount = 0;
  midi.tracks.forEach((t, index) => {
    if (t.notes.length === 0) return;
    const isDrums = t.channel === 9;
    const program = t.instrument.number;
    const notes = t.notes.map(n => ({ midi: n.midi, time: n.time, duration: n.duration, velocity: n.velocity }));
    for (const n of notes) {
      if (n.midi < minPitch) minPitch = n.midi;
      if (n.midi > maxPitch) maxPitch = n.midi;
    }
    noteCount += notes.length;
    tracks.push({ index, name: t.name, channel: t.channel, isDrums, program, family: gmFamily(program, isDrums), notes });
  });

  const sig = midi.header.timeSignatures[0]?.timeSignature;
  return {
    durationSec: midi.duration,
    bpm: midi.header.tempos[0]?.bpm ?? DEFAULT_BPM,
    timeSig: sig && sig.length >= 2 ? [sig[0], sig[1]] : DEFAULT_TIME_SIG,
    bars,
    tracks,
    minPitch: noteCount > 0 ? minPitch : 0,
    maxPitch: noteCount > 0 ? maxPitch : 0,
    noteCount,
  };
}
