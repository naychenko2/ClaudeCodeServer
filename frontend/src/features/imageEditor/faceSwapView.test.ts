import { describe, expect, it } from 'vitest';
import type { DetectedFace } from './api';
import {
  FS_TEXT, faceCaption, faceRect, faceSwapEntryVisible, faceSwapView, faceTargetCaption, faceVisual, launchFailure,
  clearFailures, clearFaceChange,
  assignCharacter, emptyAssignments, faceTargetFromAssignments, hasAssignments, isCharacterTaken,
  MAX_FACE_PASSES, parseFacesChangedBody, removeAssignment, searchFailureText, tapFace, modelLabel,
  type FaceSwapViewInput, type FaceUi,
} from './faceSwapView';

const face = (n: number, small = false): DetectedFace => ({ n, bbox: [10, 10, 30, 30], score: 0.9, small });

// Базовый вход: готово, два лица, локальные модели, ни одно не выбрано
const base = (over: Partial<FaceSwapViewInput> = {}): FaceSwapViewInput => ({
  phase: 'ready', faces: [face(1), face(2)], selectedFace: null, selectedCharacter: null,
  error: null, provider: 'local', localAvailable: true, useLocalOnce: false,
  tappedSmall: null, over8: false, facesTooClose: false, faceOutOfFrame: false, busy: false,
  providerUnavailable: false, facesChanged: false, hasMask: false,
  ...over,
});

// ── Масштаб рамок bbox→доли ───────────────────────────────────────────────────
describe('faceRect: bbox в пикселях → доли', () => {
  it('координаты делятся на размеры картинки', () => {
    const f = { bbox: [100, 50, 200, 150] as [number, number, number, number] };
    expect(faceRect(f, 1000, 500)).toEqual({ x: 0.1, y: 0.1, width: 0.1, height: 0.2 });
  });

  it('защита от деления на ноль: размеры 0 не дают NaN/Infinity', () => {
    const f = { bbox: [10, 10, 30, 30] as [number, number, number, number] };
    const r = faceRect(f, 0, 0);
    expect(Number.isFinite(r.x) && Number.isFinite(r.width)).toBe(true);
  });
});

// ── Видимость входа ───────────────────────────────────────────────────────────
describe('faceSwapEntryVisible: вход скрыт', () => {
  const on = { personal: false, flag: true, hasCharacters: true, localAvailable: true };
  it('все условия — вход есть', () => expect(faceSwapEntryVisible(on)).toBe(true));
  it('флаг off — нет', () => expect(faceSwapEntryVisible({ ...on, flag: false })).toBe(false));
  it('личная область — нет', () => expect(faceSwapEntryVisible({ ...on, personal: true })).toBe(false));
  it('нет персонажей — нет', () => expect(faceSwapEntryVisible({ ...on, hasCharacters: false })).toBe(false));
  it('локальные модели недоступны — нет', () => expect(faceSwapEntryVisible({ ...on, localAvailable: false })).toBe(false));
});

