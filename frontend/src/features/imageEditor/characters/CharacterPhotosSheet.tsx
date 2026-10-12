// Шторка «Фото» персонажа (спека character-panel-final-spec §8.1): два раздела — «Лицо для узнавания»
// и «Для обучения». Фото лица уходят на сервер сразу, а свежий персонаж едет наверх через onChanged.
// Метки фото (ADR-027) — только при включённом character-smart-photos; выключен — блок как в master.

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Modal, C, MODAL_W, SP, FLAGS, useFeature } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from '../api';
import { CharacterFacePhotos, type FacePhoto } from './CharacterFacePhotos';
import { CharacterTrainingPhotos } from './CharacterTrainingPhotos';
import { CHARACTER_PHOTOS_NOTICE_LABELS, LABELS, SHEET, TRAIN_PHOTOS } from './characterTexts';
import { hasPendingLabels, PhotoLabelCaption, PhotoLabelEditor, type PhotoLabelState } from './PhotoLabelChips';

// Правка одной метки: {файл: {angle, pitch, emotion}}; null в поле — не менять
type LabelEdit = { angle?: string | null; pitch?: string | null; emotion?: string | null };

// Пропсы блока «Лицо для узнавания», зависящие от флага. Чистая функция — тестируется без Modal
// (порталы server-renderer не умеют): выключен → {} (ровно как master), включён → подписи, нажим,
// редактор и новое согласие.
export function faceSection(smart: boolean, ctx: {
  labels: Map<string, PhotoLabelState>;
  selected: string | null;
  setSelected: (fn: (cur: string | null) => string | null) => void;
  savingLabel: string | null;
  labelError: string | null;
  saveLabel: (file: string, next: LabelEdit) => void;
  removePhoto: (file: string) => void;
  ordered: { file: string }[];
}): {
  notice?: string;
  renderCaption?: (photo: FacePhoto) => ReactNode;
  onSelectPhoto?: (photo: FacePhoto) => void;
  selectedKey?: string | null;
  labelsEditor?: ReactNode;
} {
  if (!smart) return {};
  const { selected, labels } = ctx;
  const selectedLabel = selected != null ? labels.get(selected) : undefined;
  return {
    notice: CHARACTER_PHOTOS_NOTICE_LABELS,
    renderCaption: (p: FacePhoto) => <PhotoLabelCaption label={labels.get(p.key) ?? {}} />,
    onSelectPhoto: (p: FacePhoto) => ctx.setSelected(cur => (cur === p.key ? null : p.key)),
    selectedKey: selected,
    labelsEditor: selectedLabel && selected ? (
      <PhotoLabelEditor index={Math.max(0, ctx.ordered.findIndex(p => p.file === selected))}
        label={selectedLabel} busy={ctx.savingLabel === selected} saveError={ctx.labelError}
        onSave={next => ctx.saveLabel(selected, next)}
        onClose={() => ctx.setSelected(() => null)}
        onRemove={() => ctx.removePhoto(selected)} />
    ) : undefined,
  };
}

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
  const [busy, setBusy] = useState(false);
  const trainRef = useRef<HTMLDivElement>(null);
  // Метки фото — только при включённом character-smart-photos (ADR-027 §10)
  const smart = useFeature(FLAGS.characterSmartPhotos);
  // Выбранное для правки фото (ключ = имя файла); null — редактор закрыт
  const [selected, setSelected] = useState<string | null>(null);
  const [savingLabel, setSavingLabel] = useState<string | null>(null);
  const [labelError, setLabelError] = useState<string | null>(null);
  useEffect(() => {
    if (focus === 'train') trainRef.current?.scrollIntoView?.({ block: 'start' });
  }, [focus]);

  // Живое обновление меток: пока открыта шторка и есть фото «Размечаем…», перечитываем карточку
  // персонажа раз в 5 с (метки готовы за секунды, а шторка узнаёт о них только при следующем
  // открытии — дефект «23 минуты Размечаем…»). Интервал стабилен по булеву и slug (не по объекту
  // character): один запрос на тик, а не на каждый рендер — не повтор шторма 779d926a9.
  const needsPolling = hasPendingLabels(character.photos);
  useEffect(() => {
    if (!needsPolling) return;
    const id = window.setInterval(() => {
      void api.listCharacters(projectId)
        .then(list => {
          const fresh = list.find(c => c.slug === character.slug);
          if (fresh) onChanged(fresh);
        })
        .catch(() => { /* нет связи — следующий тик */ });
    }, 5000);
    return () => window.clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [needsPolling, projectId, character.slug]);

  // Порядок сетки: главное первым, остальные по манифесту
  const ordered = [...character.photos.filter(p => p.primary), ...character.photos.filter(p => !p.primary)];
  const labels = new Map<string, PhotoLabelState>();
  for (const p of ordered) labels.set(p.file, { angle: p.angle, pitch: p.pitch, emotion: p.emotion, labelSource: p.labelSource });

  const photos: FacePhoto[] = ordered
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

  // Правка одной метки: PATCH с photoLabels {файл: {angle, pitch, emotion}}; источник становится human
  const saveLabel = async (file: string, next: LabelEdit) => {
    setSavingLabel(file);
    setLabelError(null);
    try {
      onChanged(await api.updateCharacter(projectId, character.slug, {
        name: character.name, description: character.description ?? undefined, photos: [],
        photoLabels: { [file]: next },
      }));
    } catch {
      setLabelError(LABELS.saveFailed);
    } finally {
      setSavingLabel(null);
    }
  };

  const face = faceSection(smart, {
    labels, selected,
    setSelected: fn => setSelected(fn),
    savingLabel, labelError,
    saveLabel: (f, next) => void saveLabel(f, next),
    removePhoto: f => void save({ photos: [], removePhotos: [f] }),
    ordered,
  });

  return (
    <Modal title={SHEET.photos(character.name)} width={MODAL_W.form} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        <div style={{ paddingBottom: SP.md }}>
          <CharacterFacePhotos photos={photos} disabled={busy}
            onAdd={files => save({ photos: files })}
            onRemove={key => save({ photos: [], removePhotos: [key] })}
            {...face} />
        </div>
        <div ref={trainRef} style={{ borderTop: `1px solid ${C.divider}`, paddingTop: SP.md }}>
          <CharacterTrainingPhotos projectId={projectId} slug={character.slug} onCount={onCount}
            title={TRAIN_PHOTOS.title} hint={TRAIN_PHOTOS.hint} />
        </div>
      </div>
    </Modal>
  );
}
