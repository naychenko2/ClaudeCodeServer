// Панель «Подставить персонажа» с назначениями (ADR-028, этап 3 · шаг 16а): список назначений,
// занятый персонаж, подвал (подпись кнопки, чип), плашки лимита и отказа поиска.
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { FaceSwapPanel } from './FaceSwapPanel';
import {
  assignCharacter, assignmentRefusals, emptyAssignments, faceQuoteChip, faceSwapView, FS_TEXT, hasAssignments, passesWord, pruneAssignments,
  type FaceAssignments, type FaceSwapViewInput,
} from './faceSwapView';
import { faceSwapReducer, initialFaceSwap } from './thread/faceSwap';
import type { DetectedFace, ImageEditCharacter, ImageEditorApi } from './api';

const face = (n: number): DetectedFace => ({ n, bbox: [10, 10, 30, 30], score: 0.9, small: false });
const chars = [
  { slug: 'maria', name: 'Мария', photos: [] },
  { slug: 'ivan', name: 'Иван', photos: [] },
] as unknown as ImageEditCharacter[];
const api = { characterPhotoUrl: () => '' } as unknown as ImageEditorApi;

const asg = (pairs: [number, string][], active: number | null = null): FaceAssignments => {
  let a = emptyAssignments;
  for (const [f, s] of pairs) a = assignCharacter(a, f, s);
  return { ...a, active };
};

const view = (over: Partial<FaceSwapViewInput> = {}) => faceSwapView({
  phase: 'ready', faces: [face(1), face(2), face(3)], selectedFace: null, selectedCharacter: null,
  error: null, provider: 'local', localAvailable: true, useLocalOnce: false, tappedSmall: null,
  over8: false, facesTooClose: false, faceOutOfFrame: false, busy: false, providerUnavailable: false,
  facesChanged: false, hasMask: false, ...over,
});

const render = (a: FaceAssignments, over: Partial<FaceSwapViewInput> = {}, quote: string | null = null,
  refusals: ReadonlyMap<number, string> = new Map()) => {
  const v = view({ assignments: a, ...over });
  const n = a.assignments.size;
  return renderToStaticMarkup(createElement(FaceSwapPanel, {
    api, projectId: 'p', characters: chars, assignments: a, refusals, facePasses: n > 0 ? n : null,
    canSubmit: v.canSubmit, quote, localOnce: false, submitting: false, notices: v.notices,
    onPick: () => {}, onRemove: () => {}, onTapFace: () => {}, onNoticeAction: () => {}, onSubmit: () => {},
  }));
};

describe('FaceSwapPanel: список назначений и выбор персонажа', () => {
  it('строки «Лицо n», счётчик «k из 2», ✕ с aria-label', () => {
    const html = render(asg([[1, 'maria'], [2, 'ivan']]));
    expect(html).toContain('Лицо 1');
    expect(html).toContain('Лицо 2');
    expect(html).toContain(FS_TEXT.asgMeta(2));
    expect(html).toContain(FS_TEXT.removeAria(1));
    expect(html).toContain(FS_TEXT.removeAria(2));
  });

  it('выбора персонажа нет, пока лицо не активно', () => {
    expect(render(asg([[1, 'maria']]))).not.toContain(FS_TEXT.pickFor(2));
  });

  it('занятый на лице 1 персонаж при активном лице 2: aria-disabled и «Уже на лице 1»', () => {
    const html = render(asg([[1, 'maria']], 2));
    expect(html).toContain(FS_TEXT.pickFor(2));
    expect(html).toContain('aria-disabled="true"');
    expect(html).toContain(FS_TEXT.takenOn(1));
  });

  it('при переназначении лица 1 его собственный персонаж доступен', () => {
    const html = render(asg([[1, 'maria']], 1));
    expect(html).not.toContain('aria-disabled="true"');
  });

  it('снятие назначения освобождает персонажа', () => {
    const html = render(asg([], 2));
    expect(html).not.toContain('aria-disabled="true"');
  });

  it('empty state и слот после первого назначения', () => {
    expect(render(emptyAssignments)).toContain(FS_TEXT.asgEmpty);
    expect(render(asg([[1, 'maria']]))).toContain(FS_TEXT.asgSlot);
  });
});

describe('FaceSwapPanel: подвал', () => {
  it('одно назначение — «Подставить», два — «Подставить (2)»', () => {
    const one = render(asg([[1, 'maria']]), {}, faceQuoteChip({ facePasses: 1, expectedSeconds: 50 }));
    const two = render(asg([[1, 'maria'], [2, 'ivan']]), {}, faceQuoteChip({ facePasses: 2, expectedSeconds: 100 }));
    expect(one).toContain('>Подставить<');
    expect(one).not.toContain(FS_TEXT.go2);
    expect(two).toContain(FS_TEXT.go2);
  });

  it('чип котировки приходит из подвала', () => {
    expect(render(asg([[1, 'maria'], [2, 'ivan']]), {}, '2 прохода · бесплатно · ~2 мин')).toContain('2 прохода · бесплатно · ~2 мин');
  });

  it('без назначений кнопка неактивна', () => {
    expect(render(emptyAssignments)).toMatch(/<button[^>]*disabled[^>]*>.*Подставить/s);
  });
});

