// Шторка «Имя и описание» (спека character-panel-final-spec §8.5): фото не меняются, согласие не нужно.
// Без имени «Сохранить» недоступна; при ошибке шторка остаётся открытой, введённое не теряется.

import { useState } from 'react';
import { Field, Modal, ModalActions, TextArea, TextField, MODAL_W, C, FS, SP } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from '../api';

export const NAME_SHEET_TITLE = 'Имя и описание';

// Можно ли сохранять: нужно непустое имя
export const canSaveName = (name: string) => name.trim().length > 0;

// Сохранение: имя и описание уходят без фото и без LoRA — остальное на сервере не меняется
export async function saveNameDescription(o: {
  api: ImageEditorApi; projectId: string; character: ImageEditCharacter; name: string; description: string;
  onChanged: (c: ImageEditCharacter) => void; onClose: () => void;
}) {
  const updated = await o.api.updateCharacter(o.projectId, o.character.slug, {
    name: o.name.trim(), description: o.description.trim() || undefined, photos: [],
  });
  o.onChanged(updated);
  o.onClose();
}

export function CharacterNameSheet({ api, projectId, character, onChanged, onClose }: {
  api: ImageEditorApi;
  projectId: string;
  character: ImageEditCharacter;
  onChanged: (c: ImageEditCharacter) => void;
  onClose: () => void;
}) {
  const [name, setName] = useState(character.name);
  const [description, setDescription] = useState(character.description ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    setBusy(true); setError(null);
    try {
      await saveNameDescription({ api, projectId, character, name, description, onChanged, onClose });
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : 'Не удалось сохранить. Попробуйте ещё раз.');
      setBusy(false);
    }
  };

  return (
    <Modal title={NAME_SHEET_TITLE} width={MODAL_W.form} onClose={onClose}
      footer={
        <>
          {error && <div role="alert" style={{ fontSize: FS.sm, color: C.dangerText, marginBottom: SP.sm }}>{error}</div>}
          <ModalActions confirmLabel="Сохранить" loading={busy} confirmDisabled={!canSaveName(name)}
            onConfirm={() => void save()} onCancel={onClose} />
        </>
      }>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
        <Field label="Имя">
          <TextField value={name} onChange={setName} placeholder="Например, Аня" autoFocus disabled={busy} />
        </Field>
        <Field label="Описание" hint="Уходит в запрос вместе с фото: возраст, причёска, приметы">
          <TextArea value={description} onChange={setDescription} minHeight={56} autoGrow maxHeight={140}
            placeholder="девушка 25 лет, каре, веснушки" disabled={busy} />
        </Field>
      </div>
    </Modal>
  );
}
