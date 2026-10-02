import { describe, expect, it } from 'vitest';
import { Midi } from '@tonejs/midi';
import { MidiParseError, gmFamily, parseMidi } from './midiModel';

// MIDI собираем в коде: фикстуры-файлы не нужны
function toBuffer(midi: Midi): ArrayBuffer {
  const arr = midi.toArray();
  return arr.buffer.slice(arr.byteOffset, arr.byteOffset + arr.byteLength) as ArrayBuffer;
}

describe('parseMidi', () => {
  it('одна дорожка, три ноты', () => {
    const midi = new Midi();
    const t = midi.addTrack();
    t.addNote({ midi: 60, time: 0, duration: 0.5, velocity: 0.8 });
    t.addNote({ midi: 72, time: 0.5, duration: 0.5 });
    t.addNote({ midi: 55, time: 1, duration: 1 });

    const doc = parseMidi(toBuffer(midi));
    expect(doc.noteCount).toBe(3);
    expect(doc.minPitch).toBe(55);
    expect(doc.maxPitch).toBe(72);
    expect(doc.tracks).toHaveLength(1);
    const times = doc.tracks[0].notes.map(n => n.time);
    expect(times[0]).toBeCloseTo(0, 3);
    expect(times[1]).toBeCloseTo(0.5, 3);
    expect(times[2]).toBeCloseTo(1, 3);
    expect(doc.durationSec).toBeCloseTo(2, 3);
    expect(doc.tracks[0].family).toBe('piano');
  });

  it('канал 9 — ударные, пустые дорожки отброшены с исходным index', () => {
    const midi = new Midi();
    midi.addTrack();
    const drums = midi.addTrack();
    drums.channel = 9;
    drums.addNote({ midi: 36, time: 0, duration: 0.25 });

    const doc = parseMidi(toBuffer(midi));
    expect(doc.tracks).toHaveLength(1);
    expect(doc.tracks[0].index).toBe(1);
    expect(doc.tracks[0].isDrums).toBe(true);
    expect(doc.tracks[0].family).toBe('drums');
  });

  it('120 bpm, 4/4, восемь тактов', () => {
    const midi = new Midi();
    midi.header.setTempo(120);
    midi.header.timeSignatures.push({ ticks: 0, timeSignature: [4, 4] });
    const t = midi.addTrack();
    for (let bar = 0; bar < 8; bar++) t.addNote({ midi: 60, time: bar * 2, duration: 2 });

    const doc = parseMidi(toBuffer(midi));
    expect(doc.bpm).toBeCloseTo(120, 3);
    expect(doc.timeSig).toEqual([4, 4]);
    expect(doc.bars).toHaveLength(8);
    expect(doc.bars[1]).toBeCloseTo(2, 3);
    expect(doc.bars[7]).toBeCloseTo(14, 3);
  });

  it('смена размера 4/4 → 3/4 укорачивает такт', () => {
    const midi = new Midi();
    midi.header.setTempo(120);
    const ppq = midi.header.ppq;
    midi.header.timeSignatures.push({ ticks: 0, timeSignature: [4, 4] });
    midi.header.timeSignatures.push({ ticks: ppq * 8, timeSignature: [3, 4] });
    midi.header.update();
    midi.addTrack().addNote({ midi: 60, time: 0, duration: 8 });

    const doc = parseMidi(toBuffer(midi));
    // два такта 4/4 по 2 с, дальше 3/4 по 1,5 с
    expect(doc.bars[1] - doc.bars[0]).toBeCloseTo(2, 3);
    expect(doc.bars[2]).toBeCloseTo(4, 3);
    expect(doc.bars[3] - doc.bars[2]).toBeCloseTo(1.5, 3);
    expect(doc.bars[4] - doc.bars[3]).toBeCloseTo(1.5, 3);
  });

  it('пустой файл — без нот и без исключения', () => {
    const doc = parseMidi(toBuffer(new Midi()));
    expect(doc.tracks).toEqual([]);
    expect(doc.noteCount).toBe(0);
    expect(doc.minPitch).toBe(0);
    expect(doc.maxPitch).toBe(0);
    expect(doc.bpm).toBe(120);
    expect(doc.timeSig).toEqual([4, 4]);
  });

  it('мусор и обрезанный файл — MidiParseError', () => {
    const junk = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]).buffer;
    expect(() => parseMidi(junk)).toThrow(MidiParseError);
    expect(() => parseMidi(junk)).toThrow('Файл не похож на MIDI');

    const midi = new Midi();
    midi.addTrack().addNote({ midi: 60, time: 0, duration: 1 });
    const full = toBuffer(midi);
    const cut = full.slice(0, 10);
    expect(() => parseMidi(cut)).toThrow(MidiParseError);
    expect(() => parseMidi(cut)).toThrow('Файл MIDI повреждён');
  });
});

describe('gmFamily', () => {
  it('граничные номера GM', () => {
    const cases: [number, string][] = [
      [0, 'piano'], [5, 'piano'], [7, 'piano'], [8, 'keys'], [23, 'keys'], [24, 'strings'], [31, 'strings'],
      [32, 'bass'], [39, 'bass'], [40, 'strings'], [55, 'strings'], [56, 'brass'], [79, 'brass'],
      [80, 'lead'], [87, 'lead'], [88, 'pad'], [103, 'pad'], [104, 'piano'], [127, 'piano'],
    ];
    for (const [program, family] of cases) expect(gmFamily(program, false), `program ${program}`).toBe(family);
    expect(gmFamily(0, true)).toBe('drums');
  });
});
