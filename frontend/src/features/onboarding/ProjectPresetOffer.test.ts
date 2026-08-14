// Тесты парсера/стрижки <project-preset key="…"/> и сборщика карточки предложения
// пресета в ленте. Зеркало TeamMechanicOffer.test.ts: те же гарантии (парс вне код-
// блоков, незакрытый префикс в хвосте стрима, последнее предложение по ключу
// «переезжает» к актуальной реплике, текст сабагента не порождает карточку).
import { describe, it, expect } from 'vitest';
import {
  parseProjectPresetOffer, stripProjectPresetMarkers, buildProjectPresetOffer,
  resolvePresetCardState, cardBodyForKey,
  type PresetOfferItem,
} from './ProjectPresetOffer';

const DOCS = '<project-preset key="docs"/>';
const DEV = '<project-preset key="dev"/>';

const assistant = (text = ''): PresetOfferItem => ({ kind: 'text', text });
const offer = (text: string): PresetOfferItem => ({ kind: 'text', text });

describe('parseProjectPresetOffer — поиск маркера', () => {
  it('находит валидный маркер в обычном тексте', () => {
    expect(parseProjectPresetOffer(`Что-то написать… ${DOCS} И можно применить.`)).toEqual({ key: 'docs' });
  });

  it('возвращает null, если маркера нет', () => {
    expect(parseProjectPresetOffer('Без маркера и без ключа.')).toBeNull();
  });

  it('возвращает null при неполном маркере (нет key)', () => {
    expect(parseProjectPresetOffer('<project-preset/>')).toBeNull();
  });

  it('маркер внутри fenced-кода НЕ парсится', () => {
    const text = 'Смотри:\n```\n<project-preset key="docs"/>\n```\nВот так.';
    expect(parseProjectPresetOffer(text)).toBeNull();
  });

  it('маркер внутри inline-кода НЕ парсится', () => {
    const text = 'Пример: `<project-preset key="docs"/>` — не триггер карточки.';
    expect(parseProjectPresetOffer(text)).toBeNull();
  });

  it('если есть и вне кода, и внутри кода — берётся вне кода', () => {
    const text = `${DOCS}\n\`${DEV}\``; // "<project-preset key=\"docs\"/>\n`<project-preset key=\"dev\"/>`"
    expect(parseProjectPresetOffer(text)).toEqual({ key: 'docs' });
  });

  it('несколько маркеров вне кода — возвращается первый', () => {
    const text = `${DOCS} потом ${DEV}`;
    expect(parseProjectPresetOffer(text)).toEqual({ key: 'docs' });
  });
});

describe('stripProjectPresetMarkers — отображение текста', () => {
  it('без маркера — текст не меняется', () => {
    const text = 'Просто текст без маркера.';
    expect(stripProjectPresetMarkers(text)).toBe(text);
  });

  it('маркер вне кода вырезается', () => {
    expect(stripProjectPresetMarkers(`Предлагаю ${DOCS} каркас.`)).toBe('Предлагаю  каркас.');
  });

  it('маркер внутри fenced-кода НЕ трогается', () => {
    const text = '```\n<project-preset key="docs"/>\n```';
    expect(stripProjectPresetMarkers(text)).toBe(text);
  });

  it('маркер внутри inline-кода НЕ трогается', () => {
    const text = '`<project-preset key="docs"/>`';
    expect(stripProjectPresetMarkers(text)).toBe(text);
  });

  it('стрим: незакрытый префикс <project-preset… в хвосте прячется', () => {
    const tail = '<project-preset key="d';
    expect(stripProjectPresetMarkers(`Текст идёт… ${tail}`, true)).toBe('Текст идёт… ');
  });

  it('стрим: просто открытая угловая скобка без префикса тега тоже проходит', () => {
    // "<" остаётся — это не наш префикс
    expect(stripProjectPresetMarkers('Сравнение: 1 < 2', true)).toBe('Сравнение: 1 < 2');
  });

  it('стрим: полный маркер в стриме тоже вырезается', () => {
    expect(stripProjectPresetMarkers(`Готово ${DOCS} дальше`, true)).toBe('Готово  дальше');
  });
});

describe('buildProjectPresetOffer — карточка несёт последнее предложение', () => {
  it('одно предложение в чате — карточка одна', () => {
    const items = [assistant(), offer(DOCS)];
    const map = buildProjectPresetOffer(items);
    expect(map.size).toBe(1);
    expect(map.get(1)).toEqual({ key: 'docs' });
  });

  it('повторное предложение того же пресета — карточка одна, у последнего', () => {
    const items = [assistant(), offer(DOCS), assistant(), offer(DOCS)];
    const map = buildProjectPresetOffer(items);
    expect(map.size).toBe(1);
    expect(map.has(1)).toBe(false);
    expect(map.has(3)).toBe(true);
  });

  it('разные пресеты в одном чате — у каждого своя карточка', () => {
    const items = [assistant(), offer(DOCS), offer(DEV)];
    const map = buildProjectPresetOffer(items);
    expect(map.size).toBe(2);
    expect(map.get(1)?.key).toBe('docs');
    expect(map.get(2)?.key).toBe('dev');
  });

  it('текст сабагента (parentToolUseId) не порождает карточку', () => {
    const items: PresetOfferItem[] = [
      assistant(),
      { kind: 'text', text: DOCS, parentToolUseId: 'sub-1' },
    ];
    expect(buildProjectPresetOffer(items).size).toBe(0);
  });

  it('пустой/без маркера текст — карточек нет', () => {
    const items = [assistant(), assistant('обычный ответ без маркера')];
    expect(buildProjectPresetOffer(items).size).toBe(0);
  });

  it('элемент без text (например, session_started) — не падаем, карточек нет', () => {
    const items: PresetOfferItem[] = [{ kind: 'session_started' }, { kind: 'text', text: DOCS }];
    const map = buildProjectPresetOffer(items);
    expect(map.size).toBe(1);
    expect(map.get(1)?.key).toBe('docs');
  });
});