// ── canSubmit по матрице ──────────────────────────────────────────────────────
describe('faceSwapView: canSubmit', () => {
  it('лицо+персонаж+local → true', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina' })).canSubmit).toBe(true);
  });
  it('без лица → false', () => {
    expect(faceSwapView(base({ selectedCharacter: 'marina' })).canSubmit).toBe(false);
  });
  it('без персонажа → false', () => {
    expect(faceSwapView(base({ selectedFace: 1 })).canSubmit).toBe(false);
  });
  it('провайдер не local (fal) → false, даже с лицом и персонажем', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', provider: 'fal' })).canSubmit).toBe(false);
  });
  it('локальные недоступны → false', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', localAvailable: false })).canSubmit).toBe(false);
  });
  it('useLocalOnce (9б): не-local переключён на этот запуск → true', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', provider: 'fal', useLocalOnce: true })).canSubmit).toBe(true);
  });
  it('«лиц нет» + закрашено кистью → true (состояние 2)', () => {
    expect(faceSwapView(base({ faces: [], hasMask: true, selectedCharacter: 'marina' })).canSubmit).toBe(true);
  });
  it('«лиц нет» без кисти → false', () => {
    expect(faceSwapView(base({ faces: [], selectedCharacter: 'marina' })).canSubmit).toBe(false);
  });
  it('отказ «близко» (7) → false, пока выбор не изменится', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', facesTooClose: true })).canSubmit).toBe(false);
  });
  it('этап 3: отказ по номеру лица (faceRefused) → false; после снятия назначения (clearFailures) → снова true', () => {
    const text = 'Лицо 2 слишком мелкое';
    const ui = fullUi();
    const f = launchFailure('invalid_request', text, 2);
    if (f.kind !== 'faceRefused') throw new Error('ожидался faceRefused');
    const refused = { ...ui, refusedFace: f.face, refusedText: f.message };
    const viewOf = (u: FaceUi, a = assignCharacter(assignCharacter(emptyAssignments, 1, 'marina'), 2, 'olga')) =>
      faceSwapView(base({ assignments: a, refusedFace: u.refusedFace, refusedText: u.refusedText })).canSubmit;
    expect(viewOf(refused)).toBe(false);
    // «Снять назначение с лица 2»: назначение убрано, отказ сброшен — кнопка оживает
    expect(viewOf(clearFailures(refused), assignCharacter(emptyAssignments, 1, 'marina'))).toBe(true);
  });
});

// ── Плашка режима (шаг) ──────────────────────────────────────────────────────
describe('faceSwapView: плашка режима', () => {
  it('поиск (1): спиннер, текст «Ищу лица…»', () => {
    const v = faceSwapView(base({ phase: 'loading' }));
    expect(v.barIcon).toBe('search');
    expect(v.bar).toContain('Ищу лица');
  });
  it('поиск в очереди (1б): часы, текст про очередь', () => {
    const v = faceSwapView(base({ phase: 'loading', error: 'busy' }));
    expect(v.barIcon).toBe('clock');
    expect(v.bar).toContain('Видеокарта занята');
  });
  it('одно лицо (3): «Нашёлся один человек…»', () => {
    expect(faceSwapView(base({ faces: [face(1)] })).bar).toContain('один человек');
  });
  it('несколько лиц (4): «Нажмите на лицо…»', () => {
    expect(faceSwapView(base()).bar).toContain('Нажмите на лицо');
  });
});

