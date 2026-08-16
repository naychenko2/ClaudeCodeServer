import { describe, expect, it } from 'vitest';
import { IMAGE_GEN_OFF_HINT, generatorCaption, imageGenFailureText, isImageGenQueued, placeFor } from '../ImageGenNote';
import { IMAGE_PLACE } from '../../lib/imageBackfill';
import type { ImageGenerationSettings, ImagePlaceSettings } from '../../types';

// Подпись «чем рисуется картинка» читает строку СВОЕГО места: у иконки проекта и аватара
// персоны настройка разная, и подписи обязаны расходиться. Ошибка здесь молчаливая —
// диалог просто пообещает не тот генератор.

const place = (key: string, provider: string, active: string | null, model: string | null = null): ImagePlaceSettings => ({
  key,
  title: key === IMAGE_PLACE.icon ? 'Иконка проекта' : 'Аватар персоны',
  provider,
  activeProvider: active,
  enabled: active !== null,
  model,
  models: { fal: key === IMAGE_PLACE.icon ? model : null, glif: null },
});

const settings = (icon: ImagePlaceSettings, avatar: ImagePlaceSettings): ImageGenerationSettings => ({
  providers: [
    { key: 'glif', displayName: 'glif', enabled: true, models: [] },
    { key: 'fal', displayName: 'fal.ai', enabled: true, models: [{ id: 'fal-ai/flux/schnell', displayName: 'Flux Schnell' }] },
  ],
  places: [icon, avatar],
});

describe('подпись генератора', () => {
  const s = settings(
    place(IMAGE_PLACE.icon, 'fal', 'fal', 'fal-ai/flux/schnell'),
    place(IMAGE_PLACE.avatar, 'auto', 'glif'));

  it('у каждого места своя — берётся строка своего места', () => {
    expect(generatorCaption(s, 'icon')).toBe('Рисует fal.ai · Flux Schnell');
    expect(generatorCaption(s, 'avatar')).toBe('Генератор выбирается автоматически — сейчас glif');
  });

  it('явный выбор без модели не дописывает хвост', () => {
    const explicit = settings(place(IMAGE_PLACE.icon, 'glif', 'glif'), place(IMAGE_PLACE.avatar, 'auto', 'glif'));
    expect(generatorCaption(explicit, 'icon')).toBe('Рисует glif');
  });

  it('место без настроенного сервиса говорит, чего не хватает', () => {
    const off = settings(place(IMAGE_PLACE.icon, 'fal', null), place(IMAGE_PLACE.avatar, 'auto', 'glif'));
    expect(generatorCaption(off, 'icon')).toBe(IMAGE_GEN_OFF_HINT);
    expect(generatorCaption(off, 'avatar')).not.toBe(IMAGE_GEN_OFF_HINT);
  });

  it('пока настройка грузится — молчим, иначе мигнёт «выключена»', () => {
    expect(generatorCaption(undefined, 'icon')).toBeNull();
  });

  it('настройка не доехала: подсказку даём только если место и так недоступно по caps', () => {
    expect(generatorCaption(null, 'icon')).toBeNull();
    expect(generatorCaption(null, 'icon', true)).toBe(IMAGE_GEN_OFF_HINT);
  });
});

// Картинка не пришла синхронно — два разных исхода. Заявка принята очередью догоняющей
// генерации (queued от бэкенда) — картинка дорисуется сама, и «генератор не ответил»
// здесь враньё; заявки нет (generate-preview нового проекта) — честный отказ.
describe('текст, когда картинка не пришла', () => {
  it('заявка принята — обещаем картинку, а не отказ', () => {
    expect(imageGenFailureText('icon', true, 'glif'))
      .toBe('Рисуется дольше обычного — иконка появится сама, диалог можно закрыть');
    expect(imageGenFailureText('avatar', true, 'glif'))
      .toBe('Рисуется дольше обычного — аватар появится сам, диалог можно закрыть');
  });

  it('заявки нет — прежний честный отказ с именем генератора', () => {
    expect(imageGenFailureText('icon', false, 'glif')).toBe('glif не ответил, картинка осталась прежней');
    expect(imageGenFailureText('avatar', false, null)).toBe('Генератор не ответил, картинка осталась прежней');
  });
});

describe('признак заявки в очередь', () => {
  it('читается из тела ответа ошибки, а не угадывается', () => {
    expect(isImageGenQueued({ body: { error: 'Не удалось сгенерировать изображение', queued: true } })).toBe(true);
    expect(isImageGenQueued({ body: { error: 'Не удалось сгенерировать изображение' } })).toBe(false);
    expect(isImageGenQueued(new Error('офлайн'))).toBe(false);
    expect(isImageGenQueued(undefined)).toBe(false);
  });
});

describe('поиск строки места', () => {
  const s = settings(place(IMAGE_PLACE.icon, 'auto', 'glif'), place(IMAGE_PLACE.avatar, 'auto', 'glif'));

  it('kind сопоставляется с ключом места бэкенда', () => {
    expect(placeFor(s, 'icon')?.key).toBe('project-icon');
    expect(placeFor(s, 'avatar')?.key).toBe('persona-avatar');
  });

  it('без настройки строки нет', () => {
    expect(placeFor(undefined, 'icon')).toBeNull();
  });
});
