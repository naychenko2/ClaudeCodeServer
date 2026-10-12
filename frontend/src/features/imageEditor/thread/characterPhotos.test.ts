// Блок «Фото персонажа» (ADR-027, шаг 18): чистые функции — пометка по pick/scene,
// видимость блока и «Заменить», начальный выбор, порядок нажатий, тело «Перерисовать».
// Мутации: каждое «краснеет» при сбое проверяемого поведения (см. комментарии в тестах).

import { describe, expect, it } from 'vitest';
import type { ImageEditCharacter } from '../api';
import {
  CPS, counterText, emptySelectionText, initialSelection, newRunText, overLimitText,
  pickBadge, pickLimit, redrawBody, sceneLine, showReplace, stripVisible, togglePick,
} from './characterPhotos';
import type { ImageThreadCharacter } from './threadsApi';

const scene = { angle: 'profile', pitch: 'level', emotion: 'laugh' } as const;
const character: ImageThreadCharacter = { slug: 'andrey', photos: ['a.jpg', 'b.jpg'], pick: 'auto', scene: { ...scene } };

const card = (photos: string[], photosPerRequest?: number | null): ImageEditCharacter => ({
  slug: 'andrey', name: 'Андрей', path: 'characters/andrey',
  photos: photos.map(file => ({ file })),
  createdAt: '2026-10-01T00:00:00Z',
  ...(photosPerRequest != null ? { photosPerRequest } : null),
});

describe('sceneLine: «Под сюжет: …»', () => {
  it('ракурс, наклон «прямо» не выводится, эмоция: {profile, level, laugh} → «в профиль, смех»', () => {
    expect(sceneLine({ ...scene })).toBe('Под сюжет: в профиль, смех');
    // Мутация: вернуть «level» в словарь наклона — тест краснеет (наклон «прямо» снова выводится)
  });

  it('все три признака: {front, up, neutral} → «анфас, сверху, спокойное»', () => {
    expect(sceneLine({ angle: 'front', pitch: 'up', emotion: 'neutral' })).toBe('Под сюжет: анфас, сверху, спокойное');
  });

  it('three-quarter, down, surprised → «вполоборота, снизу, удивление»', () => {
    expect(sceneLine({ angle: 'three-quarter', pitch: 'down', emotion: 'surprised' })).toBe('Под сюжет: вполоборота, снизу, удивление');
  });

  it('признаков не осталось (только level) — строки нет', () => {
    expect(sceneLine({ angle: null, pitch: 'level', emotion: null })).toBeNull();
    expect(sceneLine(null)).toBeNull();
  });
});

describe('pickBadge: пометка по pick', () => {
  it('auto → info «Подобраны под сюжет», без title', () => {
    expect(pickBadge('auto')).toEqual({ tone: 'info', text: 'Подобраны под сюжет' });
    // Мутация: tone auto → 'neutral' — тест краснеет (акцент/инфо — разные роли)
  });

  it('manual → neutral «Выбраны вами»', () => {
    expect(pickBadge('manual')).toEqual({ tone: 'neutral', text: 'Выбраны вами' });
  });

  it('fallback → neutral «Обычный порядок» с title из заметки', () => {
    expect(pickBadge('fallback')).toEqual({
      tone: 'neutral', text: 'Обычный порядок', title: 'Подходящих под сюжет фото не нашлось — взяты как обычно',
    });
    // Мутация: убрать title — тест краснеет
  });
});

describe('stripVisible: видимость блока', () => {
  const base = { versionId: 'v1', cropNote: null as string | null, character };

  it('флаг выключен — блока нет, даже с персонажем', () => {
    expect(stripVisible({ ...base, smart: false })).toBe(false);
    // Мутация: smart не участвовать в решении — тест краснеет
  });

  it('флаг включён, персонаж есть — блок есть', () => {
    expect(stripVisible({ ...base, smart: true })).toBe(true);
  });

  it('исходник (ORIGIN) — блока нет', () => {
    expect(stripVisible({ ...base, smart: true, versionId: 'origin' })).toBe(false);
  });

  it('обрезка без ИИ (cropNote) — блока нет', () => {
    expect(stripVisible({ ...base, smart: true, cropNote: '800×600 · 4:3 · обрезано без ИИ' })).toBe(false);
  });

  it('персонажа нет — блока нет', () => {
    expect(stripVisible({ ...base, smart: true, character: null })).toBe(false);
  });
});

describe('showReplace: видимость «Заменить»', () => {
  it('запуск без промпта — нет', () => {
    expect(showReplace({ prompt: null }, card(['a.jpg', 'b.jpg']))).toBe(false);
    // Мутация: промпт не проверять — тест краснеет
  });

  it('персонаж удалён (карточки нет) — нет', () => {
    expect(showReplace({ prompt: 'в профиль' }, null)).toBe(false);
  });

  it('одно фото — нет (тогда подсказка empty-state)', () => {
    expect(showReplace({ prompt: 'в профиль' }, card(['a.jpg']))).toBe(false);
  });

  it('два фото и промпт — да', () => {
    expect(showReplace({ prompt: 'в профиль' }, card(['a.jpg', 'b.jpg']))).toBe(true);
    // Мутация: >= 2 → > 2 — тест краснеет
  });

  it('старый запуск без op — как раньше: два фото и промпт — да', () => {
    expect(showReplace({ prompt: 'в профиль', op: null }, card(['a.jpg', 'b.jpg']))).toBe(true);
  });

  it('запуск с маской (op = inpaint) — нет', () => {
    expect(showReplace({ prompt: 'в профиль', op: 'inpaint' }, card(['a.jpg', 'b.jpg']))).toBe(false);
    // Мутация: op не проверять — тест краснеет
  });

  it('другие операции (edit, generate) — да', () => {
    expect(showReplace({ prompt: 'в профиль', op: 'edit' }, card(['a.jpg', 'b.jpg']))).toBe(true);
    expect(showReplace({ prompt: 'в профиль', op: 'generate' }, card(['a.jpg', 'b.jpg']))).toBe(true);
  });
});