// ── Плашки (notices) ──────────────────────────────────────────────────────────
describe('faceSwapView: плашки', () => {
  it('«лиц нет» (2): info + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ faces: [] }));
    expect(v.notices).toHaveLength(1);
    expect(v.notices[0]).toMatchObject({ tone: 'info', action: { label: 'Закрасить кистью' } });
  });
  it('больше 8 (5): info «Показаны 8 самых крупных лиц»', () => {
    expect(faceSwapView(base({ over8: true })).notices.some(n => n.text.includes('8 самых крупных'))).toBe(true);
  });
  it('мелкое лицо (6): warning + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ tappedSmall: 3 }));
    expect(v.notices.some(n => n.tone === 'warning' && n.action?.label === 'Закрасить кистью')).toBe(true);
  });
  it('«близко» (7): danger + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ facesTooClose: true }));
    expect(v.notices.some(n => n.tone === 'danger' && n.text.includes('близко'))).toBe(true);
  });
  it('face_out_of_frame: danger + «вне картинки» + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ faceOutOfFrame: true }));
    expect(v.notices.some(n => n.tone === 'danger' && n.text.includes('вне картинки') && n.action?.label === 'Закрасить кистью')).toBe(true);
  });
  it('занята долгой задачей (8): danger', () => {
    expect(faceSwapView(base({ busy: true })).notices.some(n => n.tone === 'danger' && n.icon === 'clock')).toBe(true);
  });
  it('провайдер не local (9): warning + «Использовать локальные…»', () => {
    const v = faceSwapView(base({ provider: 'fal' }));
    expect(v.notices.some(n => n.tone === 'warning' && n.action?.label === 'Использовать локальные для этого запуска')).toBe(true);
  });
  // provider_unavailable, когда клиент считал локальные доступными (localOk=true):
  // кнопка «Подставить» активна, отказ приходит — плашка обязана быть, иначе немой.
  // Мутация: вернуть старое `if (!localOk)` вокруг localUnavailable → этот тест краснеет.
  it('provider_unavailable, локальные доступны (local): нейтральная плашка, canSubmit не меняется', () => {
    const withFlag = faceSwapView(base({ provider: 'local', localAvailable: true, providerUnavailable: true }));
    const withoutFlag = faceSwapView(base({ provider: 'local', localAvailable: true, providerUnavailable: false }));
    expect(withFlag.notices.some(n => n.tone === 'info' && n.icon === 'cpu' && n.text.includes('Локальные модели сейчас недоступны') && n.action == null)).toBe(true);
    expect(withFlag.canSubmit).toBe(withoutFlag.canSubmit);
  });
  // provider_unavailable + локальные недоступны: приоритет у нейтральной плашки (причина — недоступность)
  it('provider_unavailable, локальные недоступны: нейтральная плашка, без действия', () => {
    const v = faceSwapView(base({ provider: 'local', localAvailable: false, providerUnavailable: true }));
    expect(v.notices.some(n => n.tone === 'info' && n.icon === 'cpu' && n.text.includes('Локальные модели сейчас недоступны') && n.action == null)).toBe(true);
  });
  it('409 faces_changed (10): info «Картинка изменилась…»', () => {
    expect(faceSwapView(base({ facesChanged: true })).notices.some(n => n.tone === 'info' && n.icon === 'redo')).toBe(true);
  });
});

// ── Список персонажей: доступность ────────────────────────────────────────────
describe('faceSwapView: список персонажей', () => {
  it('до выбора лица (несколько) — недоступен', () => {
    expect(faceSwapView(base()).listEnabled).toBe(false);
  });
  it('лицо выбрано — доступен', () => {
    expect(faceSwapView(base({ selectedFace: 1 })).listEnabled).toBe(true);
  });
  it('«лиц нет» — доступен (после закраски)', () => {
    expect(faceSwapView(base({ faces: [] })).listEnabled).toBe(true);
  });
});

// ── Бейдж «локальные · этот запуск» (9б) ──────────────────────────────────────
describe('faceSwapView: localOnce', () => {
  it('useLocalOnce + локальные доступны → бейдж', () => {
    expect(faceSwapView(base({ provider: 'fal', useLocalOnce: true })).localOnce).toBe(true);
  });
  it('без переключения — нет', () => {
    expect(faceSwapView(base()).localOnce).toBe(false);
  });
});

// ── Состояние рамки лица ─────────────────────────────────────────────────────
describe('faceVisual: рамка', () => {
  const i = { selectedFace: 2, tappedSmall: null, errorFaces: [] };
  it('выбранное → selected', () => expect(faceVisual(face(2), i)).toBe('selected'));
  it('остальные при выбранной → dim', () => expect(faceVisual(face(1), i)).toBe('dim'));
  it('мелкое → small (не выбирается)', () => expect(faceVisual(face(3, true), i)).toBe('small'));
  it('мелкое тапнуто → smallTapped', () => expect(faceVisual(face(3, true), { ...i, tappedSmall: 3 })).toBe('smallTapped'));
  it('«близко» → error', () => expect(faceVisual(face(3), { ...i, errorFaces: [3] })).toBe('error'));
  it('ничего не выбрано → normal', () => expect(faceVisual(face(1), { selectedFace: null, tappedSmall: null, errorFaces: [] })).toBe('normal'));
});

// ── Подпись карточки ──────────────────────────────────────────────────────────
describe('faceCaption', () => {
  it('«{Имя} → лицо {n}»', () => expect(faceCaption('Марина', 2)).toBe('Марина → лицо 2'));
});

