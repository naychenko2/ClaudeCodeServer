import type * as ToneNs from 'tone';
import type { GmFamily, MidiNote } from './midiModel';

// Набор синтезаторов на семейства GM — без семплов и саундфонтов.
// Tone приходит параметром: статический импорт утащил бы его в основной чанк.

type Tone = typeof ToneNs;
type Disposable = { dispose(): unknown };

type MelodicVoice = { synth: ToneNs.PolySynth; transpose: number };

type DrumKit = {
  kick: ToneNs.MembraneSynth;
  noise: ToneNs.NoiseSynth;
  // NoiseSynth одноголосый: два запуска в один момент он не принимает
  lastNoiseTime: number;
};

export type SynthBank = {
  melodic: Map<GmFamily, MelodicVoice>;
  drums: DrumKit | null;
  nodes: Disposable[];
};

const MAX_POLYPHONY = 32;
const KICK_NOTES = new Set([35, 36]);

function midiToHz(midi: number): number {
  return 440 * Math.pow(2, (midi - 69) / 12);
}

function createMelodic(Tone: Tone, family: GmFamily, nodes: Disposable[]): MelodicVoice {
  const out = Tone.getDestination();
  switch (family) {
    case 'strings':
    case 'pad': {
      const filter = new Tone.Filter(family === 'pad' ? 1200 : 2400, 'lowpass');
      const synth = new Tone.PolySynth({
        maxPolyphony: MAX_POLYPHONY,
        voice: Tone.Synth,
        options: {
          oscillator: { type: 'sawtooth' },
          envelope: family === 'pad'
            ? { attack: 0.3, decay: 0.2, sustain: 0.8, release: 1 }
            : { attack: 0.08, decay: 0.2, sustain: 0.7, release: 0.5 },
        },
      });
      synth.connect(filter);
      filter.connect(out);
      nodes.push(synth, filter);
      return { synth, transpose: 0 };
    }
    case 'brass':
    case 'lead': {
      const synth = new Tone.PolySynth({
        maxPolyphony: MAX_POLYPHONY,
        voice: Tone.Synth,
        options: {
          oscillator: { type: 'square' },
          envelope: { attack: 0.02, decay: 0.1, sustain: 0.6, release: 0.2 },
        },
      });
      synth.volume.value = -10;
      synth.connect(out);
      nodes.push(synth);
      return { synth, transpose: 0 };
    }
    case 'bass': {
      const synth = new Tone.PolySynth({
        maxPolyphony: MAX_POLYPHONY,
        voice: Tone.Synth,
        options: {
          oscillator: { type: 'sine' },
          envelope: { attack: 0.01, decay: 0.2, sustain: 0.6, release: 0.2 },
        },
      });
      synth.connect(out);
      nodes.push(synth);
      return { synth, transpose: -12 };
    }
    default: {
      // piano и keys
      const synth = new Tone.PolySynth({
        maxPolyphony: MAX_POLYPHONY,
        voice: Tone.Synth,
        options: {
          oscillator: { type: 'triangle' },
          envelope: { attack: 0.005, decay: 0.3, sustain: 0.2, release: 0.4 },
        },
      });
      synth.connect(out);
      nodes.push(synth);
      return { synth, transpose: 0 };
    }
  }
}

function createDrums(Tone: Tone, nodes: Disposable[]): DrumKit {
  const out = Tone.getDestination();
  const kick = new Tone.MembraneSynth();
  const noise = new Tone.NoiseSynth({
    noise: { type: 'white' },
    envelope: { attack: 0.001, decay: 0.12, sustain: 0, release: 0.05 },
  });
  noise.volume.value = -12;
  kick.connect(out);
  noise.connect(out);
  nodes.push(kick, noise);
  return { kick, noise, lastNoiseTime: -1 };
}

export function createSynths(Tone: Tone, families: GmFamily[]): SynthBank {
  const bank: SynthBank = { melodic: new Map(), drums: null, nodes: [] };
  for (const family of new Set(families)) {
    if (family === 'drums') bank.drums = createDrums(Tone, bank.nodes);
    else bank.melodic.set(family, createMelodic(Tone, family, bank.nodes));
  }
  return bank;
}

// time — момент аудиоконтекста из колбэка Tone.Part
export function playNote(bank: SynthBank, family: GmFamily, note: MidiNote, time: number): void {
  if (family === 'drums') {
    const kit = bank.drums;
    if (!kit) return;
    const dur = Math.min(note.duration, 0.2);
    if (KICK_NOTES.has(note.midi)) {
      kit.kick.triggerAttackRelease('C1', dur, time, note.velocity);
      return;
    }
    const at = time > kit.lastNoiseTime ? time : kit.lastNoiseTime + 0.001;
    kit.lastNoiseTime = at;
    kit.noise.triggerAttackRelease(dur, at, note.velocity);
    return;
  }
  const voice = bank.melodic.get(family);
  if (!voice) return;
  voice.synth.triggerAttackRelease(midiToHz(note.midi + voice.transpose), note.duration, time, note.velocity);
}

export function disposeSynths(bank: SynthBank): void {
  for (const node of bank.nodes) node.dispose();
  bank.nodes = [];
  bank.melodic.clear();
  bank.drums = null;
}
