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

// Заявку в очередь догоняющей генерации ставит бэкенд (ImageBackfillService) и сообщает
// об этом полем queued в теле ошибки; lib/offline прикрепляет тело к Error целиком.
// Сами не угадываем: у generate-preview нового проекта догонять некому, и там queued нет.
export function isImageGenQueued(err: unknown): boolean {
  const body = (err as { body?: unknown } | null | undefined)?.body;
  return !!body && typeof body === 'object' && (body as { queued?: unknown }).queued === true;
}

// Итог, когда картинка не пришла этим запросом. queued — заявка принята очередью:
// картинка дорисуется фоном, и говорить «генератор не ответил» здесь было бы враньём.
export function imageGenFailureText(kind: ImageGenKind, queued: boolean, generatorName?: string | null): string {
  if (queued) {
    return kind === 'icon'
      ? 'Рисуется дольше обычного — иконка появится сама, диалог можно закрыть'
      : 'Рисуется дольше обычного — аватар появится сам, диалог можно закрыть';
  }
  return `${generatorName ?? 'Генератор'} не ответил, картинка осталась прежней`;
}

export function ImageGenNote({ kind, status = 'idle', error, queued = false, onRetry, disabled = false, style }: {
  // Что рисуем: место настройки и текст ожидания
  kind: ImageGenKind;
  status?: 'idle' | 'running' | 'error';
  // Текст отказа от бэкенда (причину сами не придумываем)
  error?: string | null;
  // Бэкенд принял заявку в очередь догоняющей генерации — это ожидание, а не ошибка
  queued?: boolean;
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
  const failed = imageGenFailureText(kind, queued, activeName(settings, placeFor(settings, kind)));
  // Заявка принята — информационный тон, не красная плашка ошибки
  const tone = queued
    ? { bg: C.infoBg, border: null as string | null, text: C.info }
    : { bg: C.dangerBg, border: C.dangerBorder, text: C.dangerText };

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
          background: tone.bg,
          ...(tone.border ? { border: `1px solid ${tone.border}` } : null),
        }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: FS.base, color: tone.text, fontFamily: FONT.sans, lineHeight: 1.45 }}>
              {failed}
            </div>
            {/* Техническую причину показываем только при отказе: под «появится сама»
                строка «не удалось сгенерировать» противоречила бы обещанию */}
            {error && !queued && (
              <div style={{
                fontSize: FS.sm, color: tone.text, opacity: 0.85, marginTop: SP.xxs,
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