describe('faceTargetCaption: выбор подписи на карточке запуска', () => {
  it('faceTarget есть — подпись «{Имя} → лицо {n}»', () => {
    expect(faceTargetCaption({ n: 2, characterSlug: 'marina' }, 'Марина')).toBe('Марина → лицо 2');
  });
  it('faceTarget нет — подписи нет (карточка прежняя)', () => {
    expect(faceTargetCaption(null, 'Марина')).toBeNull();
    expect(faceTargetCaption(undefined, 'Марина')).toBeNull();
  });
  it('имя персонажа по slug; персонаж не найден в списке — fallback на slug', () => {
    expect(faceTargetCaption({ n: 1, characterSlug: 'marina' }, null)).toBe('marina → лицо 1');
    expect(faceTargetCaption({ n: 1, characterSlug: 'marina' }, '  ')).toBe('marina → лицо 1');
  });
});

// ── launchFailure: код отказа запуска → состояние экрана (этап 2 · шаг 10d) ─────
describe('launchFailure: код → состояние', () => {
  it('faces_too_close → facesTooClose (по коду, не по тексту)', () => {
    // message без подстроки «близко»: мутация «подстрока вместо кода» должна покраснеть
    expect(launchFailure('faces_too_close', 'лицо слишком мелкое')).toEqual({ kind: 'facesTooClose' });
  });
  it('face_out_of_frame → faceOutOfFrame', () => {
    expect(launchFailure('face_out_of_frame', 'Лицо вне картинки.')).toEqual({ kind: 'faceOutOfFrame' });
  });
  it('busy → busy (плашка 8)', () => {
    expect(launchFailure('busy', null)).toEqual({ kind: 'busy' });
  });
  it('provider_unavailable → providerUnavailable (плашка 9/нейтрально)', () => {
    expect(launchFailure('provider_unavailable', null)).toEqual({ kind: 'providerUnavailable' });
  });
  it('faces_changed → facesChanged (обновление рамок + сброс выбора)', () => {
    expect(launchFailure('faces_changed', null)).toEqual({ kind: 'facesChanged' });
  });
  it('feature_disabled → featureDisabled (закрытие режима)', () => {
    expect(launchFailure('feature_disabled', null)).toEqual({ kind: 'featureDisabled' });
  });
  it('неизвестный код → generic с сообщением', () => {
    expect(launchFailure('weird_code', 'что-то пошло не так')).toEqual({ kind: 'generic', message: 'что-то пошло не так' });
  });
  it('код null (нет кода в теле) → generic', () => {
    expect(launchFailure(null, 'сеть')).toEqual({ kind: 'generic', message: 'сеть' });
  });
  it('invalid_request + face (мелкое лицо между запусками) → отказ по лицу с текстом сервера', () => {
    const text = 'Лицо 2 слишком мелкое';
    expect(launchFailure('invalid_request', text, 2)).toEqual({ kind: 'faceRefused', face: 2, message: text });
  });
  it('invalid_request без face (или face не число) → generic, тост как раньше', () => {
    expect(launchFailure('invalid_request', 'плохой запрос')).toEqual({ kind: 'generic', message: 'плохой запрос' });
    expect(launchFailure('invalid_request', 'плохой запрос', '2')).toEqual({ kind: 'generic', message: 'плохой запрос' });
  });
  it('номер лица — только положительное целое: NaN, 0, -1, 1.5, null, \'2\' → generic; 2 → faceRefused', () => {
    for (const bad of [NaN, 0, -1, 1.5, null, '2']) {
      expect(launchFailure('invalid_request', 'плохой запрос', bad)).toEqual({ kind: 'generic', message: 'плохой запрос' });
    }
    expect(launchFailure('invalid_request', 'Лицо 2', 2)).toEqual({ kind: 'faceRefused', face: 2, message: 'Лицо 2' });
  });
});

