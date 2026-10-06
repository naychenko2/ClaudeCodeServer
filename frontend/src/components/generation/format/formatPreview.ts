// Геометрия превью «исходник в целевой рамке» (макет media-format-v1, pvHtml). Только вёрстка кадра:
// размеры результата («1600×1600», «1200×1200») приходят от сервера или из вида, здесь они не считаются.

export type PreviewMode = 'pad' | 'crop';

export interface PreviewRects {
  // Холст превью в условных единицах (высота H, ширина по пропорции)
  width: number;
  height: number;
  // Исходник внутри холста
  image: { x: number; y: number; w: number; h: number };
  // Рамка цели (у «обрезать» — то, что останется)
  frame: { x: number; y: number; w: number; h: number };
}

export function ratioOf(aspect: string): number | null {
  const [a, b] = aspect.split(':').map(Number);
  return a > 0 && b > 0 ? a / b : null;
}

// pad — «дорисовать»: рамка цели, внутри исходник; crop — «обрезать»: исходник целиком, рамка цели поверх
export function previewRects(sourceAspect: string, targetAspect: string, mode: PreviewMode, anchor: 'left' | 'center' | 'right', H: number): PreviewRects | null {
  const sr = ratioOf(sourceAspect);
  const tr = ratioOf(targetAspect);
  if (!sr || !tr) return null;
  if (mode === 'pad') {
    const W = H * tr;
    const iw = sr > tr ? W : H * sr;
    const ih = sr > tr ? W / sr : H;
    return { width: W, height: H, image: { x: (W - iw) / 2, y: (H - ih) / 2, w: iw, h: ih }, frame: { x: 0, y: 0, w: W, h: H } };
  }
  const W = H * sr;
  const fw = sr > tr ? H * tr : W;
  const fh = sr > tr ? H : W / tr;
  const fx = anchor === 'left' ? 0 : anchor === 'right' ? W - fw : (W - fw) / 2;
  return { width: W, height: H, image: { x: 0, y: 0, w: W, h: H }, frame: { x: fx, y: (H - fh) / 2, w: fw, h: fh } };
}