describe('чип котировки (шаг 16б): число проходов и время — из котировки', () => {
  it('N и M берутся из котировки: разные ETA — разные подписи', () => {
    expect(faceQuoteChip({ facePasses: 2, expectedSeconds: 100 })).toBe('2 прохода · бесплатно · ~2 мин');
    expect(faceQuoteChip({ facePasses: 2, expectedSeconds: 250 })).toBe('2 прохода · бесплатно · ~5 мин');
    expect(faceQuoteChip({ facePasses: 1, expectedSeconds: 61 })).toBe('1 проход · бесплатно · ~2 мин');
  });

  it('ETA округляется вверх, не меньше минуты; без facePasses — один проход', () => {
    expect(faceQuoteChip({ expectedSeconds: 5 })).toBe('1 проход · бесплатно · ~1 мин');
    expect(faceQuoteChip({ facePasses: 2, expectedSeconds: 0 })).toBe('2 прохода · бесплатно · ~1 мин');
  });

  it('без ETA — без времени; котировки нет — чипа нет', () => {
    expect(faceQuoteChip({ facePasses: 2, expectedSeconds: null })).toBe('2 прохода · бесплатно');
    expect(faceQuoteChip(null)).toBeNull();
  });

  it('склонение «проход»', () => {
    expect([1, 2, 4, 5, 11, 21, 22].map(passesWord)).toEqual(['проход', 'прохода', 'прохода', 'проходов', 'проходов', 'проход', 'прохода']);
  });
});

describe('faceSwapView (этап 3): плашки', () => {
  it('лимит (limit != null) → warning с текстом лимита, снимается сбросом limit', () => {
    const a = { ...asg([[1, 'maria'], [2, 'ivan']]), limit: 3 };
    expect(view({ assignments: a }).notices.some(n => n.tone === 'warning' && n.text === FS_TEXT.limit)).toBe(true);
    expect(view({ assignments: assignCharacter(a, 1, 'ivan') }).notices.some(n => n.text === FS_TEXT.limit)).toBe(false);
  });

  it('отказ поиска (busy) виден плашкой в фазе error', () => {
    const v = view({ phase: 'error', error: 'busy', searchFailure: { code: 'busy', text: 'Видеокарта занята другой задачей…' } });
    expect(v.notices).toHaveLength(1);
    expect(v.notices[0].text).toBe('Видеокарта занята другой задачей…');
  });

  it('отказ поиска попадает в рендер панели', () => {
    const v = view({ phase: 'error', searchFailure: { code: 'other', text: 'Не удалось найти лица. Попробуйте позже' } });
    const html = renderToStaticMarkup(createElement(FaceSwapPanel, {
      api, projectId: 'p', characters: chars, assignments: emptyAssignments, refusals: new Map(), facePasses: null, canSubmit: false,
      quote: null, localOnce: false, submitting: false, notices: v.notices,
      onPick: () => {}, onRemove: () => {}, onTapFace: () => {}, onNoticeAction: () => {}, onSubmit: () => {},
    }));
    expect(html).toContain('Не удалось найти лица');
  });

  it('«близко» при одном назначении называет лицо и предлагает снять его', () => {
    const v = view({ assignments: asg([[2, 'ivan']]), facesTooClose: true });
    const n = v.notices.find(x => x.unassign);
    expect(n?.text).toBe(FS_TEXT.closeN(2));
    expect(n?.unassign).toEqual([2]);
    expect(v.canSubmit).toBe(false);
  });

  it('«вне кадра» при двух назначениях — кнопка снятия на каждое лицо', () => {
    const v = view({ assignments: asg([[1, 'maria'], [3, 'ivan']]), faceOutOfFrame: true });
    expect(v.notices.find(x => x.unassign)?.unassign).toEqual([1, 3]);
  });

  it('409 faces_changed при назначениях — текст про повторное назначение', () => {
    expect(view({ assignments: emptyAssignments, facesChanged: true }).notices.some(n => n.text === FS_TEXT.changed2)).toBe(true);
  });

  it('canSubmit: нет назначений → false, одно → true', () => {
    expect(view({ assignments: emptyAssignments }).canSubmit).toBe(false);
    expect(view({ assignments: asg([[1, 'maria']]) }).canSubmit).toBe(true);
  });
});