// ── Сброс флагов отказов (этап 2 · шаг 10d) ───────────────────────────────────
const fullUi = (over: Partial<FaceUi> = {}): FaceUi => ({
  tappedSmall: null, tooClose: false, faceOutOfFrame: false, busy: false,
  providerUnavailable: false, facesChanged: false, useLocalOnce: false, submitting: false, refusedFace: null, refusedText: null,
  ...over,
});

describe('clearFailures: сброс всех флагов отказов (в начале запуска)', () => {
  it('все флаги отказов → false; tappedSmall/useLocalOnce/submitting не трогаем', () => {
    const ui = fullUi({ tooClose: true, faceOutOfFrame: true, busy: true, providerUnavailable: true, facesChanged: true, tappedSmall: 3, useLocalOnce: true, submitting: true, refusedFace: 2, refusedText: 'Лицо 2 слишком мелкое' });
    const r = clearFailures(ui);
    expect(r.refusedFace).toBeNull();
    expect(r.refusedText).toBeNull();
    expect(r.tooClose).toBe(false);
    expect(r.faceOutOfFrame).toBe(false);
    expect(r.busy).toBe(false);
    expect(r.providerUnavailable).toBe(false);
    expect(r.facesChanged).toBe(false);
    expect(r.tappedSmall).toBe(3);
    expect(r.useLocalOnce).toBe(true);
    expect(r.submitting).toBe(true);
  });
  it('чистое состояние — остаётся чистым', () => {
    expect(clearFailures(fullUi())).toEqual(fullUi());
  });
});

describe('clearFaceChange: смена выбранного лица', () => {
  it('faceOutOfFrame и tooClose → false; busy/providerUnavailable/facesChanged не трогаем', () => {
    const ui = fullUi({ tooClose: true, faceOutOfFrame: true, busy: true, providerUnavailable: true, facesChanged: true, refusedFace: 2, refusedText: 'Лицо 2 слишком мелкое' });
    const r = clearFaceChange(ui);
    expect(r.refusedFace).toBeNull();
    expect(r.refusedText).toBeNull();
    expect(r.tooClose).toBe(false);
    expect(r.faceOutOfFrame).toBe(false);
    expect(r.busy).toBe(true);
    expect(r.providerUnavailable).toBe(true);
    expect(r.facesChanged).toBe(true);
  });
});

// ── Назначения «лицо → персонаж» (ADR-028, этап 3) ─────────────────────────────
const faces3 = [face(1), face(2), face(3)];

describe('tapFace: тап по лицу', () => {
  it('свободная рамка при <2 назначений → active, limit нет', () => {
    const a = tapFace(emptyAssignments, 1, faces3);
    expect(a.active).toBe(1);
    expect(a.limit).toBeNull();
  });

  it('повторный тап по активной (назначенной) → active = null', () => {
    const assigned = assignCharacter(emptyAssignments, 1, 'm');
    const a1 = tapFace(assigned, 1, faces3); // тап по назначенной → active
    expect(a1.active).toBe(1);
    const a2 = tapFace(a1, 1, faces3); // повторный тап по активной → null
    expect(a2.active).toBeNull();
  });

  it('2 назначения + тап по свободной → limit = n, active не меняется (мутация: не ставить limit → краснеет)', () => {
    let a = assignCharacter(emptyAssignments, 1, 'm');
    a = assignCharacter(a, 2, 'i');
    expect(a.active).toBeNull();
    const r = tapFace(a, 3, faces3);
    expect(r.limit).toBe(3);
    expect(r.active).toBeNull();
    // назначений не прибавилось
    expect(r.assignments.size).toBe(2);
  });

  it('тап по назначенной → active (переназначение), limit снимается', () => {
    let a = assignCharacter(emptyAssignments, 1, 'm');
    a = { ...a, limit: 3 };
    const r = tapFace(a, 1, faces3);
    expect(r.active).toBe(1);
    expect(r.limit).toBeNull();
  });

  it('мелкое лицо → состояние не меняется (как этап 2)', () => {
    const r = tapFace(emptyAssignments, 1, [face(1, true)]);
    expect(r).toBe(emptyAssignments);
  });
});

