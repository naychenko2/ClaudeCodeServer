import { describe, expect, it } from 'vitest';
import {
  TEXT_LIMIT,
  type PersonaPromptSection,
  type PromptSectionsState,
  applySections,
  backToInherit,
  emptyState,
  modeOf,
  offBySpecialty,
  overLimit,
  sectionMode,
  sectionModes,
  setOff,
  setOwnText,
  startOwn,
  textCounter,
  toOverrides,
} from './promptSectionsModel';

// Данные — те же формы, что приносит GET /api/personas/{id}/prompt-sections:
// ВСЕ секции каталога специальности (включая выключенные), источники значений
// code|global|user|owner|persona (бэковый EffectivePromptSection, IPromptSectionProvider.cs:21).

type SectionOpts = {
  enabled?: boolean;
  text?: string;
  enabledSource?: 'code' | 'global' | 'user' | 'owner' | 'persona';
  textSource?: 'code' | 'global' | 'user' | 'owner' | 'persona';
};

const section = (id: string, o: SectionOpts = {}): PersonaPromptSection => ({
  id,
  enabled: o.enabled ?? true,
  text: o.text ?? `базовый текст ${id}`,
  enabledSource: o.enabledSource ?? 'code',
  textSource: o.textSource ?? 'code',
});

const stateWith = (effective: PersonaPromptSection[], overrides: PromptSectionsState['overrides'] = {}): PromptSectionsState => ({
  personaId: 'p-1',
  effective,
  overrides,
});

describe('sectionMode / sectionModes — режим по эффективному состоянию', () => {
  it('унаследованная включённая секция → inherit', () => {
    expect(sectionMode(section('roleRules'))).toBe('inherit');
    expect(sectionMode(section('roleRules', { textSource: 'global', enabledSource: 'global' }))).toBe('inherit');
  });

  // Ключевое различение (§2 «Форма персоны»): выключенная специальностью — НЕ off персоны.
  it('выключено специальностью → inherit, не «off»', () => {
    const offBySpec = section('dossier', { enabled: false, enabledSource: 'code' });
    expect(sectionMode(offBySpec)).toBe('inherit');
    expect(offBySpecialty(offBySpec)).toBe(true);
    // и слой настроек — тоже «не воля персоны»
    const offByLayer = section('dossier', { enabled: false, enabledSource: 'owner' });
    expect(sectionMode(offByLayer)).toBe('inherit');
    expect(offBySpecialty(offByLayer)).toBe(true);
  });

  it('выключено слоем персоны → off', () => {
    const off = section('dossier', { enabled: false, enabledSource: 'persona' });
    expect(sectionMode(off)).toBe('off');
    expect(offBySpecialty(off)).toBe(false);
  });

  it('свой текст → own (сильнее выключенности)', () => {
    expect(sectionMode(section('roleRules', { textSource: 'persona' }))).toBe('own');
    expect(sectionMode(section('roleRules', { textSource: 'persona', enabled: false, enabledSource: 'persona' }))).toBe('own');
  });

  it('sectionModes — карта режимов по списку', () => {
    const modes = sectionModes([
      section('a'),
      section('b', { enabled: false, enabledSource: 'code' }),
      section('c', { enabled: false, enabledSource: 'persona' }),
      section('d', { textSource: 'persona' }),
    ]);
    expect(modes).toEqual({ a: 'inherit', b: 'inherit', c: 'off', d: 'own' });
  });
});

describe('startOwn — черновик из текущего эффективного текста', () => {
  it('копирует эффективный текст (из любого источника)', () => {
    const st = stateWith([section('roleRules', { text: 'текст из общего слоя', textSource: 'global', enabledSource: 'global' })]);
    const next = startOwn(st, 'roleRules');
    expect(next.overrides.roleRules).toEqual({ mode: 'own', text: 'текст из общего слоя' });
  });

  // Сервер отдаёт полный каталог: базовый текст выключенной специальностью секции есть.
  it('для выключенной специальностью копирует базовый текст', () => {
    const st = stateWith([section('dossier', { enabled: false, text: 'базовый текст dossier' })]);
    expect(startOwn(st, 'dossier').overrides.dossier).toEqual({ mode: 'own', text: 'базовый текст dossier' });
  });

  it('несуществующей секции — состояние не меняется; прежнее не мутируется', () => {
    const st = stateWith([section('a')]);
    expect(startOwn(st, 'нет-такой')).toBe(st);
    const next = startOwn(st, 'a');
    expect(st.overrides).toEqual({});
    expect(next).not.toBe(st);
  });
});

