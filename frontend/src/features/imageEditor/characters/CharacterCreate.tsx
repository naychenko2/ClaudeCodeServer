// Создание персонажа в новой панели (спека character-panel-final-spec §9.2): зона загрузки, фото лица
// в памяти (на сервер уходят только вместе с персонажем), имя, описание по кнопке. Согласие — внутри
// CharacterFacePhotos, перед каждой загрузкой; перетаскивание идёт тем же путём и согласие не обходит.

import { useEffect, useRef, useState } from 'react';
import { Upload } from 'lucide-react';
import { BackButton, Button, Field, TextArea, TextField, useIsMobile, ICON_SIZE, ICON_STROKE, C, FONT, FS, R, SP } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from '../api';
import { CharacterFacePhotos, type FacePhoto } from './CharacterFacePhotos';
import { CREATE } from './characterTexts';
import { missingText } from './photos';

type MemPhoto = { key: string; url: string; blob: Blob };

let seq = 0;

export function CharacterCreate({ api, projectId, onCreated, onClose }: {
  api: ImageEditorApi;
  projectId: string;
  onCreated: (c: ImageEditCharacter) => void;
  onClose: () => void;
}) {
  const isMobile = useIsMobile();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [showDescription, setShowDescription] = useState(false);
  const [photos, setPhotos] = useState<MemPhoto[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const addRef = useRef<((files: File[]) => Promise<void>) | null>(null);
  const zoneInput = useRef<HTMLInputElement>(null);

  // blob-URL освобождаем при размонтировании; при удалении фото — сразу
  const live = useRef<MemPhoto[]>([]);
  live.current = photos;
  useEffect(() => () => live.current.forEach(p => URL.revokeObjectURL(p.url)), []);

  const onAdd = async (files: File[]) => {
    const added = files.map(f => ({ key: `n${++seq}`, blob: f as Blob, url: URL.createObjectURL(f) }));
    setPhotos(ps => [...ps, ...added]);
  };
  const onRemove = async (key: string) => {
    const gone = photos.find(p => p.key === key);
    if (gone) URL.revokeObjectURL(gone.url);
    setPhotos(ps => ps.filter(p => p.key !== key));
  };

  const missing = missingText(name, photos.length, true);
  const valid = !missing;

  const submit = async () => {
    if (!valid) return;
    setBusy(true);
    setError(null);
    try {
      const created = await api.createCharacter(projectId, {
        name: name.trim(),
        description: description.trim() || undefined,
        photos: photos.map(p => p.blob),
      });
      onCreated(created);
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  const facePhotos: FacePhoto[] = photos.map((p, i) => ({ key: p.key, url: p.url, primary: i === 0 }));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: SP.md, minWidth: 0 }}>
      <BackButton onClick={onClose} title="Все персонажи">
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Все персонажи</span>
      </BackButton>
      <h2 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.h2, color: C.textHeading }}>{CREATE.title}</h2>
      {!photos.length && (
        <>
          <div
            onDragOver={e => { e.preventDefault(); setDragOver(true); }}
            onDragLeave={() => setDragOver(false)}
            onDrop={e => { e.preventDefault(); setDragOver(false); void addRef.current?.(Array.from(e.dataTransfer.files)); }}>
          <Button variant="dashed" fullWidth disabled={busy}
            onClick={() => zoneInput.current?.click()}
            style={{
              height: 160, borderRadius: R.xl, flexDirection: 'column', gap: SP.xs,
              ...(dragOver ? { borderColor: C.accent, background: C.accentLight } : {}),
            }}>
            <Upload size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
            <span style={{ fontSize: FS.base }}>{CREATE.subtitle}</span>
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>{CREATE.hint}</span>
          </Button>
          </div>
          <input ref={zoneInput} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
            onChange={e => { void addRef.current?.(Array.from(e.target.files ?? [])); e.target.value = ''; }} />
        </>
      )}
      <CharacterFacePhotos photos={facePhotos} onAdd={onAdd} onRemove={onRemove} disabled={busy} addRef={addRef} />
      {photos.length > 0 && <div style={{ fontSize: FS.sm, color: C.textMuted }}>{CREATE.mainTip}</div>}
      <Field label="Имя">
        <TextField value={name} onChange={setName} placeholder="Например, Аня" autoFocus={!isMobile} />
      </Field>
      {showDescription ? (
        <Field label="Описание" hint="Уходит в запрос вместе с фото: возраст, причёска, приметы">
          <TextArea value={description} onChange={setDescription} minHeight={56} autoGrow maxHeight={140}
            placeholder="девушка 25 лет, каре, веснушки" />
        </Field>
      ) : (
        <div><Button variant="ghostAccent" size="sm" onClick={() => setShowDescription(true)}>{CREATE.addDescription}</Button></div>
      )}
      {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      <Button variant="primary" size="lg" fullWidth loading={busy} disabled={!valid || busy} onClick={submit}>{CREATE.submit}</Button>
      {!valid && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, textAlign: 'center' }}>Не хватает: {missing}</div>
      )}
    </div>
  );
}
