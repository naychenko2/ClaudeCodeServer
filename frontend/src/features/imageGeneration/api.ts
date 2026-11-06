// Домен «Генерация картинок» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// настройки генератора инстанса по местам. Тонкая обёртка над живым REST; DTO-типы —
// types/index.ts (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type { ImageGenerationPatch, ImageGenerationSettings, ImagePlacePatch } from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.imageGeneration.* остаются прежними для всех потребителей).
export const imageGenerationApi = {
  // Генератор картинок инстанса ПО МЕСТАМ (иконка проекта, аватар персоны): у каждого
  // места свой режим auto|fal|glif и своя модель. Чтение открыто всем (диалоги
  // подписывают, кто и чем рисует), запись — админам. Патч места: поле не прислали —
  // оставить, "" — сброс к конфигу; место вне places не трогается.
  imageGeneration: {
    get: () => request<ImageGenerationSettings>('/image-generation'),
    save: (patch: ImageGenerationPatch) =>
      request<ImageGenerationSettings>('/image-generation', { method: 'PUT', body: JSON.stringify(patch) }),
    savePlace: (place: string, patch: ImagePlacePatch) =>
      request<ImageGenerationSettings>('/image-generation',
        { method: 'PUT', body: JSON.stringify({ places: { [place]: patch } }) }),
  },
};
