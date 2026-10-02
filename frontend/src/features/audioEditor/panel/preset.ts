import type { AudioOp } from '../api';
import { opInfo } from '../ops';

// Заготовка панели «Звук» из revealWorkspacePanel('sound', 'settings', { preset }). Целиком
// фронтовая: сервер звука о вызывающей панели («Видео») не знает. Хост передаёт объект
// прозрачно, здесь он разбирается: чужое и ошибочное отбрасывается, а не доезжает до настроек.
export interface SoundPreset {
  mode: 'music';
  op?: AudioOp;
  durationSec?: number;
  style?: string;
  instrumental?: boolean;
  // Откуда заготовка: «под фильм «утро-в-горах»» — в строке контекста нового звука
  from?: string;
  // Черновик, который вызвавшая панель уже завела на сервере (у «Видео» — звук, который встанет
  // музыкой фильма): заготовка ложится на него, а не на новый звук
  threadId?: string;
}

export function parseSoundPreset(raw: unknown): SoundPreset | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (r.mode !== 'music') return null;
  const out: SoundPreset = { mode: 'music' };
  if (typeof r.op === 'string') {
    const op = r.op as AudioOp;
    if (opInfo(op)?.mode === 'music') out.op = op;
  }
  const d = r.duration ?? r.durationSec;
  if (typeof d === 'number' && Number.isFinite(d) && d > 0) out.durationSec = Math.round(d);
  if (typeof r.style === 'string' && r.style.trim()) out.style = r.style.trim();
  if (typeof r.instrumental === 'boolean') out.instrumental = r.instrumental;
  if (typeof r.from === 'string' && r.from.trim()) out.from = r.from.trim();
  if (typeof r.thread === 'string' && r.thread.trim()) out.threadId = r.thread.trim();
  return out;
}
