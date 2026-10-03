// «Из проекта» в меню кадра: выбор картинки проекта миниатюрами. Меню чипа живёт вне React-дерева сцены,
// поэтому окно открывает стор запроса, а хост (вклад composer-chip, смонтирован в каждом чате) его рисует.

import { useSyncExternalStore } from 'react';
import { MODAL_W, Modal } from 'aihome_shell/kit';
import { FRAME_ROLE, setFrameRef, type FrameSlot } from '../store/frameRefs';
import { IMG_RE } from '../editor/primitives';
import { ProjectPicker } from '../editor/ProjectPicker';

export interface FramePickRequest { sessionId: string; scope: string; slot: FrameSlot; folder: string }

let _req: FramePickRequest | null = null;
const _subs = new Set<() => void>();
const emit = () => _subs.forEach(f => f());
const subscribe = (f: () => void) => { _subs.add(f); return () => { _subs.delete(f); }; };

export const openFramePicker = (req: FramePickRequest) => { _req = req; emit(); };
export const closeFramePicker = () => { if (_req) { _req = null; emit(); } };

export function FramePickerHost({ sessionId }: { sessionId: string | null }) {
  const req = useSyncExternalStore(subscribe, () => _req, () => _req);
  if (!req || req.sessionId !== sessionId) return null;
  return (
    <Modal width={MODAL_W.form} title={`Кадр ${req.slot} · из проекта`} onClose={closeFramePicker}>
      <div data-frame-picker={FRAME_ROLE[req.slot]}>
        <ProjectPicker
          scope={req.scope} start={req.folder} accept={IMG_RE} emptyText="В этой папке нет картинок" onBack={closeFramePicker}
          onPick={path => { closeFramePicker(); void setFrameRef(req.sessionId, req.slot, { kind: 'project-file', ref: { path } }); }}
        />
      </div>
    </Modal>
  );
}
