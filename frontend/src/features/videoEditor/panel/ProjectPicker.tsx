import { useEffect, useMemo, useState } from 'react';
import { ArrowLeft, File as FileIcon, Folder } from 'lucide-react';
import { api, Button, C, FS, R, SP, useIsMobile } from 'aihome_shell/kit';
import type { FileEntry } from '../../../types';
import { TOUCH } from '../useBoxWidth';
import { ic } from './primitives';

// Потолок высоты списка файлов и высота строки на десктопе
const LIST_MAX_H = 260;
const ROW_H = 40;
const IMG = /\.(png|jpe?g|webp|gif|bmp)$/i;

// Где человек выбирал в прошлый раз: кадр B открывается там же, где выбрали кадр A (у сцены без папки старт — корень)
const _lastDir = new Map<string, string>();

// Файлы проекта: папки и подходящие файлы (миниатюры у картинок); стартует в заданной папке
export function ProjectPicker({ scope, start, accept, emptyText, onBack, onPick }: {
  scope: string; start: string; accept: RegExp; emptyText: string; onBack: () => void; onPick: (path: string) => void;
}) {
  const mobile = useIsMobile();
  const hit = mobile ? { height: TOUCH, minHeight: TOUCH } : undefined;
  const memKey = `${scope}|${accept.source}`;
  const [dir, setDir] = useState(start || _lastDir.get(memKey) || '');
  const [items, setItems] = useState<FileEntry[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    setItems(null);
    setErr(null);
    api.files.list(scope, dir).then(
      r => { if (alive) setItems(r); },
      (e: Error) => {
        if (!alive) return;
        // Стартовой папки в проекте нет — открываем корень, а не «Not Found»
        if (dir !== '') { setDir(''); return; }
        setErr(e.message || 'Папка не открылась');
        setItems([]);
      },
    );
    return () => { alive = false; };
  }, [scope, dir]);
  const shown = useMemo(() => (items ?? []).filter(i => i.isDirectory || accept.test(i.name))
    .sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name, 'ru')), [items, accept]);
  const up = () => setDir(dir.includes('/') ? dir.slice(0, dir.lastIndexOf('/')) : '');
  return (
    <div data-video-project-picker="">
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: SP.xxs }}>
        <Button size="xs" variant="ghost" leftIcon={ic(ArrowLeft)} onClick={onBack} style={hit}>Назад</Button>
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>/{dir}</span>
        {dir && <Button size="xs" variant="ghost" onClick={up} style={hit}>Вверх</Button>}
      </div>
      <div style={{ maxHeight: LIST_MAX_H, overflow: 'auto' }}>
        {items === null && <div style={{ padding: SP.sm, fontSize: FS.sm, color: C.textMuted }}>Загружаем…</div>}
        {err && <div style={{ padding: SP.sm, fontSize: FS.sm, color: C.warningText }}>{err}</div>}
        {items && !err && shown.length === 0 && <div style={{ padding: SP.sm, fontSize: FS.sm, color: C.textMuted }}>{emptyText}</div>}
        {shown.map(i => (
          <Button key={i.path} variant="ghost" size="sm" onClick={() => { if (i.isDirectory) setDir(i.path); else { _lastDir.set(memKey, dir); onPick(i.path); } }}
            style={{ width: '100%', justifyContent: 'flex-start', minHeight: mobile ? TOUCH : ROW_H, padding: `${SP.xxs}px ${SP.sm}px` }}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
              {i.isDirectory
                ? <span style={{ width: SP.xxl, height: SP.xxl, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>{ic(Folder)}</span>
                : IMG.test(i.name)
                  ? <img src={api.files.fileUrl(scope, i.path)} alt="" loading="lazy" style={{ width: SP.xxl, height: SP.xxl, objectFit: 'cover', borderRadius: R.sm }} />
                  : <span style={{ width: SP.xxl, height: SP.xxl, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>{ic(FileIcon)}</span>}
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 400 }}>{i.name}</span>
            </span>
          </Button>
        ))}
      </div>
    </div>
  );
}
