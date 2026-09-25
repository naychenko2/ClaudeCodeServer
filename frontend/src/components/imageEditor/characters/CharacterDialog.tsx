// Диалог персонажа (макет image-editor-v1, экран 9 и карточка «⋯»): создание и
// правка — имя, описание для запроса, 3–10 фото лица. Плашка про фото — дословно
// из постановки, её текст сверяет приёмка.

import { useEffect, useRef, useState } from 'react';
import { ShieldAlert, Trash2, Upload, UserPlus, X } from 'lucide-react';
import { Button, Checkbox, ConfirmDialog, Field, IconButton, Modal, TextArea, TextField } from '../../ui';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import { C, FS, R, SP } from '../../../lib/design';
import { characterSlug, type ImageEditCharacter, type ImageEditorApi } from '../../../api/imageEditor';
import { MAX_PHOTO_MB, MAX_PHOTOS, MIN_PHOTOS, photosCountText, shrinkPhoto } from './photos';

export const CHARACTER_PHOTOS_NOTICE =
  'Фото будут отправляться выбранному сервису рисования при каждой генерации. У Higgsfield они попадут в общий аккаунт администратора. Загружайте фото чужих людей только с их согласия';

type Photo = { key: string; url: string } & ({ kind: 'kept'; file: string } | { kind: 'new'; blob: Blob });

let photoSeq = 0;

