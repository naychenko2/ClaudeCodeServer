// Блок «Лицо для узнавания» (спека character-panel-final-spec §8.1.1): сетка фото лица, согласие
// перед каждой загрузкой, удаление с подтверждением. Управляемый: на сервер сам не ходит,
// загрузку и удаление выполняет родитель (onAdd / onRemove).

import { useEffect, useRef, useState, type MutableRefObject, type ReactNode } from 'react';
import { ShieldAlert, Upload, X } from 'lucide-react';
import { Button, Checkbox, ConfirmDialog, IconButton, Notice, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { CHARACTER_PHOTOS_NOTICE, FACES, REMOVE_PHOTO } from './characterTexts';
import { MAX_PHOTOS, MIN_PHOTOS, addGate, photoSizeError, pickFaceFiles, shrinkPhoto, uploadWithConsent } from './photos';

export type FacePhoto = { key: string; url: string; primary: boolean };

export function CharacterFacePhotos({ photos, onAdd, onRemove, uploading, disabled, renderCaption, onSelectPhoto, selectedKey, labelsEditor, addRef, notice = CHARACTER_PHOTOS_NOTICE }: {
  photos: FacePhoto[];
  onAdd: (files: File[]) => Promise<void>;
  onRemove: (key: string) => Promise<void>;
  uploading?: boolean;
  disabled?: boolean;
  // Места под метки фото (шаг 15): подпись под фото, выбор фото, редактор под сеткой
  renderCaption?: (photo: FacePhoto) => ReactNode;
  onSelectPhoto?: (photo: FacePhoto) => void;
  // Выбранное фото (для aria-pressed); без onSelectPhoto не используется
  selectedKey?: string | null;
  labelsEditor?: ReactNode;
  // Вход для файлов извне (зона перетаскивания создания): идёт через тот же путь с проверкой согласия
  addRef?: MutableRefObject<((files: File[]) => Promise<void>) | null>;
  // Текст согласия; по умолчанию прежний (без меток)
  notice?: string;
}) {
  const [consent, setConsent] = useState(false);
  const [pending, setPending] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [removing, setRemoving] = useState<FacePhoto | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);

  const busy = !!disabled || !!uploading || pending > 0;
  const atMin = photos.length <= MIN_PHOTOS;

  const addFiles = async (list: File[]) => {
    const gate = addGate(consent, list.length);
    if (gate === 'consent') { setError(FACES.consentFirst); return; }
    if (gate === 'empty') return;
    setError(null);
    const { picked, error: pickError } = pickFaceFiles(list, photos.length);
    if (!picked.length) { setError(pickError); return; }
    setPending(picked.length);
    const ready: File[] = [];
    let problem = pickError;
    for (const f of picked) {
      try {
        const blob = await shrinkPhoto(f);
        const tooBig = photoSizeError(f.name, blob.size);
        if (tooBig) problem = tooBig; else ready.push(blob as File);
      } catch (e) {
        problem = (e as Error).message;
      }
    }
    try {
      await uploadWithConsent(consent, ready, onAdd, () => setConsent(false));
    } catch (e) {
      problem = (e as Error).message;
    } finally {
      if (alive.current) { setPending(0); setError(problem); }
    }
  };

  const confirmRemove = async () => {
    const photo = removing;
    setRemoving(null);
    if (!photo) return;
    try {
      await onRemove(photo.key);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  if (addRef) addRef.current = addFiles;
  useEffect(() => () => { if (addRef) addRef.current = null; }, [addRef]);

  const placeholders = pending || (uploading ? 1 : 0);
  const cell: React.CSSProperties = {
    width: '100%', aspectRatio: '1', objectFit: 'cover', display: 'block',
    borderRadius: R.lg, border: `1px solid ${C.border}`,
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: SP.sm }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{FACES.title}</span>
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{FACES.counter(photos.length)}</span>
      </div>
      <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
        {FACES.hint}{onSelectPhoto ? ` ${FACES.hintLabels}` : ''}
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(72px, 1fr))', gap: SP.sm }}>
        {photos.map(p => {
          const image = <img src={p.url} alt="" style={cell} />;
          return (
            <div key={p.key} style={{ position: 'relative', minWidth: 0 }}>
              {onSelectPhoto ? (
                <button type="button" aria-pressed={p.key === selectedKey} onClick={() => onSelectPhoto(p)}
                  style={{ display: 'block', width: '100%', padding: 0, border: 'none', background: 'none', cursor: 'pointer' }}>
                  {image}
                </button>
              ) : <div>{image}</div>}
              {p.primary && (
                <span style={{
                  position: 'absolute', left: SP.xs, top: `calc(100% - ${SP.xs}px)`, transform: 'translateY(-100%)',
                  padding: `0 ${SP.xs}px`, fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4,
                  background: C.accent, color: C.onAccent, borderRadius: R.sm, pointerEvents: 'none',
                }}>Основное</span>
              )}
              <div style={{ position: 'absolute', top: -SP.sm, right: -SP.sm }}>
                <IconButton size="xs" variant="media" ariaLabel={atMin ? FACES.minReached : FACES.remove}
                  title={atMin ? FACES.minReached : FACES.remove} disabled={busy || atMin}
                  onClick={() => setRemoving(p)}>
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
              </div>
              {renderCaption && <div style={{ marginTop: SP.xs }}>{renderCaption(p)}</div>}
            </div>
          );
        })}
        {Array.from({ length: placeholders }, (_, i) => (
          <div key={`up${i}`} style={{
            aspectRatio: '1', borderRadius: R.lg, background: C.bgInset, display: 'flex',
            alignItems: 'center', justifyContent: 'center', textAlign: 'center',
            fontSize: FS.xs, color: C.textMuted,
          }}>Загружаем…</div>
        ))}
        {photos.length + placeholders < MAX_PHOTOS && (
          <Button variant="dashed" size="sm" loading={!!uploading || pending > 0}
            disabled={!!disabled || !consent} title={consent ? undefined : FACES.consentFirst}
            onClick={() => input.current?.click()}
            style={{ aspectRatio: '1', padding: SP.xs, flexDirection: 'column', gap: SP.xxs, fontSize: FS.xs, height: 'auto' }}>
            <Upload size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            {photos.length ? FACES.add : FACES.upload}
          </Button>
        )}
      </div>
      <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
        onChange={e => { void addFiles(Array.from(e.target.files ?? [])); e.target.value = ''; }} />
      {labelsEditor}
      {error && <div style={{ fontSize: FS.sm, color: C.dangerText, overflowWrap: 'anywhere' }}>{error}</div>}
      <Notice tone="warning" icon={ShieldAlert}>{notice}</Notice>
      <label style={{ display: 'flex', gap: SP.xs, alignItems: 'center', fontSize: FS.sm, color: C.textPrimary }}>
        <Checkbox checked={consent} onChange={setConsent} disabled={!!disabled || pending > 0} />
        <span>{FACES.consentLabel}</span>
      </label>
      {removing && (
        <ConfirmDialog title={REMOVE_PHOTO.title} subtitle={REMOVE_PHOTO.body}
          confirmLabel={REMOVE_PHOTO.confirm} cancelLabel={REMOVE_PHOTO.cancel} confirmVariant="danger"
          onConfirm={confirmRemove} onCancel={() => setRemoving(null)} />
      )}
    </div>
  );
}
