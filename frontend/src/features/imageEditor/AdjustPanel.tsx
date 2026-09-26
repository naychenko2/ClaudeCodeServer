// Секция «Правка без ИИ» (ADR-018 §9): обрезка, поворот, отражение и «Размер и сжатие».
// Правку делает сервер (transform) отдельным шагом истории, экран показывает её сразу.

import { useState } from 'react';
import { Crop, FlipHorizontal2, FlipVertical2, Link2, Link2Off, RotateCcw, RotateCw } from 'lucide-react';
import { Button, IconButton, SegmentedControl, TextField, ICON_SIZE, ICON_STROKE, C, FS, SP } from 'aihome_shell/kit';
import type { ImageEditorApi, ImageEncodeFormat, ImageEncodeSpec, ImageTransformBase, ImageTransformOp } from './api';
import { SectionHint } from './EditorSections';
import {
  formatBytes, lockedHeight, lockedWidth, presetDims, QUALITY_DEFAULT, QUALITY_MIN, SIZE_PRESETS, sizeFormChanged, sizeFormOps,
  type Dims, type SizeForm,
} from './transforms';
import { useWeight } from './useWeight';

const ic = (I: typeof Crop, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

const FORMATS: { value: ImageEncodeFormat; label: string }[] = [
  { value: 'png', label: 'PNG' }, { value: 'jpeg', label: 'JPG' }, { value: 'webp', label: 'WebP' },
];

const Label = ({ children }: { children: string }) => (
  <div style={{ fontSize: FS.xs, color: C.textMuted, fontWeight: 600 }}>{children}</div>
);

export function AdjustPanel({ api, projectId, stepId, size, base, sourceFormat, beforeBytes, blockReason, cropping, onOp, onCrop, onApply }: {
  api: ImageEditorApi;
  projectId: string;
  // Шаг истории: сменился — форма размера заново от него
  stepId: string;
  // Размер текущего шага; null — картинка ещё грузится
  size: Dims | null;
  // База для веса через dryRun; null — картинки на сервере нет
  base: Promise<ImageTransformBase> | null;
  // Формат текущей картинки; undefined — ещё не узнали (форма ждёт: иначе вес посчитается не от того формата)
  sourceFormat: ImageEncodeFormat | null | undefined;
  beforeBytes: number | null;
  // Почему правка недоступна; пусто — доступна
  blockReason: string;
  cropping: boolean;
  onOp: (op: ImageTransformOp) => void;
  onCrop: () => void;
  onApply: (ops: ImageTransformOp[], encode: ImageEncodeSpec) => void;
}) {
  const off = !!blockReason || !size;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <Label>Кадр</Label>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
          <Button size="sm" variant={cropping ? 'ghostAccent' : 'ghostFilled'} leftIcon={ic(Crop)} disabled={off}
            title={blockReason || 'Обрезать рамкой'} onClick={onCrop}>
            Обрезать
          </Button>
          <IconButton size="sm" title="Повернуть влево" ariaLabel="Повернуть влево" disabled={off} onClick={() => onOp({ type: 'rotate', degrees: 270 })}>{ic(RotateCcw, ICON_SIZE.sm)}</IconButton>
          <IconButton size="sm" title="Повернуть вправо" ariaLabel="Повернуть вправо" disabled={off} onClick={() => onOp({ type: 'rotate', degrees: 90 })}>{ic(RotateCw, ICON_SIZE.sm)}</IconButton>
          <IconButton size="sm" title="Отразить по горизонтали" ariaLabel="Отразить по горизонтали" disabled={off} onClick={() => onOp({ type: 'flip', axis: 'horizontal' })}>{ic(FlipHorizontal2, ICON_SIZE.sm)}</IconButton>
          <IconButton size="sm" title="Отразить по вертикали" ariaLabel="Отразить по вертикали" disabled={off} onClick={() => onOp({ type: 'flip', axis: 'vertical' })}>{ic(FlipVertical2, ICON_SIZE.sm)}</IconButton>
        </div>
      </div>
      {size && !blockReason && sourceFormat !== undefined
        ? <SizeCompress key={`${stepId}:${size.w}x${size.h}:${sourceFormat}`} api={api} projectId={projectId} size={size} base={base}
            sourceFormat={sourceFormat} beforeBytes={beforeBytes} onApply={onApply} />
        : <SectionHint>{blockReason || 'Картинка загружается…'}</SectionHint>}
      <SectionHint>Каждая правка — отдельный шаг истории, оригинал в проекте не меняется.</SectionHint>
    </div>
  );
}