describe('isCharacterTaken: занятый персонаж', () => {
  it('персонаж на другом лице недоступен (мутация: не исключать занятого → краснеет)', () => {
    const a = assignCharacter(emptyAssignments, 1, 'm');
    expect(isCharacterTaken(a, 'm', 2)).toBe(true);
    // на том же лице — не «занят»
    expect(isCharacterTaken(a, 'm', 1)).toBe(false);
    // другой персонаж — свободен
    expect(isCharacterTaken(a, 'i', 2)).toBe(false);
  });

  it('снятие назначения освобождает персонажа', () => {
    let a = assignCharacter(emptyAssignments, 1, 'm');
    a = removeAssignment(a, 1);
    expect(isCharacterTaken(a, 'm', 2)).toBe(false);
  });
});

describe('assignCharacter: переназначение заменяет, не добавляет', () => {
  it('повторное назначение тому же лицу → одно назначение (мутация: добавлять → краснеет)', () => {
    let a = assignCharacter(emptyAssignments, 1, 'm');
    a = assignCharacter(a, 1, 'i'); // заменяет, не добавляет
    expect(a.assignments.size).toBe(1);
    expect(a.assignments.get(1)).toBe('i');
    expect(a.active).toBeNull();
  });

  it('назначение на второе лицо → два (лимит MAX_FACE_PASSES)', () => {
    let a = assignCharacter(emptyAssignments, 1, 'm');
    a = assignCharacter(a, 2, 'i');
    expect(a.assignments.size).toBe(2);
    expect(MAX_FACE_PASSES).toBe(2);
  });
});

describe('faceTargetFromAssignments: сборка цели', () => {
  it('faces в порядке возрастания номера лица (мутация: не сортировать → краснеет)', () => {
    let a = assignCharacter(emptyAssignments, 2, 'i');
    a = assignCharacter(a, 1, 'm');
    const t = faceTargetFromAssignments(a, 'v1', 'fk');
    expect(t).toEqual({ versionId: 'v1', facesKey: 'fk', faces: [{ face: 1, characterSlug: 'm' }, { face: 2, characterSlug: 'i' }] });
  });

  it('одно лицо → faces из одного элемента', () => {
    const a = assignCharacter(emptyAssignments, 1, 'm');
    const t = faceTargetFromAssignments(a, 'v1', 'fk');
    expect(t).toEqual({ versionId: 'v1', facesKey: 'fk', faces: [{ face: 1, characterSlug: 'm' }] });
  });

  it('без назначений → null (запуска нет)', () => {
    expect(faceTargetFromAssignments(emptyAssignments, 'v1', 'fk')).toBeNull();
  });
});

describe('hasAssignments', () => {
  it('нет назначений → false; есть → true', () => {
    expect(hasAssignments(emptyAssignments)).toBe(false);
    expect(hasAssignments(assignCharacter(emptyAssignments, 1, 'm'))).toBe(true);
  });
});

describe('parseFacesChangedBody: тело 409 при запуске', () => {
  it('facesKey + faces → FacesResponse', () => {
    const body = { facesKey: 'fk2', width: 100, height: 100, faces: [{ n: 1, bbox: [0, 0, 10, 10], score: 0.9, small: false }] };
    expect(parseFacesChangedBody(body)).toEqual(body);
  });
  it('без facesKey / без массива faces → null', () => {
    expect(parseFacesChangedBody({ facesKey: 'fk' })).toBeNull();
    expect(parseFacesChangedBody({ faces: [] })).toBeNull();
    expect(parseFacesChangedBody(null)).toBeNull();
  });
});

