// Связь кадров с «Картинками» (ADR-022 §3, фронтовая часть): нити картинок чата читаются через
// REST редактора картинок (то же, что видит его панель), по ним рисуются миниатюры кадров-нитей
// и ловится момент, когда у нарисованной для кадра нити появилась версия.
// Видео никогда не читает чужие хранилища напрямую — только публичные ручки и события.

import { useEffect, useSyncExternalStore } from 'react';
import { onMessage, readStoredToken, request } from 'aihome_shell/kit';
import type { FrameRef } from '../api';
import { isPersonalScope } from '../scope';

interface ImgVersion { id: string; number: number; currentStepId: string | null; baseStepId: string | null }
interface ImgThread {
  id: string; file: string | null; lineage: string[]; currentStepId: string | null;
  versions?: ImgVersion[]; currentVersionId?: string | null;
}
interface ImgState { focus: string | null; revision: number; threads: ImgThread[] }

export const imageBase = (scope: string, sessionId: string) =>
  isPersonalScope(scope) ? `/image-editor/chats/${encodeURIComponent(sessionId)}` : `/projects/${encodeURIComponent(scope)}/image-editor`;
const threadsUrl = (scope: string, sessionId: string) =>
  isPersonalScope(scope) ? `${imageBase(scope, sessionId)}/threads` : `${imageBase(scope, sessionId)}/sessions/${encodeURIComponent(sessionId)}/threads`;

const _states = new Map<string, ImgState>();
const _loading = new Set<string>();
// Нить, нарисованная ради кадра: первая версия станет кадром
export interface FrameBinding { sceneId: string; slot: 'A' | 'B'; threadId: string }
const _bindings = new Map<string, FrameBinding>();
const _boundListeners = new Set<(sessionId: string, b: FrameBinding, versionId: string) => void>();
let _version = 0;
const _subs = new Set<() => void>();
let _off: (() => void) | null = null;
const emit = () => { _version++; _subs.forEach(f => f()); };

// Версия нити с картинкой: первая не «origin» (у черновика исходника нет), иначе текущая
export function versionWithImage(t: ImgThread): string | null {
  const vs = t.versions ?? [];
  const cur = vs.find(v => v.id === t.currentVersionId);
  if (cur && cur.id !== 'origin') return cur.id;
  const any = vs.filter(v => v.id !== 'origin').pop();
  if (any) return any.id;
  return t.file ? 'origin' : null;
}

function onImageEvent(m: { type?: string; sessionId?: string; state?: ImgState }) {
  if (m.type !== 'image_thread_changed' || !m.sessionId || !m.state) return;
  _states.set(m.sessionId, m.state);
  const b = _bindings.get(m.sessionId);
  if (b) {
    const t = m.state.threads.find(x => x.id === b.threadId);
    const v = t && versionWithImage(t);
    if (v) {
      _bindings.delete(m.sessionId);
      _boundListeners.forEach(f => f(m.sessionId!, b, v));
    }
  }
  emit();
}

function ensureLive() {
  if (_off) return;
  _off = onMessage(msg => onImageEvent(msg as never));
}

export async function loadImageThreads(scope: string, sessionId: string, force = false): Promise<ImgState | null> {
  if (!force && (_states.has(sessionId) || _loading.has(sessionId))) return _states.get(sessionId) ?? null;
  _loading.add(sessionId);
  try {
    const st = await request<ImgState>(threadsUrl(scope, sessionId), { live: true });
    _states.set(sessionId, st);
    emit();
    return st;
  } catch {
    return null;
  } finally {
    _loading.delete(sessionId);
  }
}

// Заводит черновик «Новая картинка» (draftFolder) или берёт файл проекта в работу (file)
export async function createImageThread(scope: string, sessionId: string, what: { draftFolder: string } | { file: string }): Promise<string | null> {
  const cur = await loadImageThreads(scope, sessionId, true);
  const before = new Set((cur?.threads ?? []).map(t => t.id));
  const next = await request<ImgState>(threadsUrl(scope, sessionId), {
    method: 'POST', body: JSON.stringify({ ...what, revision: cur?.revision ?? 0 }),
  });
  _states.set(sessionId, next);
  emit();
  const created = next.threads.find(t => !before.has(t.id)) ?? next.threads.find(t => 'file' in what && (t.file === what.file || t.lineage.includes(what.file)));
  return created?.id ?? null;
}

export function bindFrame(sessionId: string, b: FrameBinding) {
  ensureLive();
  _bindings.set(sessionId, b);
}
export const unbindFrame = (sessionId: string) => { _bindings.delete(sessionId); };
export const getBinding = (sessionId: string | null) => (sessionId && _bindings.get(sessionId)) || null;

// Подписка на «нить для кадра получила картинку» — её ставит стор действий
export function onFrameReady(fn: (sessionId: string, b: FrameBinding, versionId: string) => void) {
  ensureLive();
  _boundListeners.add(fn);
  return () => { _boundListeners.delete(fn); };
}

const withToken = (url: string) => {
  const t = readStoredToken();
  return t ? `${url}?access_token=${encodeURIComponent(t)}` : url;
};

// Адрес картинки версии нити (шаг из рабочей папки редактора картинок); null — картинки нет или она ещё не пришла
export function imageSrcOf(scope: string, sessionId: string, threadId: string, versionId: string): string | null {
  const t = _states.get(sessionId)?.threads.find(x => x.id === threadId);
  if (!t) return null;
  const v = t.versions?.find(x => x.id === versionId);
  const step = v ? (v.currentStepId ?? (v.id === 'origin' ? t.currentStepId : v.baseStepId)) : t.currentStepId;
  if (step) return withToken(`/api${imageBase(scope, sessionId)}/steps/${encodeURIComponent(step)}`);
  return null;
}

export function imageThreadName(sessionId: string, threadId: string): string | null {
  const t = _states.get(sessionId)?.threads.find(x => x.id === threadId);
  if (!t) return null;
  const f = t.lineage[0] ?? t.file;
  return f ? f.split('/').pop() ?? f : 'Новая картинка';
}

export const imageThreadExists = (sessionId: string, threadId: string) => !!_states.get(sessionId)?.threads.some(x => x.id === threadId);
// Версия, которую видно в нити сейчас: актуальнее ли кадр, чем она
export const latestVersionOf = (sessionId: string, threadId: string): string | null => {
  const t = _states.get(sessionId)?.threads.find(x => x.id === threadId);
  return t ? versionWithImage(t) : null;
};

export function useImageFramesVersion(scope: string, sessionId: string | null, need: boolean): number {
  ensureLive();
  const v = useSyncExternalStore(f => { _subs.add(f); return () => { _subs.delete(f); }; }, () => _version, () => _version);
  useEffect(() => { if (need && sessionId) void loadImageThreads(scope, sessionId); }, [need, scope, sessionId]);
  return v;
}

export const frameThread = (f: FrameRef | null | undefined) => (f && f.kind === 'image' ? f.threadId : null);

export function __resetImageFrames() { _states.clear(); _bindings.clear(); _loading.clear(); emit(); }
export function __setImageState(sessionId: string, st: ImgState) { _states.set(sessionId, st); emit(); }
