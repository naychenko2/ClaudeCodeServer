// Настройки панели «Звук» на нить (решение Андрея 01.10: настройки на каждый файл).
//
// Три половины, и каждая хранится там, где её примет сервер:
// - режим, операция, поставщик, модель, число вариантов и параметры модели — в настройках нити на
//   сервере (без нити — в префах режима области). Параметры модели сервер сливает в params и сверяет
//   со схемой, поэтому туда идут ТОЛЬКО ключи схемы;
// - входы операции из белого списка сервера (язык, образец, кусок, голос из библиотеки, реплики,
//   куски склейки и стыки) — в inputs тех же настроек. PUT заменяет inputs целиком, поэтому они едут
//   в КАЖДОМ сохранении, а при смене операции — только те, что нужны новой (projectInputs);
// - остальные входы (модель RVC, записи обучения, слова песни, громкость обрезки, имя склейки) —
//   в localStorage браузера на ту же нить: в белом списке сервера их нет.
// Загруженный файл образца никуда не ложится — живёт до перезагрузки страницы.

import type { AudioFileFormat, AudioJoint, AudioMode, AudioModePrefs, AudioOp, AudioOpInputs, AudioThread, AudioThreadSettings } from '../api';
import { audioApi } from '../api';
import { TO_END, type AudioSelection } from '../player/selection';
import { focusLabel } from '../strip/summary';
import { mutate, setScopePrefs, setShortcutMode } from '../thread/threadStore';

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

// В браузер ложатся только входы вне белого списка сервера
export function writeInputs(key: string, value: PanelInputs) {
  try { localStorage.setItem(key, JSON.stringify(localPart(value))); } catch { /* квота — входы не критичны */ }
}

function localPart(v: PanelInputs): Partial<PanelInputs> {
  const { language: _l, referencePath: _r, voice: _v, replicas: _d, concat, ...rest } = v;
  const { pieces: _p, joint: _j, joints: _js, ...concatRest } = concat;
  return { ...rest, concat: concatRest as ConcatInputs };
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

// Только входы, нужные операции; пусто — null (сервер сам чужие входы не чистит)
export function projectInputs(raw: AudioOpInputs | null | undefined, op: AudioOp): AudioOpInputs | null {
  if (!raw) return null;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(raw)) {
    if (v === undefined || v === null) continue;
    const fits = KEYS_OF[k as keyof AudioOpInputs];
    if (fits?.(op)) out[k] = v;
  }
  return Object.keys(out).length ? out as AudioOpInputs : null;
}

// Входы панели → форма сервера: пустое не едет, у личного чата путей проекта и библиотеки нет
export function toServerInputs(
  op: AudioOp, v: PanelInputs, piece: AudioSelection | null, personal: boolean,
): AudioOpInputs | null {
  const raw: AudioOpInputs = {};
  if (v.language) raw.language = v.language;
  if (!personal && v.referencePath.trim()) raw.referencePath = v.referencePath;
  if (!personal && v.voice) raw.voice = v.voice;
  if (piece) {
    raw.startSec = piece.start;
    if (piece.end !== TO_END) raw.endSec = piece.end;
  }
  if (v.replicas.length) raw.dialogue = v.replicas.map(r => (r.voice ? { text: r.text, voice: r.voice } : { text: r.text }));
  const pieces = v.concat.pieces.filter(p => !personal || !p.projectFile);
  if (pieces.length) {
    raw.pieces = pieces.map(p => (p.projectFile
      ? { projectFile: p.projectFile }
      : { threadId: p.threadId ?? '', ...(p.versionId ? { versionId: p.versionId } : {}) }));
    raw.joint = v.concat.joint;
    if (v.concat.joints.some(Boolean)) raw.joints = v.concat.joints;
  }
  return projectInputs(raw, op);
}

// Подпись куска склейки по нитям чата; нить удалена — честно говорим
function pieceLabel(p: { threadId?: string; versionId?: string; projectFile?: string }, threads: AudioThread[]): string {
  if (p.projectFile) return p.projectFile;
  const t = threads.find(x => x.id === p.threadId);
  if (!t) return 'Звук удалён из чата';
  return focusLabel({ ...t, currentVersionId: p.versionId ?? t.currentVersionId });
}

// Форма сервера → входы панели поверх локальной половины
export function mergeInputs(local: PanelInputs, raw: AudioOpInputs | null | undefined, threads: AudioThread[]): PanelInputs {
  const r = raw ?? {};
  return {
    ...local,
    language: r.language ?? '',
    referencePath: r.referencePath ?? '',
    voice: r.voice ?? '',
    replicas: (r.dialogue ?? []).map(d => ({ text: d.text ?? '', voice: d.voice ?? '' })),
    concat: {
      ...local.concat,
      pieces: (r.pieces ?? []).map(p => ({
        threadId: p.threadId ?? null, versionId: p.versionId ?? null, projectFile: p.projectFile ?? null, label: pieceLabel(p, threads),
      })),
      joint: r.joint ?? DEFAULT_CONCAT.joint,
      joints: r.joints ?? [],
    },
  };
}

// Кусок из входов сервера: без начала куска нет, без конца — до конца версии
export function serverPiece(raw: AudioOpInputs | null | undefined): AudioSelection | null {
  if (typeof raw?.startSec !== 'number') return null;
  return { start: raw.startSec, end: typeof raw.endSec === 'number' ? raw.endSec : TO_END };
}

// Разовый перенос старых входов из браузера: на сервере пусто, а в localStorage что-то есть — отдаём
// их для отправки; локальную копию серверных ключей стираем в любом случае (правда теперь на сервере)
export function migrateLocal(key: string, server: AudioOpInputs | null | undefined, op: AudioOp, personal: boolean): AudioOpInputs | null {
  let raw: Partial<PanelInputs> | null;
  try {
    raw = JSON.parse(localStorage.getItem(key) ?? 'null') as Partial<PanelInputs> | null;
  } catch {
    return null;
  }
  if (!raw || typeof raw !== 'object') return null;
  const legacy = raw.language || raw.referencePath || raw.voice || raw.replicas?.length || raw.concat?.pieces?.length;
  if (!legacy) return null;
  const old = readInputs(key);
  writeInputs(key, old);
  const empty = !server || Object.keys(server).length === 0;
  return empty ? toServerInputs(op, old, null, personal) : null;
}

// Куда пишутся настройки: в нить, если она выбрана, иначе — в префы режима области
export async function saveSettings(
  scope: string, sessionId: string | null, thread: AudioThread | null, next: AudioThreadSettings,
): Promise<boolean> {
  if (thread && sessionId) {
    return mutate(scope, sessionId, rev => audioApi.settings(scope, sessionId, thread.id, next, rev));
  }
  if (sessionId) setShortcutMode(sessionId, next.mode as AudioMode);
  const prefs: AudioModePrefs = {
    operation: next.operation, provider: next.provider, model: next.model, count: next.count ?? null, fields: next.fields,
    inputs: next.inputs ?? null,
  };
  try {
    setScopePrefs(scope, await audioApi.putPrefs(scope, sessionId, next.mode, prefs));
    return true;
  } catch {
    return false;
  }
}