describe('modeOf — режим строки формы', () => {
  it('без локального перехода — серверный режим', () => {
    const st = stateWith([
      section('a'),
      section('b', { enabled: false, enabledSource: 'persona' }),
      section('c', { textSource: 'persona' }),
    ]);
    expect(modeOf(st, 'a')).toBe('inherit');
    expect(modeOf(st, 'b')).toBe('off');
    expect(modeOf(st, 'c')).toBe('own');
  });

  it('локальный переход сильнее серверного', () => {
    const st = stateWith([section('c', { textSource: 'persona' })], { c: { mode: 'off' } });
    expect(modeOf(st, 'c')).toBe('off');
  });

  // Явный «как у специальности» на own-секции с сервера — строка становится inherit:
  // без отметки её режим читался бы с сервера и возврат был бы недостижим.
  it('явный inherit сильнее серверного own', () => {
    const st = stateWith([section('c', { textSource: 'persona' })], { c: { mode: 'inherit' } });
    expect(modeOf(st, 'c')).toBe('inherit');
  });
});

describe('toOverrides — только отличия (тело PUT promptSections)', () => {
  const EFF = [
    section('roleRules', { text: 'типовой текст роли', textSource: 'global', enabledSource: 'global' }),
    section('dossier', { enabled: false, text: 'базовый текст dossier', enabledSource: 'code' }),
  ];

  // Нетронутое состояние — пустой список: «всё как у специальности» (c1).
  it('нет локальных переходов и всё унаследовано → []', () => {
    expect(toOverrides(stateWith(EFF))).toEqual([]);
  });

  // «Своё» без правки черновика — повтор унаследованного, override не нужен (c1).
  it('own с текстом, равным тексту специальности → []', () => {
    const st = startOwn(stateWith(EFF), 'roleRules');
    expect(toOverrides(st)).toEqual([]);
  });

  it('own со своим текстом → [{id, text}] (c1)', () => {
    const st = setOwnText(startOwn(stateWith(EFF), 'roleRules'), 'roleRules', 'мой текст роли');
    expect(toOverrides(st)).toEqual([{ id: 'roleRules', text: 'мой текст роли' }]);
  });

  it('off → [{id, enabled:false}] (c1)', () => {
    expect(toOverrides(setOff(stateWith(EFF), 'roleRules'))).toEqual([{ id: 'roleRules', enabled: false }]);
  });

  // Свой текст выключенной специальностью секции — тоже отличие (enabled при этом не
  // просим: его слой не трогаем, оверлей бэка оставит базовый enabled).
  it('own для выключенной специальностью → [{id, text}] без enabled', () => {
    const st = setOwnText(startOwn(stateWith(EFF), 'dossier'), 'dossier', 'свой текст dossier');
    expect(toOverrides(st)).toEqual([{ id: 'dossier', text: 'свой текст dossier' }]);
  });

  // Существующий override обязан попасть в целую замену: отсутствие молча снесло бы его.
  it('уже own на сервере без правок → свой текст сохраняется', () => {
    const st = stateWith([section('roleRules', { text: 'свой текст персоны', textSource: 'persona', enabledSource: 'persona', enabled: true })]);
    expect(toOverrides(st)).toEqual([{ id: 'roleRules', text: 'свой текст персоны' }]);
  });

  it('уже off на сервере без правок → {id, enabled:false} сохраняется', () => {
    const st = stateWith([section('dossier', { enabled: false, enabledSource: 'persona' })]);
    expect(toOverrides(st)).toEqual([{ id: 'dossier', enabled: false }]);
  });

  // Возврат «как у специальности» для серверного own: id помечен inherit и не выходит —
  // целая замена без него удаляет существующий override.
  it('backToInherit для own-секции → [] (удаление через целую замену)', () => {
    const st = stateWith([section('roleRules', { text: 'свой текст', textSource: 'persona' })], { roleRules: { mode: 'own', text: 'черновик' } });
    expect(toOverrides(backToInherit(st, 'roleRules'))).toEqual([]);
  });

  it('backToInherit для унаследованной секции — в записи ничего не меняется', () => {
    expect(toOverrides(backToInherit(stateWith(EFF), 'roleRules'))).toEqual([]);
  });

  // Та же читка текста, что у бэка (PromptSectionOverlay.Apply): trim, пустое — «не задано».
  it('пустой или пробельный черновик — не свой текст → []', () => {
    expect(toOverrides(setOwnText(startOwn(stateWith(EFF), 'roleRules'), 'roleRules', '   '))).toEqual([]);
  });

  it('черновик trim-ится перед записью', () => {
    const st = setOwnText(startOwn(stateWith(EFF), 'roleRules'), 'roleRules', '  мой текст  ');
    expect(toOverrides(st)).toEqual([{ id: 'roleRules', text: 'мой текст' }]);
  });

  // Порядок — порядок ответа (тот же закон, что у бэкового Apply:30).
  it('порядок — порядок секций ответа', () => {
    const st = setOff(setOff(stateWith(EFF), 'dossier'), 'roleRules');
    expect(toOverrides(st).map(o => o.id)).toEqual(['roleRules', 'dossier']);
  });

  it('локальный переход для id вне ответа не выходит', () => {
    const st = stateWith(EFF, { 'нет-такой': { mode: 'off' } });
    expect(toOverrides(st)).toEqual([]);
  });

  // Явный effective — источник сравнения вызывающего (сигнатура контракта).
  it('effective аргумент сильнее состояния', () => {
    const st = stateWith([], { roleRules: { mode: 'own', text: 'тот же текст' } });
    expect(toOverrides(st, [section('roleRules', { text: 'тот же текст', textSource: 'global' })])).toEqual([]);
  });
});

