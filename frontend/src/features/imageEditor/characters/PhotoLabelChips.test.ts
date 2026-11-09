// labelCaption — чистая функция подписи под фото (ADR-027 §1, спека §10.5).
// Проверяем текст и цвет по состоянию меток; наклон в подписи не участвует.

import { describe, expect, it } from 'vitest';
import { C } from 'aihome_shell/kit';
import { hasPendingLabels, isEditorBlocked, labelCaption, type PhotoLabelState } from './PhotoLabelChips';
import { LABELS } from './characterTexts';

describe('labelCaption', () => {
  it('меток не было (все null) — «Размечаем…», курсив, muted', () => {
    const r = labelCaption({});
    expect(r.text).toBe(LABELS.pending);
    expect(r.italic).toBe(true);
    expect(r.color).toBe(C.textMuted);
  });

  it('авторазметка: ракурс + эмоция — «Вполоборота · улыбка», secondary', () => {
    const r = labelCaption({ angle: 'three-quarter', emotion: 'smile', labelSource: 'auto' });
    expect(r.text).toBe('Вполоборота · улыбка');
    expect(r.italic).toBe(false);
    expect(r.color).toBe(C.textSecondary);
  });

  it('ручная правка: тот же текст, но primary', () => {
    const r = labelCaption({ angle: 'front', emotion: 'neutral', labelSource: 'human' });
    expect(r.text).toBe('Анфас · спокойное');
    expect(r.color).toBe(C.textPrimary);
  });

  it('модель ответила, но меток нет (unknown) — «Без меток», muted', () => {
    const r = labelCaption({ angle: 'unknown', emotion: 'unknown', labelSource: 'auto' });
    expect(r.text).toBe(LABELS.none);
    expect(r.color).toBe(C.textMuted);
    expect(r.italic).toBe(false);
  });

  it('только ракурс (эмоции нет) — подпись только ракурсом', () => {
    const r = labelCaption({ angle: 'profile', labelSource: 'auto' });
    expect(r.text).toBe('Профиль');
  });

  it('только эмоция (ракурса нет) — подпись только эмоцией', () => {
    const r = labelCaption({ emotion: 'laugh', labelSource: 'auto' });
    expect(r.text).toBe('смех');
  });

  it('наклон в подписи не показываем: pitch не попадает в текст', () => {
    const r = labelCaption({ pitch: 'up', labelSource: 'auto' });
    // pitch seul — ни ракурса, ни эмоции → «Без меток»
    expect(r.text).toBe(LABELS.none);
  });

  it('смешанно: ракурс известен, эмоция unknown — берём только ракурс', () => {
    const r = labelCaption({ angle: 'front', emotion: 'unknown', labelSource: 'auto' });
    expect(r.text).toBe('Анфас');
  });
});

// isEditorBlocked — блокировка редактора меток (ADR-027 §1).
// Ключевой кейс: pending (меток ещё нет) НЕ блокирует — ручная метка доступна всегда.
describe('isEditorBlocked', () => {
  it('pending (меток ещё нет) и не идёт сохранение — редактор активен', () => {
    expect(isEditorBlocked({}, false)).toBe(false);
    expect(isEditorBlocked({}, undefined)).toBe(false);
  });

  it('идёт сохранение своего PATCH — редактор заблокирован', () => {
    expect(isEditorBlocked({}, true)).toBe(true);
  });

  it('метки уже есть (auto) и не идёт сохранение — редактор активен', () => {
    expect(isEditorBlocked({ angle: 'front', labelSource: 'auto' }, false)).toBe(false);
  });
});

// hasPendingLabels — есть ли фото «Размечаем…» (все поля меток null): шторка «Фото»
// по этому признаку перечитывает карточку. Мутация: считать pending по labelSource alone
// (убрать проверки меток) — тест краснеет (unknown-фото без labelSource снова опрашивается).
describe('hasPendingLabels', () => {
  it('все поля null — фото размечается', () => {
    expect(hasPendingLabels([{}, { angle: null, pitch: null, emotion: null, labelSource: null }])).toBe(true);
    // Мутация: требовать labelSource === 'auto' — тест краснеет
  });

  it('метки есть (auto/human) — не размечается', () => {
    expect(hasPendingLabels([{ angle: 'front', labelSource: 'auto' }])).toBe(false);
    expect(hasPendingLabels([{ angle: 'front', emotion: 'smile', labelSource: 'human' }])).toBe(false);
  });

  it('модель ответила, но меток нет (unknown) — не размечается', () => {
    expect(hasPendingLabels([{ angle: 'unknown', emotion: 'unknown', labelSource: 'auto' }])).toBe(false);
    // Мутация: опираться только на labelSource (без проверок меток) — unknown снова «размечается», тест краснеет
  });

  it('смешанно: одно размечается, другое нет — true', () => {
    expect(hasPendingLabels([{}, { angle: 'front', labelSource: 'auto' }])).toBe(true);
  });

  it('labelSource пуст, но метка есть (angle) — не «размечается»: опора на метки, а не только на labelSource', () => {
    expect(hasPendingLabels([{ angle: 'front', labelSource: null }])).toBe(false);
    // Мутация: опираться только на labelSource (без проверок меток) — такое фото снова «размечается», тест краснеет
  });

  it('пустой список — false', () => {
    expect(hasPendingLabels([])).toBe(false);
  });
});
