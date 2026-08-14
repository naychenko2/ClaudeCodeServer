// Тесты парсера/стрижки <project-preset key="…"/> и сборщика карточки предложения
// пресета в ленте. Зеркало TeamMechanicOffer.test.ts: те же гарантии (парс вне код-
// блоков, незакрытый префикс в хвосте стрима, последнее предложение по ключу
// «переезжает» к актуальной реплике, текст сабагента не порождает карточку).
import { describe, it, expect } from 'vitest';
import {
  parseProjectPresetOffer, stripProjectPresetMarkers, buildProjectPresetOffer,
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
