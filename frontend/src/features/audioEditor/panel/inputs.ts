// Входы операций звука: поля правки без ИИ (TrimInputs — для редактора звука) и старое хранилище входов в
// localStorage браузера (`cc_audio_inputs:`), из которого context/legacyInputs.ts переносит значения в
// референсы контекста хода (ADR-023).

import type { AudioFileFormat, AudioJoint, AudioOp, AudioOpInputs } from '../api';

export interface Replica { voice: string; text: string }

export interface TrimInputs {
  start: number | null;
  end: number | null;
  gainDb: number;
  fadeIn: number;
  fadeOut: number;
  normalize: boolean;
  format: AudioFileFormat | '';
}

export interface ConcatPiece { threadId?: string | null; versionId?: string | null; projectFile?: string | null; label: string }

export interface ConcatInputs {
  pieces: ConcatPiece[];
  joint: AudioJoint;
  // Свой стык на место между кусками i и i+1; null — как у всех
  joints: (AudioJoint | null)[];
  normalize: boolean;
  name: string;
  format: AudioFileFormat;
}

export interface PanelInputs {
  language: string;
  referencePath: string;
  // Голос из библиотеки «Голоса» значением voice:<slug>; пусто — не выбран
  voice: string;
  voiceModelPath: string;
  voiceIndexPath: string;
  clipPaths: string[];
  replicas: Replica[];
  // Музыка: слова с секциями, «Инструментал», длительность результата (null — как у модели)
  lyrics: string;
  instrumental: boolean;
  durationSec: number | null;
  trim: TrimInputs;
  concat: ConcatInputs;
}

export const DEFAULT_TRIM: TrimInputs = { start: null, end: null, gainDb: 0, fadeIn: 0, fadeOut: 0, normalize: false, format: '' };
export const DEFAULT_CONCAT: ConcatInputs = {
  pieces: [], joint: { kind: 'pause', seconds: 0.5 }, joints: [], normalize: true, name: '', format: 'mp3',
};

export const DEFAULT_INPUTS: PanelInputs = {
  language: '', referencePath: '', voice: '', voiceModelPath: '', voiceIndexPath: '', clipPaths: [], replicas: [],
  lyrics: '', instrumental: false, durationSec: null,
  trim: DEFAULT_TRIM, concat: DEFAULT_CONCAT,
};

const PREFIX = 'cc_audio_inputs:';

// Ключ нити; без нити — черновик настроек чата (новый звук)
export const inputsKey = (scope: string, sessionId: string | null, threadId: string | null) =>
  `${PREFIX}${scope}:${threadId ?? `new:${sessionId ?? ''}`}`;

export function readInputs(key: string): PanelInputs {
  try {
    const raw = JSON.parse(localStorage.getItem(key) ?? 'null') as Partial<PanelInputs> | null;
    if (!raw || typeof raw !== 'object') return DEFAULT_INPUTS;
    return {
      ...DEFAULT_INPUTS, ...raw,
      trim: { ...DEFAULT_TRIM, ...(raw.trim ?? {}) },
      concat: { ...DEFAULT_CONCAT, ...(raw.concat ?? {}) },
    };
  } catch {
    return DEFAULT_INPUTS;
  }
}

// В браузер ложатся только входы вне белого списка сервера — плюс старые входы белого списка,
// которые ещё ждут переезда на сервер (их операция у нити не выбрана, см. migrateLocal)
export function writeInputs(key: string, value: PanelInputs) {
  storeLocal(key, localPart(value), waitingPart(readRaw(key)));
}

function storeLocal(key: string, local: Partial<PanelInputs>, waiting: Partial<PanelInputs>) {
  const value = { ...local, ...waiting, concat: { ...local.concat, ...waiting.concat } };
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* квота — входы не критичны */ }
}

function readRaw(key: string): Partial<PanelInputs> | null {
  try {
    const raw = JSON.parse(localStorage.getItem(key) ?? 'null') as Partial<PanelInputs> | null;
    return raw && typeof raw === 'object' ? raw : null;
  } catch {
    return null;
  }
}

function localPart(v: PanelInputs): Partial<PanelInputs> {
  const { language: _l, referencePath: _r, voice: _v, replicas: _d, concat, ...rest } = v;
  const { pieces: _p, joint: _j, joints: _js, ...concatRest } = concat;
  return { ...rest, concat: concatRest as ConcatInputs };
}

// Входы белого списка из сырого localStorage, которые операция op не берёт (без op — все): на
// сервер их сейчас не отправить, стереть — потерять. concat — только серверная половина склейки
function waitingPart(raw: Partial<PanelInputs> | null, op?: AudioOp): Partial<PanelInputs> {
  if (!raw) return {};
  const waits = (k: keyof AudioOpInputs) => !op || !KEYS_OF[k](op);
  const out: Partial<PanelInputs> = {};
  if (raw.language && waits('language')) out.language = raw.language;
  if (raw.referencePath && waits('referencePath')) out.referencePath = raw.referencePath;
  if (raw.voice && waits('voice')) out.voice = raw.voice;
  if (raw.replicas?.length && waits('dialogue')) out.replicas = raw.replicas;
  if (raw.concat?.pieces?.length && waits('pieces')) {
    const { pieces, joint, joints } = raw.concat;
    out.concat = { pieces, ...(joint ? { joint } : {}), ...(joints ? { joints } : {}) } as ConcatInputs;
  }
  return out;
}

// ── Входы на сервере ──

const LANGUAGE_OPS: ReadonlySet<AudioOp> = new Set(['speak', 'designVoice', 'cloneVoice', 'dialogue', 'transcribe']);
const REFERENCE_OPS: ReadonlySet<AudioOp> = new Set(['cloneVoice', 'convertVoice', 'master']);
// Голос из библиотеки берут озвучка, клон по образцу и смена голоса (LibraryVoiceRefusal драйверов)
export const LIBRARY_VOICE_OPS: ReadonlySet<AudioOp> = new Set(['speak', 'cloneVoice', 'convertVoice']);
const PIECE_OPS: ReadonlySet<AudioOp> = new Set(['repaint', 'trim']);

const KEYS_OF: Record<keyof AudioOpInputs, (op: AudioOp) => boolean> = {
  language: op => LANGUAGE_OPS.has(op),
  referencePath: op => REFERENCE_OPS.has(op),
  voice: op => LIBRARY_VOICE_OPS.has(op),
  startSec: op => PIECE_OPS.has(op),
  endSec: op => PIECE_OPS.has(op),
  dialogue: op => op === 'dialogue',
  pieces: op => op === 'concat',
  joint: op => op === 'concat',
  joints: op => op === 'concat',
};

