// «Сохранить как…» версии звука: папка проекта и имя. Перезаписи нет — занятое имя сервер
// отвергает 409 name_taken со свободным именем рядом, его можно взять одной кнопкой.
// Стемы и субтитры ложатся рядом под тем же именем — это решает сервер (AudioProjectSaver).

import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Button, Field, Modal, ModalActions, TextField, C, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { AudioThread, AudioThreadVersion } from '../api';
import { saveVersion } from './actions';
import { defaultSaveFolder, defaultSaveName, splitSuggestion, versionLabel } from './model';

export function SaveAsDialog({ scope, sessionId, thread, version, onClose }: {
  scope: string; sessionId: string; thread: AudioThread; version: AudioThreadVersion; onClose: () => void;
}) {
  const [folder, setFolder] = useState(defaultSaveFolder(thread));
  const [name, setName] = useState(defaultSaveName(thread, version));
  const [busy, setBusy] = useState(false);
  const [taken, setTaken] = useState<{ error: string; suggestion: string } | null>(null);

  const submit = async () => {
    if (!name.trim() || busy) return;
    setBusy(true);
    const r = await saveVersion(scope, sessionId, thread, version.id, { folder: folder.trim().replace(/^\/+|\/+$/g, ''), fileName: name.trim() });
    setBusy(false);
    if (r.ok) onClose();
    else setTaken(r.suggestion ? { error: r.error, suggestion: r.suggestion } : null);
  };

  const take = (path: string) => {
    const s = splitSuggestion(path);
    setFolder(s.folder);
    setName(s.fileName);
    setTaken(null);
  };

  return (
    <Modal title="Сохранить как…" subtitle={versionLabel(version)} width={480} onClose={onClose}
      footer={<ModalActions confirmLabel="Сохранить" onConfirm={() => { void submit(); }} loading={busy}
        confirmDisabled={!name.trim()} onCancel={onClose} />}>
      <div data-audio-save-as="" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <Field label="Папка" hint="Путь от корня проекта; пусто — корень">
          <TextField value={folder} onChange={v => { setFolder(v); setTaken(null); }} placeholder="корень проекта" mono />
        </Field>
        <Field label="Имя файла" hint="Расширение — по формату версии; стемы лягут папкой рядом с тем же именем">
          <TextField value={name} onChange={v => { setName(v); setTaken(null); }} autoFocus onEnter={() => { void submit(); }} />
        </Field>
        {taken && (
          <div data-audio-save-taken="" style={{
            display: 'flex', flexDirection: 'column', gap: SP.xs, fontSize: FS.sm, lineHeight: 1.45,
            color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
          }}>
            <span style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', fontWeight: 600 }}>
              <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 2 }} />
              {taken.error}
            </span>
            <span>Перезаписать нельзя — возьмите другое имя. Свободное рядом: <b>{taken.suggestion}</b></span>
            <span>
              <Button size="sm" variant="secondary" onClick={() => take(taken.suggestion)}>
                Взять «{taken.suggestion.split('/').pop()}»
              </Button>
            </span>
          </div>
        )}
      </div>
    </Modal>
  );
}