describe('textCounter / overLimit', () => {
  it('textCounter — «N / 1024» (c1)', () => {
    expect(textCounter('абв')).toBe('3 / 1024');
    expect(textCounter('')).toBe(`0 / ${TEXT_LIMIT}`);
  });

  it('overLimit — порог строго больше лимита', () => {
    expect(overLimit('а'.repeat(TEXT_LIMIT))).toBe(false);
    expect(overLimit('а'.repeat(TEXT_LIMIT + 1))).toBe(true);
  });
});

describe('applySections — ответ применяется своей персоне', () => {
  const SECTIONS_A = [section('roleRules'), section('dossier', { enabled: false })];

  it('ответ своей персоны: состояние загружается, черновики сбрасываются', () => {
    const draft = setOff(stateWith(SECTIONS_A, { roleRules: { mode: 'own', text: 'черновик' } }), 'dossier');
    const next = applySections(draft, 'p-1', SECTIONS_A);
    expect(next.effective).toEqual(SECTIONS_A);
    expect(next.overrides).toEqual({});
  });

  // Гонка (c2): персону сменили, пока запрос шёл — поздний ответ чужой не перекрашивает строки.
  it('устаревший ответ prompt-sections после смены персоны не применяется', () => {
    const stateB = emptyState('p-2');
    const late = applySections(stateB, 'p-1', SECTIONS_A); // ответ p-1 пришёл, когда открыта p-2
    expect(late).toBe(stateB);
    expect(late.effective).toEqual([]);
    // и наоборот: состояние p-1 не принимает ответ p-2
    const stateA = applySections(emptyState('p-1'), 'p-1', SECTIONS_A);
    const after = applySections(stateA, 'p-2', [section('чужая-секция')]);
    expect(after).toBe(stateA);
    expect(after.effective).toEqual(SECTIONS_A);
  });

  it('массив ответа копируется: состояние не делит его с результатом запроса', () => {
    const resp = [section('roleRules')];
    const st = applySections(emptyState('p-1'), 'p-1', resp);
    st.effective[0] = section('подменено');
    expect(resp[0].id).toBe('roleRules');
  });
});
