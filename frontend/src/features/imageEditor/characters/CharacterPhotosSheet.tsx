// Шторка «Фото» персонажа (спека character-panel-final-spec §8.1): два раздела — «Лицо для узнавания»
// и «Для обучения». Фото лица уходят на сервер сразу, а свежий персонаж едет наверх через onChanged.

import { useEffect, useRef, useState } from 'react';
import { Modal, useIsMobile, C, FS, MODAL_W, SP } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from '../api';
import { CharacterFacePhotos, type FacePhoto } from './CharacterFacePhotos';
import { CharacterTrainingPhotos } from './CharacterTrainingPhotos';
import { FACES, SHEET, TRAIN_PHOTOS } from './characterTexts';

export function CharacterPhotosSheet({ api, projectId, character, onChanged, onCount, onClose, focus }: {
  api: ImageEditorApi;
  projectId: string;
  character: ImageEditCharacter;
  onChanged: (c: ImageEditCharacter) => void;
  onCount?: (n: number) => void;
  onClose: () => void;
  // 'train' — открыть сразу на разделе обучения
  focus?: 'face' | 'train';
}) {
  const isMobile = useIsMobile();
  const [busy, setBusy] = useState(false);
  const trainRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (focus === 'train') trainRef.current?.scrollIntoView?.({ block: 'start' });
  }, [focus]);

  const photos: FacePhoto[] = [...character.photos.filter(p => p.primary), ...character.photos.filter(p => !p.primary)]
    .map((p, i) => ({ key: p.file, url: api.characterPhotoUrl(projectId, character.slug, p.file), primary: i === 0 }));

  // Имя и описание шлём как есть: PUT с одними фото их не меняет по смыслу
  const save = async (input: { photos: Blob[]; removePhotos?: string[] }) => {
    setBusy(true);
    try {
      onChanged(await api.updateCharacter(projectId, character.slug, {
        name: character.name, description: character.description ?? undefined, ...input,
      }));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={SHEET.photos(character.name)} width={MODAL_W.form} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, minWidth: 0 }}>
        <section style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
          <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{FACES.title}</div>
          <CharacterFacePhotos photos={photos} disabled={busy} isMobile={isMobile}
            onAdd={files => save({ photos: files })}
            onRemove={key => save({ photos: [], removePhotos: [key] })} />
        </section>
        <div ref={trainRef}>
          <CharacterTrainingPhotos projectId={projectId} slug={character.slug} onCount={onCount}
            title={TRAIN_PHOTOS.title} hint={TRAIN_PHOTOS.hint} />
        </div>
      </div>
    </Modal>
  );
}
