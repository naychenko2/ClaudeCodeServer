// Диалог «Сохранить в проект» (экран 7). Результат всегда новым файлом: у правки
// имя версии подбирает сервер (hero.png → hero.v2.png, занято — следующий номер),
// поэтому в диалоге оно показано как итог, а не поле ввода. У «Нарисовать картинку»
// имя задаёт человек.

import { useState } from 'react';
import { Field, Modal, ModalActions, TextField, C, FS, R, SP } from 'aihome_shell/kit';

export function SaveDialog({ mode, sourcePath, suggestedName, folder, onSave, onClose }: {
  mode: 'edit' | 'create';
  sourcePath: string | null;
  suggestedName: string;
  folder: string;
  onSave: (v: { fileName: string; folder: string }) => Promise<void>;
  onClose: () => void;
}) {
  const [name, setName] = useState(suggestedName);
  const [dir, setDir] = useState(folder);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSave({ fileName: name.trim(), folder: dir.trim() });
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  const edit = mode === 'edit';
  return (
    <Modal title="Сохранить в проект" onClose={onClose}
      footer={<ModalActions confirmLabel="Сохранить в проект" onConfirm={submit} loading={busy}
        confirmDisabled={!edit && !name.trim()} onCancel={onClose} />}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <Field label="Имя файла">
          <TextField value={edit ? suggestedName : name} onChange={setName} disabled={edit} autoFocus={!edit} onEnter={submit} />
        </Field>
        <Field label="Папка">
          <TextField value={edit ? (folder || '(корень проекта)') : dir} onChange={setDir} disabled={edit} placeholder="(корень проекта)" />
        </Field>
        {edit && sourcePath && (
          <div style={{ fontSize: FS.sm, color: C.successText, background: C.successBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px` }}>
            Оригинал {sourcePath} не изменится — новая версия ляжет рядом.
          </div>
        )}
        {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      </div>
    </Modal>
  );
}