export function CharacterDialog({ api, projectId, character, connected, onSaved, onDeleted, onToggle, onClose }: {
  api: ImageEditorApi;
  projectId: string;
  // null — новый персонаж
  character: ImageEditCharacter | null;
  connected?: boolean;
  onSaved: (c: ImageEditCharacter, created: boolean) => void;
  onDeleted?: (slug: string) => void;
  // «Подключить к генерации» / «Отключить от генерации» из карточки
  onToggle?: () => void;
  onClose: () => void;
}) {
  const [name, setName] = useState(character?.name ?? '');
  const [description, setDescription] = useState(character?.description ?? '');
  const [photos, setPhotos] = useState<Photo[]>(() => (character?.photos ?? []).map(p => ({
    kind: 'kept', key: p.file, file: p.file, url: api.characterPhotoUrl(projectId, character!.slug, p.file),
  })));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  // Согласие не запоминается: фото уезжают при каждой генерации, подтверждать при каждом сохранении
  const [consent, setConsent] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  // object URL новых фото живут, пока открыт диалог
  const created = useRef<string[]>([]);
  useEffect(() => () => created.current.forEach(u => URL.revokeObjectURL(u)), []);

  const addFiles = async (files: FileList | null) => {
    if (!files?.length) return;
    setError(null);
    const room = MAX_PHOTOS - photos.length;
    const picked = Array.from(files).filter(f => f.type.startsWith('image/')).slice(0, room);
    const added: Photo[] = [];
    for (const f of picked) {
      try {
        const blob = await shrinkPhoto(f);
        if (blob.size > MAX_PHOTO_MB * 1024 * 1024) { setError(`${f.name} больше ${MAX_PHOTO_MB} МБ`); continue; }
        const url = URL.createObjectURL(blob);
        created.current.push(url);
        added.push({ kind: 'new', key: `n${++photoSeq}`, blob, url });
      } catch (e) {
        setError((e as Error).message);
      }
    }
    if (files.length > room) setError(`Не больше ${MAX_PHOTOS} фото`);
    setPhotos(ps => [...ps, ...added].slice(0, MAX_PHOTOS));
  };

  const valid = !!name.trim() && photos.length >= MIN_PHOTOS && consent;
  const slug = character?.slug ?? characterSlug(name || 'имя');

  const submit = async () => {
    if (!valid) return;
    setBusy(true);
    setError(null);
    const req = {
      name: name.trim(),
      description: description.trim() || undefined,
      removePhotos: (character?.photos ?? []).map(p => p.file).filter(f => !photos.some(p => p.kind === 'kept' && p.file === f)),
      photos: photos.flatMap(p => (p.kind === 'new' ? [p.blob] : [])),
    };
    try {
      const saved = character
        ? await api.updateCharacter(projectId, character.slug, req)
        : await api.createCharacter(projectId, req);
      onSaved(saved, !character);
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!character) return;
    try {
      await api.deleteCharacter(projectId, character.slug);
      onDeleted?.(character.slug);
    } catch (e) {
      setError((e as Error).message);
      setConfirmDelete(false);
    }
  };

  const footer = (
    <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'flex-end', width: '100%' }}>
      {character && onDeleted && (
        <Button variant="ghost" leftIcon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          disabled={busy} onClick={() => setConfirmDelete(true)}>
          Удалить
        </Button>
      )}
      <span style={{ flex: 1 }} />
      {character && onToggle && (
        <Button variant="secondary" disabled={busy} onClick={onToggle}>
          {connected ? 'Отключить от генерации' : 'Подключить к генерации'}
        </Button>
      )}
      <Button variant="primary" loading={busy} disabled={!valid || busy} onClick={submit}>
        {character ? 'Сохранить' : 'Сохранить персонажа'}
      </Button>
    </div>
  );

  return (
    <>
      <Modal width={480} onClose={onClose} footer={footer}
        title={character ? character.name : (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm }}>
            <UserPlus size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />Новый персонаж
          </span>
        )}
        subtitle={character ? `${character.path}/ · ${character.photos.length} фото` : undefined}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
          <Field label="Имя">
            <TextField value={name} onChange={setName} placeholder="Например, Аня" autoFocus={!character} />
          </Field>
          <Field label="Описание" hint="Уходит в запрос вместе с фото: возраст, причёска, приметы">
            <TextArea value={description} onChange={setDescription} minHeight={56} autoGrow maxHeight={140}
              placeholder="девушка 25 лет, каре, веснушки" />
          </Field>
          <Field label={`Фото лица · от ${MIN_PHOTOS} до ${MAX_PHOTOS}`} hint="Лучше разные ракурсы и освещение, лицо крупно.">
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(62px, 1fr))', gap: SP.md }}>
              {photos.map(p => (
                <div key={p.key} style={{ position: 'relative', aspectRatio: '1' }}>
                  <img src={p.url} alt="" style={{
                    width: '100%', height: '100%', objectFit: 'cover', display: 'block',
                    borderRadius: R.lg, border: `1px solid ${C.border}`,
                  }} />
                  <div style={{ position: 'absolute', top: -SP.sm, right: -SP.sm }}>
                    <IconButton size="xs" variant="media" title="Убрать фото" ariaLabel="Убрать фото" disabled={busy}
                      onClick={() => setPhotos(ps => ps.filter(x => x.key !== p.key))}>
                      <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                    </IconButton>
                  </div>
                </div>
              ))}
              {photos.length < MAX_PHOTOS && (
                <Button variant="dashed" size="sm" disabled={busy} onClick={() => input.current?.click()}
                  style={{ aspectRatio: '1', padding: SP.xs, flexDirection: 'column', gap: SP.xxs, fontSize: FS.xs, height: 'auto' }}>
                  <Upload size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  {photos.length ? 'Ещё фото' : 'Загрузить'}
                </Button>
              )}
            </div>
            <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
              onChange={e => { void addFiles(e.target.files); e.target.value = ''; }} />
          </Field>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap', fontSize: FS.sm }}>
            <span style={{ color: photos.length >= MIN_PHOTOS ? C.successText : C.warningText }}>{photosCountText(photos.length)}</span>
            <span style={{ color: C.textMuted, overflowWrap: 'anywhere' }}>Папка: characters/{slug}/</span>
          </div>
          <div style={{
            display: 'flex', gap: SP.sm, alignItems: 'flex-start', fontSize: FS.sm, lineHeight: 1.45,
            color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
          }}>
            <span style={{ flex: '0 0 auto', display: 'inline-flex', marginTop: 1 }}>
              <ShieldAlert size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </span>
            <span>{CHARACTER_PHOTOS_NOTICE}</span>
          </div>
          <label style={{ display: 'flex', gap: SP.xs, alignItems: 'center', fontSize: FS.sm, color: C.textPrimary }}>
            <Checkbox checked={consent} onChange={setConsent} disabled={busy} ariaLabel="Согласие на отправку фото" />
            <span>Понимаю, у людей на фото есть согласие</span>
          </label>
          {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
        </div>
      </Modal>
      {confirmDelete && character && (
        <ConfirmDialog title={`Удалить персонажа «${character.name}»?`}
          subtitle={`Папка ${character.path}/ с фото удалится из проекта.`}
          confirmLabel="Удалить" confirmVariant="danger"
          onConfirm={remove} onCancel={() => setConfirmDelete(false)} />
      )}
    </>
  );
}