describe('searchFailureText: причина отказа поиска', () => {
  it('busy → очередь видеокарты', () => {
    expect(searchFailureText('busy')).toContain('Видеокарта занята');
  });
  it('provider_unavailable → локальные недоступны', () => {
    expect(searchFailureText('provider_unavailable')).toContain('Локальные модели');
  });
  it('прочий код → общий текст', () => {
    expect(searchFailureText('other')).toBe('Не удалось найти лица. Попробуйте позже');
  });
});

// ── Хвосты перепроверки Майи (n3, n4) ────────────────────────────────────────
describe('faceSwapView: полоса режима при отказе (n3)', () => {
  const two = () => assignCharacter(assignCharacter(emptyAssignments, 1, 'marina'), 2, 'olga');

  it('без отказа два назначения — «можно подставлять»', () => {
    const a = two();
    expect(a.active).toBeNull();
    expect(faceSwapView(base({ assignments: a })).bar).toBe(FS_TEXT.barTwo);
  });

  it('отказ «близко» по лицу 2 — полоса повторяет отказ, а не зовёт подставлять (мутация: вернуть barTwo → краснеет)', () => {
    const v = faceSwapView(base({ assignments: two(), facesTooClose: true, refusedFace: 2 }));
    expect(v.canSubmit).toBe(false);
    expect(v.bar).toBe(FS_TEXT.closeN(2));
  });

  it('отказ «вне кадра» без номера при двух назначениях — общий текст отказа', () => {
    expect(faceSwapView(base({ assignments: two(), faceOutOfFrame: true })).bar).toBe(FS_TEXT.outOfFrame);
  });

  it('отказ сервера по лицу (faceRefused) — его текст', () => {
    const text = 'Лицо 2 слишком мелкое';
    expect(faceSwapView(base({ assignments: two(), refusedFace: 2, refusedText: text })).bar).toBe(text);
  });

  it('одно назначение с отказом — не «подставить сразу»', () => {
    const a = assignCharacter(emptyAssignments, 1, 'marina');
    expect(faceSwapView(base({ assignments: a, facesTooClose: true })).bar).toBe(FS_TEXT.closeN(1));
  });
});

describe('тексты режима: номер лица не отрывается от слова (n4)', () => {
  it('между «лица/лице» и номером — неразрывный пробел (мутация: обычный пробел → краснеет)', () => {
    for (const s of [FS_TEXT.pickFor(1), FS_TEXT.barPick(1), FS_TEXT.barReassign(1),
      FS_TEXT.takenOn(1), FS_TEXT.busyTile(1), FS_TEXT.unassignN(1)]) {
      expect(s).toMatch(/лиц[ае]\u00A01/);
      expect(s).not.toMatch(/лиц[ае] 1/);
    }
  });
});

// ── Подпись модели в строке запуска (v2): локальные — «локальные модели», облачные — label ──
describe('modelLabel: подпись модели', () => {
  const catalog = { providers: [
    { key: 'local', models: [{ id: 'flux3-klein', label: 'FLUX 3 Klein — по умолчанию' }, { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1 — качественно' }] },
    { key: 'fal', models: [{ id: 'fal-ai/flux-pro/kontext', label: 'FLUX Kontext' }] },
  ] };
  it('локальная модель — «локальные модели», не id (мутация: вернуть id → краснеет)', () => {
    expect(modelLabel(catalog, 'qwen-image-2.1')).toBe('локальные модели');
    expect(modelLabel(catalog, 'flux3-klein')).toBe('локальные модели');
  });
  it('облачная модель — label из каталога', () => {
    expect(modelLabel(catalog, 'fal-ai/flux-pro/kontext')).toBe('FLUX Kontext');
  });
  it('каталог не пришёл — id как есть', () => {
    expect(modelLabel(null, 'qwen-image-2.1')).toBe('qwen-image-2.1');
  });
  it('модель не найдена в каталоге — id как есть', () => {
    expect(modelLabel(catalog, 'unknown-model')).toBe('unknown-model');
  });
  it('id null — null', () => {
    expect(modelLabel(catalog, null)).toBeNull();
  });
});
