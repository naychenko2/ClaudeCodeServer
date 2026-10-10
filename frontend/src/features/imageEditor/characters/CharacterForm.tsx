// Форма персонажа внутри панели «Персонажи» (записка v3, раздел «Персонажи»): создание и
// правка — имя, описание для запроса, 3–10 фото лица. Плашка про фото — дословно из
// постановки, её текст сверяет приёмка. Согласие не запоминается, «Сохранить» недоступна
// без имени, трёх фото и галочки — под кнопкой подсказка, чего не хватает.

import { useEffect, useRef, useState } from 'react';
import { ShieldAlert, Trash2, Upload, X } from 'lucide-react';
import { BackButton, Button, Checkbox, ConfirmDialog, Field, IconButton, TextArea, TextField, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { characterSlug, type ImageEditCharacter, type ImageEditorApi } from '../api';
import { putCharacter, reloadCharacters } from './useCharacters';
import { useLoraVersions } from './useLoraVersions';
import { CharacterLoraSection, type LoraInput } from './CharacterLoraSection';
import { CharacterLoraTrainSection } from './CharacterLoraTrainSection';
import { CharacterLoraVersions } from './CharacterLoraVersions';
import { CharacterTrainingPhotos } from './CharacterTrainingPhotos';
import { MAX_PHOTO_MB, MAX_PHOTOS, MIN_PHOTOS, photosCountText, shrinkPhoto } from './photos';

export const CHARACTER_PHOTOS_NOTICE =
  'Фото будут отправляться выбранному сервису рисования при каждой генерации. У Higgsfield они попадут в общий аккаунт администратора. Загружайте фото чужих людей только с их согласия';

type Photo = { key: string; url: string } & ({ kind: 'kept'; file: string } | { kind: 'new'; blob: Blob });

let photoSeq = 0;

export function CharacterForm({ api, projectId, character, onSaved, onDeleted, onClose }: {
  api: ImageEditorApi;
  projectId: string;
  // null — новый персонаж
  character: ImageEditCharacter | null;
  onSaved: (c: ImageEditCharacter, created: boolean) => void;
  onDeleted: (slug: string) => void;
  onClose: () => void;
}) {
  // Один контроллер версий на обе секции: подключение из «Обучения» сразу виден в списке версий
  const versions = useLoraVersions(character ? projectId : null, character?.slug ?? null, c => {
    if (c) putCharacter(projectId, c); else void reloadCharacters(projectId);
  });
  const [name, setName] = useState(character?.name ?? '');
  const [trainPhotos, setTrainPhotos] = useState<number | null>(null);
  const [description, setDescription] = useState(character?.description ?? '');
  // основное фото — первым (бейдж «Основное» стоит на photos[0]), остальные в прежнем порядке
  const [photos, setPhotos] = useState<Photo[]>(() => {
    const all = character?.photos ?? [];
    return [...all.filter(p => p.primary), ...all.filter(p => !p.primary)].map(p => ({
      kind: 'kept' as const, key: p.file, file: p.file, url: api.characterPhotoUrl(projectId, character!.slug, p.file),
    }));
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  // Согласие не запоминается: фото уезжают при каждой генерации, подтверждать при каждом сохранении
  const [consent, setConsent] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const [lora, setLora] = useState<LoraInput | undefined>(undefined);

  // object URL новых фото живут, пока открыт диалог
  const created = useRef<string[]>([]);
  useEffect(() => () => created.current.forEach(u => URL.revokeObjectURL(u)), []);

  const addFiles = async (files: FileList | null) => {
    if (!files?.length) return;
    setError(null);
    const room = MAX_PHOTOS - photos.length;
    if (room <= 0) { setError(`Можно не больше ${MAX_PHOTOS} фото. Уберите лишние и загрузите заново.`); return; }
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
    if (files.length > room) setError(`Добавлено не всё: в форме не больше ${MAX_PHOTOS} фото. Уберите лишние, если хотите добавить другие.`);
    setPhotos(ps => [...ps, ...added].slice(0, Math.max(MAX_PHOTOS, ps.length)));
  };

  const valid = !!name.trim() && photos.length >= MIN_PHOTOS && consent;
  const missing = [
    !name.trim() && 'имя',
    photos.length < MIN_PHOTOS && `ещё ${MIN_PHOTOS - photos.length} фото`,
    !consent && 'галочка согласия',
  ].filter(Boolean).join(', ');
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
      ...(lora ? { lora } : {}),
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
      onDeleted(character.slug);
    } catch (e) {
      setError((e as Error).message);
      setConfirmDelete(false);
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: SP.md, minWidth: 0 }}>
      <BackButton onClick={onClose} title="К списку персонажей">
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>
          {character ? character.name : 'Новый персонаж'}
        </span>
      </BackButton>
      <Field label="Имя">
        <TextField value={name} onChange={setName} placeholder="Например, Аня" autoFocus={!character} />
      </Field>
      <Field label="Описание" hint="Уходит в запрос вместе с фото: возраст, причёска, приметы">
        <TextArea value={description} onChange={setDescription} minHeight={56} autoGrow maxHeight={140}
          placeholder="девушка 25 лет, каре, веснушки" />
      </Field>
      <Field label={`Фото лица · от ${MIN_PHOTOS} до ${MAX_PHOTOS}`} hint="Первое фото — главное: по нему AI Home узнаёт лицо в каждой картинке. Остальные добавляют ракурсы и свет.">
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(62px, 1fr))', gap: SP.md }}>
          {photos.map((p, i) => (
            <div key={p.key} style={{ position: 'relative', aspectRatio: '1' }}>
              <img src={p.url} alt="" style={{
                width: '100%', height: '100%', objectFit: 'cover', display: 'block',
                borderRadius: R.lg, border: `1px solid ${C.border}`,
              }} />
              {i === 0 && (
                <span style={{
                  position: 'absolute', left: SP.xs, bottom: SP.xs, padding: `0 ${SP.xs}px`,
                  fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4,
                  background: C.accent, color: C.onAccent, borderRadius: R.sm,
                }}>Основное</span>
              )}
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
        <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45, marginTop: SP.xs }}>
          Для основного подойдёт фото анфас на уровне глаз, на ровном фоне, лицо крупно, голова не запрокинута.
          {' '}Чтобы сменить основное, уберите его — главным станет следующее.
        </div>
      </Field>
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap', fontSize: FS.sm }}>
        <span style={{ color: photos.length >= MIN_PHOTOS ? C.successText : C.warningText }}>{photosCountText(photos.length)}</span>
        <span style={{ color: C.textMuted, overflowWrap: 'anywhere' }}>Папка: characters/{slug}/</span>
      </div>
      {character && <CharacterTrainingPhotos key={character.slug} projectId={projectId} slug={character.slug} disabled={busy} onCount={setTrainPhotos} />}
      {character && <CharacterLoraTrainSection key={character.slug} projectId={projectId} slug={character.slug} versions={versions} hasConnected={!!character.local?.lora} photoCount={trainPhotos} disabled={busy} />}
      {character && <CharacterLoraVersions key={character.slug} versions={versions} disabled={busy} />}
      <CharacterLoraSection key={`${character?.slug ?? 'new'}|${character?.local?.lora ?? ''}|${character?.local?.trigger ?? ''}|${character?.local?.strength ?? ''}`} api={api} projectId={projectId} current={character?.local} disabled={busy} onChange={setLora} />
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
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'center' }}>
        {character && (
          <Button variant="ghost" leftIcon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            disabled={busy} onClick={() => setConfirmDelete(true)}>
            Удалить
          </Button>
        )}
        <span style={{ flex: 1 }} />
        <Button variant="primary" loading={busy} disabled={!valid || busy} onClick={submit}>
          Сохранить
        </Button>
      </div>
      {!valid && missing && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, textAlign: 'right' }}>Не хватает: {missing}</div>
      )}
      {confirmDelete && character && (
        <ConfirmDialog title={`Удалить «${character.name}»?`}
          subtitle={`Папка ${character.path}/ с фото удалится из проекта`}
          confirmLabel="Удалить" confirmVariant="danger"
          onConfirm={remove} onCancel={() => setConfirmDelete(false)} />
      )}
    </div>
  );
}
