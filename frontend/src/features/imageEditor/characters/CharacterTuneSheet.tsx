// Шторка «Тонкая настройка» (спека character-panel-final-spec §8.3): слово-метка обучения и своя обученная версия.
// Слово-метка на сервере не хранится, оно уходит в старт обучения через setTrigger хука; LoRA — через updateCharacter.
// «Отмена» откатывает и слово-метку, и выбор LoRA; при ошибке сохранения шторка остаётся открытой.

import { useState } from 'react';
import { Field, Modal, ModalActions, TextField, MODAL_W, C, FS, SP } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditLoras, ImageEditorApi } from '../api';
import { CharacterLoraSection, type LoraInput } from './CharacterLoraSection';
import { EXPERT } from './characterTexts';
import { isValidTrigger } from './loraTrainView';
import { TRAIN_TEXT } from './loraTrainTexts';

type SetTrigger = (t: string | null) => void;

// Отмена: возвращаем слово-метку, каким оно было при открытии (null — умолчательное), и закрываем
export function cancelTune(initialEdited: string | null, setTrigger: SetTrigger, onClose: () => void) {
  setTrigger(initialEdited);
  onClose();
}

// Сохранение: LoRA не трогали — запроса нет; иначе имя и описание шлём как есть, LoRA — полным путём
export async function saveTune(o: {
  api: ImageEditorApi; projectId: string; character: ImageEditCharacter; lora: LoraInput | undefined;
  onChanged: (c: ImageEditCharacter) => void; onClose: () => void;
}) {
  if (o.lora) {
    o.onChanged(await o.api.updateCharacter(o.projectId, o.character.slug, {
      name: o.character.name, description: o.character.description ?? undefined, photos: [], lora: o.lora,
    }));
  }
  o.onClose();
}

export function CharacterTuneSheet({ api, projectId, character, loraList, trigger, edited, setTrigger, onChanged, onClose }: {
  api: ImageEditorApi;
  projectId: string;
  character: ImageEditCharacter;
  loraList: ImageEditLoras | null;
  // Слово-метка из useCharacterTrain; edited — явно введённое (null — умолчательное)
  trigger: string;
  edited: string | null;
  setTrigger: SetTrigger;
  onChanged: (c: ImageEditCharacter) => void;
  onClose: () => void;
}) {
  const [initialEdited] = useState(edited);
  const [lora, setLora] = useState<LoraInput | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const valid = isValidTrigger(trigger);

  const save = async () => {
    setBusy(true); setError(null);
    try {
      await saveTune({ api, projectId, character, lora, onChanged, onClose });
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : 'Не удалось сохранить. Попробуйте ещё раз.');
      setBusy(false);
    }
  };

  return (
    <Modal title={EXPERT.title} width={MODAL_W.form} onClose={() => cancelTune(initialEdited, setTrigger, onClose)}
      footer={
        <ModalActions confirmLabel="Сохранить" loading={busy} confirmDisabled={!valid}
          onConfirm={() => void save()} onCancel={() => cancelTune(initialEdited, setTrigger, onClose)} />
      }>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
        <Field label={EXPERT.triggerLabel}
          hint={valid ? TRAIN_TEXT.triggerHint : undefined} error={valid ? undefined : TRAIN_TEXT.triggerError}>
          <TextField value={trigger} onChange={setTrigger} mono invalid={!valid} disabled={busy} />
        </Field>
        <CharacterLoraSection key={`${character.slug}|${character.local?.lora ?? ''}`} list={loraList} current={character.local}
          slug={character.slug} disabled={busy} onChange={setLora} />
        {error && <div role="alert" style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      </div>
    </Modal>
  );
}
