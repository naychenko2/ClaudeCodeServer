import { type CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { IMAGE_PLACE, type ImageGenKind } from '../lib/imageBackfill';
import { useImageGenerator } from '../lib/imageGeneration';
import { Button, WaitingIndicator } from './ui';
import type { ImageGenerationSettings, ImagePlaceSettings } from '../types';

// Подпись «чем сейчас рисуется картинка» плюс состояния ожидания и отказа — для мест,
// где человек генерирует изображение. Настройка общая для инстанса, но своя НА МЕСТО
// (иконка проекта и аватар персоны настраиваются отдельно), поэтому подпись читает строку
// своего места. GET /api/image-generation открыт любому авторизованному.

export const IMAGE_GEN_OFF_HINT =
  'Генерация картинок выключена — добавьте ключ fal.ai или токен glif в настройках';

// undefined — настройка ещё грузится, null — запрос не удался (подпись не показываем,
// причину не выдумываем). Сам снимок общий — lib/imageGeneration.
type Settings = ImageGenerationSettings | null | undefined;

// Строка своего места в общей настройке
export function placeFor(s: Settings, kind: ImageGenKind): ImagePlaceSettings | null {
  const key = IMAGE_PLACE[kind];
  return s?.places.find(p => p.key.toLowerCase() === key.toLowerCase()) ?? null;
}

// Кто пойдёт следующим запросом в этом месте: имя провайдера (+ модель, если она задана)
function activeName(s: Settings, place: ImagePlaceSettings | null, withModel = false): string | null {
  const key = place?.activeProvider;
  if (!s || !place || !key) return null;
  const p = s.providers.find(x => x.key.toLowerCase() === key.toLowerCase());
  const name = p?.displayName ?? key;
  if (!withModel || !place.model) return name;
  const model = p?.models.find(m => m.id === place.model)?.displayName ?? place.model;
  return `${name} · ${model}`;
}

// Подпись у кнопки генерации — по настройке СВОЕГО места. Пока настройка грузится —
// молчим (иначе мигнёт «выключена»); disabled — вызывающий уже знает от caps, что
// генерация недоступна: подсказка на случай, если сама настройка не доехала.
export function generatorCaption(s: Settings, kind: ImageGenKind, disabled = false): string | null {
  if (s === undefined) return null;
  const place = placeFor(s, kind);
  if (s === null || !place) return disabled ? IMAGE_GEN_OFF_HINT : null;
  if (!place.enabled) return IMAGE_GEN_OFF_HINT;
  const name = activeName(s, place, true);
  if (!place.provider || place.provider === 'auto') {
    return name ? `Генератор выбирается автоматически — сейчас ${name}` : 'Генератор выбирается автоматически';
  }
  return name ? `Рисует ${name}` : 'Генератор выбирается автоматически';
}

export function ImageGenNote({ kind, status = 'idle', error, onRetry, disabled = false, style }: {
  // Что рисуем: место настройки и текст ожидания
  kind: ImageGenKind;
  status?: 'idle' | 'running' | 'error';
  // Текст отказа от бэкенда (причину сами не придумываем)
  error?: string | null;
  onRetry?: () => void;
  disabled?: boolean;
  style?: CSSProperties;
}) {
  const settings = useImageGenerator();
  const isMobile = useIsMobile();

  const caption = generatorCaption(settings, kind, disabled);
  const waiting = kind === 'icon'
    ? 'Иконка рисуется — появится сама, можно продолжать'
    : 'Аватар рисуется — появится сам, можно продолжать';
  const failed = `${activeName(settings, placeFor(settings, kind)) ?? 'Генератор'} не ответил, картинка осталась прежней`;

  if (!caption && status === 'idle') return null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0, ...style }}>
      {caption && (
        <span style={{ fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.45 }}>
          {caption}
        </span>
      )}

      {status === 'running' && <WaitingIndicator hint={waiting} />}

      {status === 'error' && (
        <div style={{
          display: 'flex', flexDirection: isMobile ? 'column' : 'row',
          alignItems: isMobile ? 'stretch' : 'center', gap: SP.sm,
          padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
        }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: FS.base, color: C.dangerText, fontFamily: FONT.sans, lineHeight: 1.45 }}>
              {failed}
            </div>
            {error && (
              <div style={{
                fontSize: FS.sm, color: C.dangerText, opacity: 0.85, marginTop: SP.xxs,
                fontFamily: FONT.sans, lineHeight: 1.4, wordBreak: 'break-word',
              }}>
                {error}
              </div>
            )}
          </div>
          {onRetry && (
            <Button variant="ghost" size="sm" onClick={onRetry} style={{ flexShrink: 0 }}>
              Попробовать снова
            </Button>
          )}
        </div>
      )}
    </div>
  );
}
