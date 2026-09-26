// Диалог «Сохранить в проект» (экран 7). Результат всегда новым файлом: у правки
// имя версии подбирает сервер (hero.png → hero.v2.png, занято — следующий номер),
// поэтому в диалоге оно показано как итог, а не поле ввода. У «Нарисовать картинку»
// имя задаёт человек. Тяжёлый файл (больше HeavyFileMb) — предупреждение и кнопка,
// которая подставляет кодирование в WebP; блокировки нет (ADR-018 §9).

import { useState } from 'react';
import { Button, Field, Modal, ModalActions, TextField, C, FS, R, SP } from 'aihome_shell/kit';
import { formatBytes } from './transforms';

export interface HeavyFile {
  bytes: number;
  // Оценка веса в WebP; null — ещё считается
  webpBytes: number | null;
  compress: boolean;
  onCompress: (on: boolean) => void;
}

export function SaveDialog({ mode, sourcePath, suggestedName, folder, onSave, onClose, heavy }: {
  mode: 'edit' | 'create';
  sourcePath: string | null;
  suggestedName: string;
  folder: string;
  onSave: (v: { fileName: string; folder: string }) => Promise<void>;
  onClose: () => void;
  heavy?: HeavyFile | null;
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
        {heavy && (
          <div data-heavy="true" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', fontSize: FS.sm, color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px` }}>
            <span style={{ flex: 1, minWidth: 180 }}>
              {heavy.compress
                ? `Сохраним в WebP${heavy.webpBytes != null ? ` ≈ ${formatBytes(heavy.webpBytes)}` : ''} вместо ${formatBytes(heavy.bytes)}.`
                : `Файл ${formatBytes(heavy.bytes)} — тяжёлый. Сжать в WebP${heavy.webpBytes != null ? ` ≈ ${formatBytes(heavy.webpBytes)}` : ''}?`}
            </span>
            <Button size="sm" variant="secondary" onClick={() => heavy.onCompress(!heavy.compress)}>
              {heavy.compress ? 'Оставить как есть' : 'Сжать в WebP'}
            </Button>
          </div>
        )}
        {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      </div>
    </Modal>
  );
}
