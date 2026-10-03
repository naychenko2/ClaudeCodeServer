// Заготовка панели «Картинки», передаваемая вызовом открытия панели (ключ images, вкладка settings, { preset }).
// Хост передаёт объект прозрачно; здесь разбирается единственное, что «Картинки» понимают
// без знания о вызывающей панели, — нить, которую взять в работу («Править кадр»).
export interface ImagesPreset { threadId: string }

export function parseImagesPreset(raw: unknown): ImagesPreset | null {
  if (!raw || typeof raw !== 'object') return null;
  const t = (raw as Record<string, unknown>).thread;
  return typeof t === 'string' && t.trim() ? { threadId: t.trim() } : null;
}
