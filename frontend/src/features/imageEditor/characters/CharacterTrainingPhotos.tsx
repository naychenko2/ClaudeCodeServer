// Секция «Фото для обучения» в карточке сохранённого персонажа (ADR-026 §6.1, макет character-lora-train).
// Фото уходят на сервер сразу, а не вместе с формой: обучение смотрит на сохранённую папку.

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertCircle, File as FileIcon, Upload, X } from 'lucide-react';
import { Badge, Button, IconButton, ProgressBar, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { loraTrainApi, type TrainingPhotoRejectReason, type TrainingPhotosDto } from '../api';
import { shrinkPhoto } from './photos';
import { TRAIN_PHOTOS_TEXT } from './loraTrainTexts';
import {
  chunk, photosCounter, photosState, photosStatus, rejectText, splitByRoom, uploadPercent, uploadProgressText,
  TRAIN_GRID_CAP, TRAIN_MAX, TRAIN_MIN,
} from './trainingPhotos';

type Rejected = { key: number; fileName: string; reason: TrainingPhotoRejectReason };
type Upload = { done: number; total: number };

let rejectSeq = 0;
const STATUS_COLOR = { muted: C.textMuted, warning: C.warningText, success: C.successText } as const;

export function CharacterTrainingPhotos({ projectId, slug, disabled, onCount }: {
  projectId: string;
  slug: string;
  disabled?: boolean;
  // Сколько фото сейчас в наборе — нужно разделу обучения
  onCount?: (n: number) => void;
}) {
  const [dto, setDto] = useState<TrainingPhotosDto | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [upload, setUpload] = useState<Upload | null>(null);
  const [rejected, setRejected] = useState<Rejected[]>([]);
  const [showAll, setShowAll] = useState(false);
  const [busyName, setBusyName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  // Поколение персонажа: ответы, пришедшие после смены slug, отбрасываются
  const epoch = useRef(0);

  const reload = useCallback(() => {
    const my = epoch.current;
    setLoadError(false);
    loraTrainApi.photos(projectId, slug)
      .then(d => { if (my === epoch.current) setDto(d); })
      .catch(() => { if (my === epoch.current) setLoadError(true); });
  }, [projectId, slug]);
  useEffect(() => {
    epoch.current++;
    setDto(null); setRejected([]); setError(null); setUpload(null); setBusyName(null); setShowAll(false);
    reload();
  }, [reload]);

  useEffect(() => { if (dto) onCount?.(dto.count); }, [dto, onCount]);

  const min = dto?.min ?? TRAIN_MIN;
  const max = dto?.max ?? TRAIN_MAX;
  const count = dto?.count ?? 0;
  const items = dto?.items ?? [];
  const state = photosState(count, !!upload, min, max);
  const status = photosStatus(state, count, min);
  const addRejected = (list: { fileName: string; reason: TrainingPhotoRejectReason }[]) =>
    setRejected(r => [...r, ...list.map(x => ({ ...x, key: ++rejectSeq }))]);

  const addFiles = async (files: FileList | null) => {
    if (!files?.length || upload) return;
    const my = epoch.current;
    setError(null);
    const { accepted, overflow } = splitByRoom(Array.from(files), count, max);
    if (overflow.length) addRejected(overflow.map(f => ({ fileName: f.name, reason: 'limit' as const })));
    const prepared: File[] = [];
    const local: { fileName: string; reason: TrainingPhotoRejectReason }[] = [];
    setUpload({ done: 0, total: accepted.length });
    try {
      for (const f of accepted) {
        try { prepared.push(await shrinkPhoto(f) as File); } catch { local.push({ fileName: f.name, reason: 'format' }); }
        if (my !== epoch.current) return;
      }
      if (local.length) addRejected(local);
      let done = local.length;
      for (const part of chunk(prepared)) {
        const r = await loraTrainApi.addPhotos(projectId, slug, part);
        if (my !== epoch.current) return;
        setDto(r.photos);
        if (r.rejected.length) addRejected(r.rejected);
        done += part.length;
        setUpload({ done, total: accepted.length });
      }
    } catch (e) {
      if (my !== epoch.current) return;
      setError((e as Error).message);
      reload();
    } finally {
      if (my === epoch.current) setUpload(null);
    }
  };

  const remove = async (name: string) => {
    const my = epoch.current;
    setBusyName(name); setError(null);
    try {
      await loraTrainApi.deletePhoto(projectId, slug, name);
      if (my !== epoch.current) return;
      setDto(d => d && { ...d, items: d.items.filter(p => p.name !== name), count: Math.max(0, d.count - 1) });
      reload();
    } catch (e) {
      if (my !== epoch.current) return;
      setError((e as Error).message);
    } finally {
      if (my === epoch.current) setBusyName(null);
    }
  };

  const addBtn = (primary?: boolean) => (
    <Button variant={primary ? 'primary' : 'secondary'} size="sm"
      disabled={disabled || !!upload || state === 'limit' || !dto} onClick={() => input.current?.click()}>
      <Upload size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />{TRAIN_PHOTOS_TEXT.add}
    </Button>
  );

  const total = upload ? items.length + upload.total : items.length;
  const visible = showAll ? total : Math.min(total, TRAIN_GRID_CAP);
  const tone = count >= min ? 'success' : count === 0 ? 'neutral' : 'warning';

  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px` }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{TRAIN_PHOTOS_TEXT.title}</span>
        <Badge tone={tone} size="sm">{upload ? `${count}+${upload.total}` : count} из {max}</Badge>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: `0 ${SP.md}px ${SP.md}px`, minWidth: 0 }}>
        <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{TRAIN_PHOTOS_TEXT.hint}</div>
        {!dto && !loadError && <div style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаю список…</div>}
        {loadError && (
          <div style={{ display: 'flex', gap: SP.sm, alignItems: 'center', flexWrap: 'wrap', fontSize: FS.sm, color: C.dangerText }}>
            Не удалось получить список фото.
            <Button variant="ghost" size="sm" onClick={reload}>Повторить</Button>
          </div>
        )}
        {dto && state === 'empty' && (
          <div style={{
            border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl, padding: `${SP.lg}px ${SP.md}px`,
            display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, textAlign: 'center',
          }}>
            <p style={{ margin: 0, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45, maxWidth: 280 }}>{TRAIN_PHOTOS_TEXT.empty}</p>
            {addBtn(true)}
          </div>
        )}
        {dto && state !== 'empty' && (
          <>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap', minWidth: 0 }}>
              <span style={{ fontSize: FS.sm, fontWeight: 600, color: count >= min ? C.successText : count === 0 ? C.textMuted : C.warningText }}>
                {photosCounter(count, min, max)}
              </span>
              {addBtn()}
            </div>
            {upload && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                <div style={{ fontSize: FS.sm, color: C.textMuted }}>{uploadProgressText(upload.done, upload.total)}</div>
                <ProgressBar value={uploadPercent(upload.done, upload.total)} />
              </div>
            )}
            {status && <div style={{ fontSize: FS.sm, lineHeight: 1.45, color: STATUS_COLOR[status.tone] }}>{status.text}</div>}
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(62px, 1fr))', gap: SP.md }}>
              {Array.from({ length: visible }, (_, i) => {
                const p = items[i];
                if (!p) return <div key={`s${i}`} aria-hidden style={{ aspectRatio: '1', borderRadius: R.lg, background: C.bgInset }} />;
                return (
                  <div key={p.name} style={{ position: 'relative', aspectRatio: '1', minWidth: 0 }}>
                    <img src={loraTrainApi.photoUrl(projectId, slug, p.name)} alt="" loading="lazy" style={{
                      width: '100%', height: '100%', objectFit: 'cover', display: 'block',
                      borderRadius: R.lg, border: `1px solid ${C.border}`,
                    }} />
                    <div style={{ position: 'absolute', top: -SP.sm, right: -SP.sm }}>
                      <IconButton size="xs" variant="media" title={TRAIN_PHOTOS_TEXT.remove} ariaLabel={TRAIN_PHOTOS_TEXT.remove}
                        disabled={disabled || !!upload || !!busyName} onClick={() => void remove(p.name)}>
                        <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                    </div>
                  </div>
                );
              })}
            </div>
            {total > TRAIN_GRID_CAP && (
              <div>
                <Button variant="ghost" size="sm" onClick={() => setShowAll(v => !v)}>
                  {showAll ? 'Свернуть' : `Показать все ${total}`}
                </Button>
              </div>
            )}
          </>
        )}
        {rejected.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
            <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary }}>Не подошли · {rejected.length}</div>
            {rejected.map(r => (
              <div key={r.key} style={{
                display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.xs}px ${SP.sm}px`,
                borderRadius: R.md, background: C.dangerBg, minWidth: 0,
              }}>
                <span style={{ display: 'inline-flex', flex: '0 0 auto', color: C.textMuted }}>
                  <FileIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                </span>
                <div style={{ flex: 1, minWidth: 0, fontSize: FS.sm, lineHeight: 1.4 }}>
                  <div style={{ fontSize: FS.xs, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.fileName}</div>
                  <div style={{ color: C.dangerText, overflowWrap: 'anywhere' }}>{rejectText(r.reason)}</div>
                </div>
                <IconButton size="xs" variant="media" title="Скрыть" ariaLabel="Скрыть"
                  onClick={() => setRejected(list => list.filter(x => x.key !== r.key))}>
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
              </div>
            ))}
          </div>
        )}
        {error && (
          <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', fontSize: FS.sm, color: C.dangerText, overflowWrap: 'anywhere' }}>
            <AlertCircle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flex: '0 0 auto', marginTop: 1 }} />{error}
          </div>
        )}
        <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
          onChange={e => { void addFiles(e.target.files); e.target.value = ''; }} />
      </div>
    </div>
  );
}
