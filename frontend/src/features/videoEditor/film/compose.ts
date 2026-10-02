// «Сочинить под фильм…» → панель «Звук» с заготовкой и возвратом. Первый готовый трек в music/
// встаёт музыкой фильма сам; остальные варианты — «Сменить». «Звук» о видео не знает: связь держит
// этот модуль по событию нитей звука (публичному, как его видит панель «Звук»).

import { onMessage, revealWorkspacePanel, showToast } from 'aihome_shell/kit';
import type { FilmState } from '../api';
import { VIDEO_PANEL, patchFilm } from '../store/videoStore';
import { soundPreset } from './model';

interface AudioThreadLite { id: string; file: string | null; versions?: unknown[] }
interface Pending { scope: string; path: string; name: string; at: number; known: Set<string> | null }

const _pending = new Map<string, Pending>();
const _fromSound = new Set<string>();
let _off: (() => void) | null = null;

const MUSIC_RE = /^music\/.+\.(mp3|wav|flac|ogg|m4a)$/i;

// Первый новый звук с файлом в music/ — музыка фильма
export function pickNewTrack(threads: AudioThreadLite[], known: Set<string> | null): string | null {
  for (const t of threads) {
    if (known?.has(t.id)) continue;
    if (t.file && MUSIC_RE.test(t.file)) return t.file;
  }
  return null;
}

function ensure() {
  if (_off) return;
  _off = onMessage(msg => {
    const m = msg as unknown as { type?: string; sessionId?: string; state?: { threads?: AudioThreadLite[] } };
    if (m.type !== 'audio_thread_changed' || !m.sessionId || !m.state?.threads) return;
    const p = _pending.get(m.sessionId);
    if (!p) return;
    // Первое событие после запроса — снимок того, что уже было: новыми считаются только звуки сверх него
    if (!p.known) { p.known = new Set(m.state.threads.filter(t => t.file).map(t => t.id)); return; }
    const file = pickNewTrack(m.state.threads, p.known);
    if (!file) return;
    _pending.delete(m.sessionId);
    _fromSound.add(`${p.path}\n${file}`);
    void patchFilm(p.scope, m.sessionId, p.path, [{ op: 'music', music: { file, volume: 60, fadeOut: 2 } }]).then(ok => {
      if (ok) showToast(`Трек сочинён: ${file} — встал музыкой фильма «${p.name}»`, '', 'info');
    });
  });
}

export function composeForFilm(scope: string, sessionId: string, name: string, f: FilmState) {
  ensure();
  _pending.set(sessionId, { scope, path: f.path, name, at: Date.now(), known: null });
  revealWorkspacePanel('sound', 'settings', {
    sessionId, preset: soundPreset(name, f),
    returnTo: { key: VIDEO_PANEL, tab: 'film', target: f.path, label: `К фильму «${name}» — панель «Видео»` },
  });
}

export const isFromSound = (path: string, file: string) => _fromSound.has(`${path}\n${file}`);
export const isComposing = (sessionId: string | null, path: string) => !!sessionId && _pending.get(sessionId)?.path === path;