describe('resolvePresetCardState — режим карточки от серверного presetKey', () => {
  // Серверный presetKey === null — проект создан до фичи, к каркасу возвращаться
  // не нужно. Активной кнопки быть не должно ни при каком содержимом ленты:
  // иначе клик уйдёт в 409 на бэке (см. исходный дефект п.4-доработки).
  it('presetKey === null при наличии маркера в ленте — карточка скрыта', () => {
    expect(resolvePresetCardState(null, true)).toEqual({ mode: 'hidden' });
  });

  it('presetKey === null без маркеров в ленте — карточка скрыта', () => {
    expect(resolvePresetCardState(null, false)).toEqual({ mode: 'hidden' });
  });

  it('presetKey === undefined (DTO ещё не приехал) — карточка скрыта', () => {
    expect(resolvePresetCardState(undefined, true)).toEqual({ mode: 'hidden' });
    expect(resolvePresetCardState(undefined, false)).toEqual({ mode: 'hidden' });
  });

  it('presetKey === "pending" с маркером — активная кнопка', () => {
    expect(resolvePresetCardState('pending', true)).toEqual({ mode: 'pending' });
  });

  it('presetKey === "pending" без маркера — карточка скрыта (предложения ещё не было)', () => {
    expect(resolvePresetCardState('pending', false)).toEqual({ mode: 'hidden' });
  });

  it('presetKey === "none" — отказ зафиксирован', () => {
    expect(resolvePresetCardState('none', true)).toEqual({ mode: 'declined' });
    expect(resolvePresetCardState('none', false)).toEqual({ mode: 'declined' });
  });

  it('presetKey === "docs" — каркас применён, карточка-итог', () => {
    expect(resolvePresetCardState('docs', true)).toEqual({ mode: 'applied', key: 'docs' });
  });

  it('неизвестный ключ — карточка-итог с этим ключом в подписи', () => {
    expect(resolvePresetCardState('future-key', true)).toEqual({ mode: 'applied', key: 'future-key' });
  });
});

describe('cardBodyForKey — тело карточки от ключа пресета', () => {
  // Карточка в pending должна описывать ровно тот каркас, который бэкенд создаст после
  // клика. Без ключа или с неизвестным ключом — нейтральная формулировка, а не
  // документный текст: это и есть исходный баг п.6б (документный текст для dev).

  const DOCS_MARKER = 'папки под работу с документами';
  const DEV_MARKER = 'папки под разработку';
  const PERSONAL_MARKER = 'три папки';
  const NEUTRAL = 'Создам папки и файлы под тип этого проекта.';

  it('docs — документный текст (как был до фикса)', () => {
    const body = cardBodyForKey('docs');
    expect(body).toContain(DOCS_MARKER);
    expect(body).toContain('Исходники');
    expect(body).toContain('CLAUDE.md');
  });

  it('dev — текст под разработку, без CLAUDE.md в составе', () => {
    const body = cardBodyForKey('dev');
    expect(body).toContain(DEV_MARKER);
    expect(body).toContain('`docs`');
    expect(body).toContain('`docs/adr`');
    expect(body).toContain('`notes`');
    // «CLAUDE.md трогать не буду» — гарантия согласия: для dev-пресета CLAUDE.md не создаётся.
    expect(body).toContain('CLAUDE.md');
  });

  it('personal — текст под три личные папки', () => {
    const body = cardBodyForKey('personal');
    expect(body).toContain(PERSONAL_MARKER);
    expect(body).toContain('Материалы');
    expect(body).toContain('Заметки');
    expect(body).toContain('Архив');
  });

  it('docs / dev / personal дают три РАЗНЫХ тела', () => {
    // Защита от регрессии: если кто-то склеит их обратно в одну константу, тест ловит.
    const docs = cardBodyForKey('docs');
    const dev = cardBodyForKey('dev');
    const personal = cardBodyForKey('personal');
    expect(docs).not.toBe(dev);
    expect(docs).not.toBe(personal);
    expect(dev).not.toBe(personal);
  });

  it('неизвестный ключ — нейтральное, а не документное', () => {
    // Исходный баг: молча падали на docs. Теперь — нейтральная формулировка.
    expect(cardBodyForKey('future-key')).toBe(NEUTRAL);
    expect(cardBodyForKey('future-key')).not.toContain(DOCS_MARKER);
  });

  it('null/undefined/пустая строка — нейтральное', () => {
    expect(cardBodyForKey(null)).toBe(NEUTRAL);
    expect(cardBodyForKey(undefined)).toBe(NEUTRAL);
    expect(cardBodyForKey('')).toBe(NEUTRAL);
  });
});