function SizeCompress({ api, projectId, size, base, sourceFormat, beforeBytes, onApply }: {
  api: ImageEditorApi;
  projectId: string;
  size: Dims;
  base: Promise<ImageTransformBase> | null;
  sourceFormat: ImageEncodeFormat | null;
  beforeBytes: number | null;
  onApply: (ops: ImageTransformOp[], encode: ImageEncodeSpec) => void;
}) {
  const [form, setForm] = useState<SizeForm>(() => ({
    unit: 'px', w: size.w, h: size.h, percent: 100, format: sourceFormat ?? 'png', quality: QUALITY_DEFAULT,
  }));
  const [lock, setLock] = useState(true);
  const set = (patch: Partial<SizeForm>) => setForm(f => ({ ...f, ...patch }));

  const { ops, encode, target } = sizeFormOps(size, form);
  const changed = sizeFormChanged(size, form, sourceFormat);
  const valid = target.w >= 1 && target.h >= 1 && form.percent > 0;
  const weight = useWeight(api, projectId, changed && valid ? base : null, ops, encode);

  const num = (v: string) => Math.max(0, Math.round(Number(v.replace(/\D/g, '')) || 0));
  const setW = (w: number) => set(lock ? { w, h: w ? lockedHeight(size, w) : form.h } : { w });
  const setH = (h: number) => set(lock ? { h, w: h ? lockedWidth(size, h) : form.w } : { h });

  const presetOn = (p: number | '50%') => (p === '50%'
    ? form.unit === '%' && form.percent === 50
    : form.unit === 'px' && Math.max(form.w, form.h) === p && form.w === presetDims(size, p).w);

  const weightText = !changed
    ? beforeBytes != null ? `Сейчас ${formatBytes(beforeBytes)}` : ''
    : weight.error ? weight.error
      : `${beforeBytes != null ? `${formatBytes(beforeBytes)} → ` : ''}${weight.bytes != null ? formatBytes(weight.bytes) : '…'}`;

  return (
    <div data-size-compress="true" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <Label>Размер и сжатие</Label>
      <SegmentedControl value={form.unit} onChange={unit => set({ unit })}
        options={[{ value: 'px', label: 'Пиксели' }, { value: '%', label: 'Проценты' }]} />
      {form.unit === 'px' ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField value={form.w ? String(form.w) : ''} onChange={v => setW(num(v))} title="Ширина, px" placeholder="ширина" />
          </div>
          <IconButton size="sm" active={lock} title={lock ? 'Пропорции сохраняются' : 'Пропорции свободны'}
            ariaLabel="Замок пропорций" onClick={() => { if (!lock && form.w) set({ h: lockedHeight(size, form.w) }); setLock(!lock); }}>
            {ic(lock ? Link2 : Link2Off, ICON_SIZE.sm)}
          </IconButton>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField value={form.h ? String(form.h) : ''} onChange={v => setH(num(v))} title="Высота, px" placeholder="высота" />
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
          <div style={{ width: 96 }}>
            <TextField value={form.percent ? String(form.percent) : ''} onChange={v => set({ percent: Math.min(400, num(v)) })} title="Процент от размера" />
          </div>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>% · {target.w}×{target.h}</span>
        </div>
      )}
      <div style={{ display: 'flex', gap: SP.xxs, flexWrap: 'wrap' }}>
        {SIZE_PRESETS.map(p => (
          <Button key={p} size="xs" pill variant={presetOn(p) ? 'ghostAccent' : 'ghostFilled'} title={`${p} px по длинной стороне`}
            onClick={() => { const d = presetDims(size, p); set({ unit: 'px', w: d.w, h: d.h }); setLock(true); }}>
            {String(p)}
          </Button>
        ))}
        <Button size="xs" pill variant={presetOn('50%') ? 'ghostAccent' : 'ghostFilled'} onClick={() => set({ unit: '%', percent: 50 })}>50 %</Button>
      </div>
      <Label>Формат</Label>
      <SegmentedControl value={form.format} onChange={format => set({ format })} options={FORMATS} />
      {form.format !== 'png' && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>Качество</span>
          <input type="range" min={QUALITY_MIN} max={100} step={1} value={form.quality} aria-label="Качество"
            onChange={e => set({ quality: Number(e.target.value) })} style={{ flex: 1, minWidth: 0, accentColor: C.accent }} />
          <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 28, textAlign: 'right' }}>{form.quality}</span>
        </div>
      )}
      <div data-weight="true" style={{ fontSize: FS.sm, color: weight.error ? C.dangerText : C.textPrimary, minHeight: 18 }}>{weightText}</div>
      <Button size="sm" variant="primary" disabled={!changed || !valid} onClick={() => onApply(ops, encode)}>Применить</Button>
    </div>
  );
}