describe('pickLimit: лимит из DTO персонажа', () => {
  it('photosPerRequest из DTO, а не число в коде', () => {
    expect(pickLimit(card(['a.jpg', 'b.jpg'], 5))).toBe(5);
    // Мутация: игнорировать DTO и вернуть 3 — тест краснеет
  });

  it('DTO без поля (флаг выключен) — 3, как в бэкенде', () => {
    expect(pickLimit(card(['a.jpg', 'b.jpg']))).toBe(3);
    expect(pickLimit(null)).toBe(3);
  });
});

describe('initialSelection: начальный выбор окна', () => {
  it('фото запуска в их порядке; имён, которых нет в манифесте, отбрасывает', () => {
    expect(initialSelection(['a.jpg', 'x.jpg', 'b.jpg'], [{ file: 'a.jpg' }, { file: 'b.jpg' }, { file: 'c.jpg' }]))
      .toEqual(['a.jpg', 'b.jpg']);
    // Мутация: брать порядок манифеста, а не запуска — тест краснеет
  });

  it('ничего общего — пустой выбор', () => {
    expect(initialSelection(['x.jpg'], [{ file: 'a.jpg' }])).toEqual([]);
  });
});

describe('togglePick: порядок = порядок нажатий', () => {
  it('невыбранное добавляется в конец', () => {
    expect(togglePick(['a.jpg'], 'b.jpg', 3)).toEqual(['a.jpg', 'b.jpg']);
    // Мутация: вставлять в начало — тест краснеет
  });

  it('выбранное снимается, номера остальных сдвигаются', () => {
    expect(togglePick(['a.jpg', 'b.jpg', 'c.jpg'], 'a.jpg', 3)).toEqual(['b.jpg', 'c.jpg']);
  });

  it('лимит полон — нажатие по новому ничего не делает', () => {
    expect(togglePick(['a.jpg', 'b.jpg', 'c.jpg'], 'd.jpg', 3)).toEqual(['a.jpg', 'b.jpg', 'c.jpg']);
    // Мутация: вытеснять первое фото — тест краснеет
  });
});

describe('тексты окна', () => {
  it('счётчик, превышение, пустой выбор, строка запуска', () => {
    expect(counterText(2, 3)).toBe('Выбрано 2 из 3');
    expect(overLimitText(3)).toBe('Можно взять не больше 3 фото');
    expect(emptySelectionText).toBe('Выберите хотя бы одно фото');
    expect(newRunText('Nano Banana Pro')).toBe('Новый запуск: Nano Banana Pro · исходная версия останется');
    // Мутация: «из {лимит}» → «из 3» — тест краснеет
  });

  it('тексты заметки: заголовок, подсказка, ошибка, empty-state', () => {
    expect(CPS.pickerTitle).toBe('Какие фото взять');
    expect(CPS.pickerSubtitle).toBe('Первое фото важнее остальных. Локальная модель берёт только одно.');
    expect(CPS.invalid).toBe('Части фото больше нет у персонажа. Выберите заново.');
    expect(CPS.singleHint).toBe('Добавьте фото в разных ракурсах — AI Home будет подбирать подходящее под сюжет.');
    expect(CPS.photoGone).toBe('Этого фото больше нет у персонажа');
    expect(CPS.characterGone).toBe('Персонажа больше нет в проекте');
  });
});

describe('redrawBody: тело «Перерисовать» (POST …/jobs)', () => {
  const a = {
    prompt: 'Андрей смеётся у окна, в профиль',
    characterSlug: 'andrey',
    characterPhotos: ['b.jpg', 'a.jpg'],
    threadId: 't1',
    sessionId: 's1',
    versionId: 'v2',
    baseStepId: 'st1',
    contextRevision: 7,
  };

  it('промпт, персонаж, фото, нить, сессия, база и ревизия контекста', () => {
    expect(redrawBody(a)).toEqual({
      prompt: a.prompt,
      characterSlug: 'andrey',
      characterPhotos: ['b.jpg', 'a.jpg'],
      sessionId: 's1',
      threadId: 't1',
      versionId: 'v2',
      baseStepId: 'st1',
      contextRevision: 7,
    });
  });

  it('запуск без базы (генерация с нуля): versionId/baseStepId в теле нет', () => {
    const body = redrawBody({ ...a, versionId: null, baseStepId: null });
    expect(body).not.toHaveProperty('versionId');
    expect(body).not.toHaveProperty('baseStepId');
    // Мутация: класть null-поля в тело — тест краснеет
  });

  it('без ревизии контекста — поля contextRevision нет', () => {
    expect(redrawBody({ ...a, contextRevision: null })).not.toHaveProperty('contextRevision');
  });
});
