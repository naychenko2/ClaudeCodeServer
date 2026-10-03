// Кадр A или B вкладки «Сцена»: крупная миниатюра 16:9 с именем файла; клик раскрывает меню прямо под
// кадрами (макет v7): «Править кадр», «Нарисовать в «Картинках»», «Из проекта», «С компьютера»,
// «Кадр B сцены N», «Убрать кадр». Миниатюра — файл проекта или картинка нити «Картинок».

import { useRef, useState } from 'react';
import { ArrowLeft, Brush, FolderOpen, Image as ImageIcon, Pencil, Plus, Trash2, Upload } from 'lucide-react';
import { api, Button, C, FS, ICON_SIZE, R, SP, ByClaude } from 'aihome_shell/kit';
import { videoApi, type FrameRef } from '../api';
import { isPersonalScope } from '../scope';
import { frameName } from '../scene/model';
import { frameVersionLabel, imageSrcOf, imageThreadName, useImageFramesVersion } from '../store/imageFrames';
import { TOUCH } from '../useBoxWidth';
import { ic } from './primitives';
import { ProjectPicker } from './ProjectPicker';

export const IMG_RE = /\.(png|jpe?g|webp|gif|bmp)$/i;

// Адрес миниатюры кадра; null — картинки пока нет (нить без версии, личный чат без файла)
export function useFrameSrc(scope: string, sessionId: string | null, f: FrameRef | null): { src: string | null; name: string } {
  useImageFramesVersion(scope, sessionId, f?.kind === 'image');
  if (!f) return { src: null, name: '' };
  if (f.kind === 'file') {
    // Личный чат: файл лежит в рабочей папке чата, не в проекте — отдаёт ручка кадра
    if (isPersonalScope(scope)) return { src: sessionId ? videoApi.frameFileUrl(sessionId, f.path) : null, name: frameName(f) };
    return { src: api.files.fileUrl(scope, f.path) || null, name: frameName(f) };
  }
  const named = sessionId ? imageThreadName(sessionId, f.threadId) : null;
  const src = sessionId ? imageSrcOf(scope, sessionId, f.threadId, f.versionId) : null;
  return { src, name: named ? `${named} · ${frameVersionLabel(sessionId, f.threadId, f.versionId)}` : frameName(f) };
}

export function FrameThumb({ scope, sessionId, frame, label, empty, onClick, active, claude, stale, small }: {
  scope: string; sessionId: string | null; frame: FrameRef | null; label: 'A' | 'B'; empty: string;
  onClick?: () => void; active?: boolean; claude?: boolean; stale?: boolean; small?: boolean;
}) {
  const { src, name } = useFrameSrc(scope, sessionId, frame);
  return (
    <div style={{ flex: 1, minWidth: 0 }} data-video-frame={label}>
      <button type="button" onClick={onClick} aria-expanded={active} title={frame ? `Кадр ${label}: ${name}` : `Выбрать кадр ${label}`}
        style={{
          position: 'relative', display: 'block', width: '100%', aspectRatio: '16 / 9', padding: 0, overflow: 'hidden',
          border: `1px ${frame ? 'solid' : 'dashed'} ${active ? C.accent : C.border}`, borderRadius: R.lg,
          background: src ? C.bgInset : 'transparent', cursor: onClick ? 'pointer' : 'default',
        }}>
        {src
          ? <img src={src} alt={`Кадр ${label}`} style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
          : (
            <span style={{
              display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: SP.xxs, height: '100%',
              padding: SP.xs, fontSize: small ? FS.xs : FS.sm, color: C.textMuted, textAlign: 'center',
            }}>
              {frame ? ic(ImageIcon, ICON_SIZE.md) : ic(Plus, ICON_SIZE.md)}
              <span>{frame ? name : `Кадр ${label}`}</span>
              {!frame && <span style={{ fontSize: FS.xs }}>{empty}</span>}
            </span>
          )}
        <span style={{
          position: 'absolute', left: SP.xs, top: SP.xs, minWidth: ICON_SIZE.md, height: ICON_SIZE.md, padding: `0 ${SP.xs}px`, borderRadius: R.sm,
          background: C.bgCard, color: C.textHeading, fontSize: FS.xs, fontWeight: 700, lineHeight: `${ICON_SIZE.md}px`, textAlign: 'center',
        }}>{label}</span>
      </button>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginTop: SP.xxs, minWidth: 0, fontSize: FS.xs, color: stale ? C.warningText : C.textMuted }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{frame ? name : '—'}</span>
        {claude && <ByClaude />}
      </div>
    </div>
  );
}

