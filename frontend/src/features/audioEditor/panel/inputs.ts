// Настройки панели «Звук» на нить (решение Андрея 01.10: настройки на каждый файл).
//
// Две половины, и каждая хранится там, где её примет сервер:
// - режим, операция, поставщик, модель, число вариантов и параметры модели — в настройках нити на
//   сервере (без нити — в префах режима области). Параметры модели сервер сливает в params и сверяет
//   со схемой, поэтому туда идут ТОЛЬКО ключи схемы;
// - входы запуска, которых в схеме нет (язык, образец, записи, реплики, кусок обрезки, куски склейки), —
//   в localStorage браузера на ту же нить: в params они дали бы отказ «задаётся общим полем».
//   Кусок (начало и конец) сюда не пишется: он общий с выделением на волне и живёт в сторе.
// Загруженный файл образца в localStorage не ложится — живёт до перезагрузки страницы.

import type { AudioFileFormat, AudioJoint, AudioMode, AudioModePrefs, AudioThread, AudioThreadSettings } from '../api';
import { audioApi } from '../api';
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
  language: '', referencePath: '', voiceModelPath: '', voiceIndexPath: '', clipPaths: [], replicas: [],
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

export function writeInputs(key: string, value: PanelInputs) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* квота — входы не критичны */ }
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
  };
  try {
    setScopePrefs(scope, await audioApi.putPrefs(scope, sessionId, next.mode, prefs));
    return true;
  } catch {
    return false;
  }
}