describe('отсечка назначений при смене списка лиц', () => {
  it('pruneAssignments отбрасывает назначения, active и limit на отсутствующие лица', () => {
    const a = { ...asg([[1, 'maria'], [3, 'ivan']]), active: 3, limit: 2 };
    const r = pruneAssignments(a, [face(1), face(2)]);
    expect([...r.assignments.keys()]).toEqual([1]);
    expect(r.active).toBeNull();
    expect(r.limit).toBe(2);
  });

  it('ничего не отброшено — тот же объект', () => {
    const a = asg([[1, 'maria']]);
    expect(pruneAssignments(a, [face(1), face(2)])).toBe(a);
  });

  it('редьюсер: повторный searchOk убирает назначение на пропавшее лицо', () => {
    let s = faceSwapReducer(initialFaceSwap, { type: 'searchStart' });
    s = faceSwapReducer(s, { type: 'searchOk', response: { facesKey: 'k', width: 100, height: 100, faces: [face(1), face(2), face(3)] }, requestId: 1 });
    s = faceSwapReducer(s, { type: 'assign', face: 1, slug: 'maria' });
    s = faceSwapReducer(s, { type: 'assign', face: 3, slug: 'ivan' });
    s = faceSwapReducer(s, { type: 'searchStart' });
    s = faceSwapReducer(s, { type: 'searchOk', response: { facesKey: 'k2', width: 100, height: 100, faces: [face(1), face(2)] }, requestId: 2 });
    expect([...s.assignments.assignments.keys()]).toEqual([1]);
  });
});

describe('кисть и назначения', () => {
  it('clearAssignments сбрасывает все назначения; hasAssignments управляет подтверждением', () => {
    let s = faceSwapReducer(initialFaceSwap, { type: 'assign', face: 1, slug: 'maria' });
    expect(hasAssignments(s.assignments)).toBe(true);
    s = faceSwapReducer(s, { type: 'clearAssignments' });
    expect(hasAssignments(s.assignments)).toBe(false);
  });

  it('отказ по назначенному лицу: строка красная, причина под именем, бейдж danger (m1)', () => {
    const a = asg([[1, 'maria'], [2, 'ivan']]);
    const faces = [face(1), face(2)];
    const refusals = assignmentRefusals({ tooClose: true, faceOutOfFrame: false, refusedFace: 2 }, faces, a.assignments);
    expect([...refusals]).toEqual([[2, FS_TEXT.closeWhy]]);
    const html = render(a, { facesTooClose: true, refusedFace: 2 }, null, refusals);
    // Красная только строка лица 2, причина под именем
    expect(html.match(/data-face-refused/g)).toHaveLength(1);
    const row2 = html.slice(html.indexOf('data-face-refused'));
    // Фон и рамка — у самой строки (её открывающий тег), а не только у бейджа
    const rowTag = row2.slice(0, row2.indexOf('>'));
    expect(rowTag).toContain('background:var(--c-danger-bg)');
    expect(rowTag).toContain('var(--c-danger-border)');
    expect(row2).toContain('Иван');
    expect(row2).toContain(FS_TEXT.closeWhy);
    // Текст отказа сервера — причина как есть; неназначенное лицо в строки не попадает
    const srv = assignmentRefusals({ tooClose: false, faceOutOfFrame: false, refusedFace: 3, refusedText: 'Лицо 3 слишком мелкое' }, [...faces, face(3)], a.assignments);
    expect(srv.size).toBe(0);
    // Назначенное лицо: в строку отказа попадает ровно текст сервера, а не текст по умолчанию (m-T1)
    const srvAssigned = assignmentRefusals({ tooClose: true, faceOutOfFrame: false, refusedFace: 2, refusedText: 'Лицо 2 слишком мелкое' }, faces, a.assignments);
    expect(srvAssigned.get(2)).toBe('Лицо 2 слишком мелкое');
    expect(srvAssigned.get(1)).toBeUndefined();
    const out = assignmentRefusals({ tooClose: false, faceOutOfFrame: true, refusedFace: 1 }, faces, a.assignments);
    expect(out.get(1)).toBe(FS_TEXT.outWhy);
  });

  it('без отказа строки не красные', () => {
    expect(render(asg([[1, 'maria']]))).not.toContain('data-face-refused');
  });

  it('«Уже на лице N» — читаемая подпись, приглушены только аватар и имя (m6)', () => {
    const html = render(asg([[1, 'maria']], 2));
    // Тег подписи «Уже на лице 1» (не title кнопки): читаемая, без приглушения
    const i = html.indexOf('>' + FS_TEXT.takenOn(1) + '<');
    const capTag = html.slice(html.lastIndexOf('<span', i), i);
    expect(capTag).toContain('var(--c-text-secondary)');
    expect(capTag).not.toContain('opacity');
    // Приглушены аватар (обёртка перед ним) и имя занятого персонажа
    const nameEnd = html.indexOf('>Мария<');
    const nameTag = html.slice(html.lastIndexOf('<span', nameEnd), nameEnd);
    expect(nameTag).toContain('opacity:0.5');
    const btn = html.slice(html.lastIndexOf('<button', nameEnd), nameEnd);
    expect(btn.match(/opacity:0\.5/g)).toHaveLength(2);
  });

  it('текст подтверждения — по макету', () => {
    expect(FS_TEXT.brushConfirm).toBe('Кисть работает с одним лицом — назначения сбросятся');
  });
});