export interface FrameMenuProps {
  scope: string;
  personal: boolean;
  isMobile?: boolean;
  slot: 'A' | 'B';
  frame: FrameRef | null;
  // Кадр B прошлой сцены (у кадра A — стык без скачка); null — прошлой сцены нет
  prevB: { frame: FrameRef; sceneName: string } | null;
  folder: string;
  onPick: (f: FrameRef | null) => void;
  onEdit: () => void;
  onDraw: () => void;
  onUpload: (file: File) => void;
  onClose: () => void;
}

function Row({ icon, label, hint, disabled, onClick, danger, touch }: { icon: React.ReactNode; label: string; hint?: string; disabled?: boolean; onClick: () => void; danger?: boolean; touch?: boolean }) {
  return (
    <Button variant="ghost" size="sm" disabled={disabled} onClick={onClick} title={disabled ? hint : undefined}
      style={{ width: '100%', justifyContent: 'flex-start', height: 'auto', minHeight: touch ? TOUCH : 36, padding: `${SP.xs}px ${SP.sm}px`, color: danger ? C.danger : undefined }}>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, textAlign: 'left', minWidth: 0 }}>
        {icon}
        <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0, fontWeight: 400 }}>
          <span>{label}</span>
          {hint && <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'normal' }}>{hint}</span>}
        </span>
      </span>
    </Button>
  );
}

// Меню кадра — раскрывается прямо под кадрами, а не поповером
export function FrameMenu(p: FrameMenuProps) {
  const [mode, setMode] = useState<'menu' | 'project'>('menu');
  const file = useRef<HTMLInputElement>(null);
  const t = !!p.isMobile;
  return (
    <div data-video-frame-menu={p.slot} style={{ marginTop: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md, background: C.bgCard, padding: SP.xxs }}>
      {mode === 'menu' ? (
        <>
          {p.frame && <Row touch={t} icon={ic(Pencil)} label="Править кадр" hint="Откроет «Картинки» на этой картинке" onClick={p.onEdit} />}
          <Row touch={t} icon={ic(Brush)} label="Нарисовать в «Картинках»" hint="Откроет «Картинки», готовая картинка сама станет кадром" onClick={p.onDraw} />
          {p.slot === 'A' && p.prevB && (
            <Row touch={t} icon={ic(ArrowLeft)} label={`Кадр B сцены «${p.prevB.sceneName}»`} hint="Стык без скачка" onClick={() => p.onPick(p.prevB!.frame)} />
          )}
          {p.slot === 'B' && p.prevB && (
            <Row touch={t} icon={ic(ArrowLeft)} label={`Кадр B сцены «${p.prevB.sceneName}»`} onClick={() => p.onPick(p.prevB!.frame)} />
          )}
          {!p.personal && <Row touch={t} icon={ic(FolderOpen)} label="Из проекта" hint="Картинка из папок проекта" onClick={() => setMode('project')} />}
          <Row touch={t} icon={ic(Upload)} label="С компьютера"
            hint={p.personal ? 'Файл останется в этом чате, в проект не попадёт' : 'Файл ляжет в video/…/кадры/ проекта'}
            onClick={() => file.current?.click()} />
          <input ref={file} type="file" accept="image/*" hidden
            onChange={e => { const f = e.target.files?.[0]; e.target.value = ''; if (f) p.onUpload(f); }} />
          {p.frame && <Row touch={t} icon={ic(Trash2)} label="Убрать кадр" danger onClick={() => p.onPick(null)} />}
        </>
      ) : (
        <ProjectPicker scope={p.scope} start={p.folder} accept={IMG_RE} emptyText="В этой папке нет картинок" onBack={() => setMode('menu')} onPick={f => p.onPick({ kind: 'file', path: f })} />
      )}
    </div>
  );
}

