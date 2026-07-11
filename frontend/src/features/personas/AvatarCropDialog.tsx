// Диалог кропа аватара: круглое окно 280px, drag-панорама (Pointer Events),
// зум колесом и ползунком (1..4). «Применить» рисует квадрат 512×512 в canvas
// по computeCropRect и отдаёт JPEG-blob + параметры кропа наружу.

import { useCallback, useRef, useState } from 'react';
import { C, FONT } from '../../lib/design';
import { Modal, ModalActions } from '../../components/ui';
import { MAX_SCALE, MIN_SCALE, clampOffset, computeCropRect } from '../../lib/avatarCrop';

const WINDOW_SIZE = 280;   // диаметр круглого окна кропа
const OUT_SIZE = 512;      // размер итогового квадрата

export interface AvatarCropResult {
  blob: Blob;
  crop: { scale: number; offsetX: number; offsetY: number };
}

interface Props {
  src: string;                                   // objectURL файла или URL оригинала
  initial?: { scale: number; offsetX: number; offsetY: number } | null;
  title?: string;
  onApply: (result: AvatarCropResult) => Promise<void> | void;
  onClose: () => void;
}

export function AvatarCropDialog({ src, initial, title, onApply, onClose }: Props) {
  const imgRef = useRef<HTMLImageElement | null>(null);
  const [imgSize, setImgSize] = useState<{ w: number; h: number } | null>(null);
  const [scale, setScale] = useState(initial?.scale ?? 1);
  const [offset, setOffset] = useState({ x: initial?.offsetX ?? 0, y: initial?.offsetY ?? 0 });
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Стартовая точка drag: позиция указателя + offset на момент захвата
  const dragRef = useRef<{ px: number; py: number; ox: number; oy: number } | null>(null);

  const onImgLoad = () => {
    const img = imgRef.current;
    if (!img || !img.naturalWidth) return;
    setImgSize({ w: img.naturalWidth, h: img.naturalHeight });
  };

  // Пиксель экрана → пиксель исходника: cover-вписывание × пользовательский зум
  const totalScale = imgSize ? (WINDOW_SIZE / Math.min(imgSize.w, imgSize.h)) * scale : 1;

  const setClampedOffset = useCallback((x: number, y: number, s = scale) => {
    if (!imgSize) return;
    const { offsetX, offsetY } = clampOffset(imgSize.w, imgSize.h, s, x, y);
    setOffset({ x: offsetX, y: offsetY });
  }, [imgSize, scale]);

  const changeScale = (next: number) => {
    const s = Math.min(Math.max(next, MIN_SCALE), MAX_SCALE);
    setScale(s);
    setClampedOffset(offset.x, offset.y, s);
  };

  const onPointerDown = (e: React.PointerEvent) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    dragRef.current = { px: e.clientX, py: e.clientY, ox: offset.x, oy: offset.y };
  };

  const onPointerMove = (e: React.PointerEvent) => {
    const d = dragRef.current;
    if (!d || !imgSize) return;
    // Тянем картинку за указателем: смещение окна — в противоход, в пикселях исходника
    const dx = (e.clientX - d.px) / totalScale;
    const dy = (e.clientY - d.py) / totalScale;
    setClampedOffset(d.ox - dx, d.oy - dy);
  };

  const onPointerUp = () => { dragRef.current = null; };

  const onWheel = (e: React.WheelEvent) => {
    changeScale(scale + (e.deltaY < 0 ? 0.15 : -0.15));
  };

  const apply = async () => {
    const img = imgRef.current;
    if (!img || !imgSize || applying) return;
    setApplying(true);
    setError(null);
    try {
      const rect = computeCropRect(imgSize.w, imgSize.h, scale, offset.x, offset.y, OUT_SIZE);
      const canvas = document.createElement('canvas');
      canvas.width = OUT_SIZE;
      canvas.height = OUT_SIZE;
      const ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('Canvas недоступен');
      ctx.drawImage(img, rect.sx, rect.sy, rect.sSize, rect.sSize, 0, 0, OUT_SIZE, OUT_SIZE);
      const blob = await new Promise<Blob>((resolve, reject) =>
        canvas.toBlob(b => (b ? resolve(b) : reject(new Error('Не удалось получить изображение'))), 'image/jpeg', 0.9));
      await onApply({ blob, crop: { scale, offsetX: offset.x, offsetY: offset.y } });
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось применить кроп');
    } finally {
      setApplying(false);
    }
  };

  // Позиция картинки: центр исходника + offset должен попасть в центр окна
  const imgStyle: React.CSSProperties = imgSize ? {
    position: 'absolute',
    left: WINDOW_SIZE / 2 - (imgSize.w / 2 + offset.x) * totalScale,
    top: WINDOW_SIZE / 2 - (imgSize.h / 2 + offset.y) * totalScale,
    width: imgSize.w * totalScale,
    height: imgSize.h * totalScale,
    maxWidth: 'none',
    userSelect: 'none',
    pointerEvents: 'none',
  } : { opacity: 0, position: 'absolute' };

  return (
    <Modal
      title={title ?? 'Кадрирование аватара'}
      subtitle="Перетащите картинку и подберите масштаб"
      width={360}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Применить"
          onConfirm={apply}
          loading={applying}
          confirmDisabled={!imgSize}
          onCancel={onClose}
        />
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 14 }}>
        {/* Квадратная сцена с круглым окном: затемнение вне круга — box-shadow «дыркой» */}
        <div
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onWheel={onWheel}
          style={{
            position: 'relative', width: WINDOW_SIZE, height: WINDOW_SIZE,
            overflow: 'hidden', borderRadius: 12, background: '#111',
            cursor: 'grab', touchAction: 'none',
          }}
        >
          {/* eslint-disable-next-line jsx-a11y/alt-text */}
          <img ref={imgRef} src={src} aria-hidden draggable={false} onLoad={onImgLoad} style={imgStyle} />
          {/* Круглая маска поверх: всё вне круга затемняется */}
          <div style={{
            position: 'absolute', inset: 0, borderRadius: '50%', pointerEvents: 'none',
            boxShadow: '0 0 0 9999px rgba(0,0,0,0.55)',
            border: '2px solid rgba(255,255,255,0.85)', boxSizing: 'border-box',
          }} />
        </div>

        {/* Зум */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, width: WINDOW_SIZE }}>
          <span style={zoomSign}>−</span>
          <input
            type="range"
            min={MIN_SCALE}
            max={MAX_SCALE}
            step={0.05}
            value={scale}
            onChange={e => changeScale(Number(e.target.value))}
            aria-label="Масштаб"
            style={{ flex: 1, accentColor: C.accent }}
          />
          <span style={zoomSign}>+</span>
        </div>

        {error && (
          <span style={{ fontSize: 12, color: C.dangerText, fontFamily: FONT.sans }}>{error}</span>
        )}
      </div>
    </Modal>
  );
}

const zoomSign: React.CSSProperties = {
  fontSize: 16, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1, userSelect: 'none',
};
