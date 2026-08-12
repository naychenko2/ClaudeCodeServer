import { describe, it, expect } from 'vitest';
import {
  validateLocalActionRoute,
  routeCanSave,
  RATE_NO_PROVIDER,
  RATE_PICKER_HINT,
  rateUnknownModel,
  ratePresetMissing,
  RATE_PRESET_EMPTY,
  RATE_MODEL_SPACES,
} from '../routeFormat';
import type { ModelOption } from '../models';

// Каталог моделей как срез /api/models: «обычные» модели по id + модели прямого
// адаптера со сквозным префиксом direct:. Через список проверяется форма 8.
const MODELS: ModelOption[] = [
  { value: 'opus', label: 'Opus' },
  { value: 'glm-5.2[1m]', label: 'GLM 5.2' },
  { value: 'direct:nvidia/nemotron:free', label: 'Nemotron (free)' },
  { value: 'direct:MiniMax-M3', label: 'MiniMax M3' },
];
const PRESET_IDS = ['6f1c0f6a', 'eco-chain'];

const CAT = { models: MODELS, presetIds: PRESET_IDS };
const ok = (v: string) => validateLocalActionRoute(v, CAT);

describe('validateLocalActionRoute — формы 1–6 (литералы и tier)', () => {
  it('пропускает local/claude/default и tier:*', () => {
    expect(ok('local')).toEqual({ ok: true });
    expect(ok('claude')).toEqual({ ok: true });
    expect(ok('default')).toEqual({ ok: true });
    expect(ok('tier:strong')).toEqual({ ok: true });
    expect(ok('tier:medium')).toEqual({ ok: true });
    expect(ok('tier:weak')).toEqual({ ok: true });
  });

  it('литералы регистрозависимы (Local/LOCAL — не валидны)', () => {
    // ADR-009 §2: switch по строковым константам. UPPER → не опознаётся → форма 8 → нет в каталоге.
    expect(ok('Local').ok).toBe(false);
    expect(ok('LOCAL').ok).toBe(false);
  });

  it('tier: с неизвестным уровнем отбрасывается', () => {
    const r = ok('tier:powerful');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(rateUnknownModel('tier:powerful'));
  });

  it('tier: с внутренним пробелом не разбирается', () => {
    // «tier: strong» — уровень « strong», не узнаётся, уходит в форму 8, которой нет в каталоге.
    expect(ok('tier: strong').ok).toBe(false);
  });
});

describe('validateLocalActionRoute — форма 7 (preset:{id})', () => {
  it('пропускает существующий пресет, регистр префикса не важен', () => {
    expect(ok('preset:6f1c0f6a')).toEqual({ ok: true });
    expect(ok('Preset:eco-chain')).toEqual({ ok: true });
  });

  it('preset без id отбрасывается', () => {
    const r = ok('preset:');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(RATE_PRESET_EMPTY);
  });

  it('несуществующий пресет — текст про пресет, а не про модель', () => {
    const r = ok('preset:gone');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(ratePresetMissing('gone'));
  });
});

describe('validateLocalActionRoute — форма 8 ({id модели})', () => {
  it('пропускает модель из каталога', () => {
    expect(ok('opus')).toEqual({ ok: true });
    expect(ok('glm-5.2[1m]')).toEqual({ ok: true });
    expect(ok('direct:MiniMax-M3')).toEqual({ ok: true });
  });

  it('голое имя прямой модели — случай прод-дефекта: текст noProvider', () => {
    // MiniMax-M3 в каталоге есть только как direct:MiniMax-M3 → модель записана без поставщика.
    const r = ok('MiniMax-M3');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(RATE_NO_PROVIDER);
  });

  it('несуществующая модель — текст unknownModel с именем', () => {
    const r = ok('gpt-4o-mini');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(rateUnknownModel('gpt-4o-mini'));
  });

  it('пробелы в id модели отбрасываются', () => {
    const r = ok('glm 4.7');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(RATE_MODEL_SPACES);
  });

  it('«strong» без префикса — не слот, а неизвестная модель', () => {
    // ADR-009 §3: «strong» трактуется как id модели, которого нет в каталоге.
    const r = ok('strong');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.message).toBe(rateUnknownModel('strong'));
  });
});

describe('validateLocalActionRoute — крайние случаи', () => {
  it('пустое и только-пробелы — блокируем сохранение без текста ошибки', () => {
    expect(validateLocalActionRoute('', CAT)).toEqual({ ok: false, message: '' });
    expect(validateLocalActionRoute('   ', CAT)).toEqual({ ok: false, message: '' });
    expect(validateLocalActionRoute(null, CAT)).toEqual({ ok: false, message: '' });
  });

  it('без каталога модель/прямую модель проверить нельзя — синтаксис валиден', () => {
    expect(validateLocalActionRoute('any-id', {}).ok).toBe(true);
    expect(validateLocalActionRoute('direct:any-id', {}).ok).toBe(true);
    // но пресет без id и пробелы ловятся и без каталога
    expect(validateLocalActionRoute('preset:', {}).ok).toBe(false);
    expect(validateLocalActionRoute('a b', {}).ok).toBe(false);
  });

  it('хвостовой пробел снимается внешним trim', () => {
    // ADR-009 §2: в сторе хвостовых пробелов не бывает (Set делает trim).
    expect(validateLocalActionRoute('tier:strong ', CAT)).toEqual({ ok: true });
    expect(validateLocalActionRoute(' opus ', CAT)).toEqual({ ok: true });
  });
});

describe('routeCanSave', () => {
  it(' true только для валидных значений', () => {
    expect(routeCanSave('tier:strong', CAT)).toBe(true);
    expect(routeCanSave('opus', CAT)).toBe(true);
    expect(routeCanSave('MiniMax-M3', CAT)).toBe(false);
    expect(routeCanSave('', CAT)).toBe(false);
    expect(routeCanSave('preset:gone', CAT)).toBe(false);
  });
});

describe('тексты — правило языка раздела', () => {
  // docs/features: ни в одном тексте нет слов «маршрут», «префикс», direct:, «каталог», «валидация».
  const STOP = [/маршрут/i, /префикс/i, /direct:/i, /каталог/i, /валидаци/i];

  it('главные тексты не содержат стоп-слов', () => {
    const samples = [
      RATE_NO_PROVIDER,
      RATE_PICKER_HINT,
      rateUnknownModel('x'),
      ratePresetMissing('x'),
      RATE_PRESET_EMPTY,
      RATE_MODEL_SPACES,
    ];
    for (const t of samples) {
      for (const re of STOP) expect(re.test(t)).toBe(false);
    }
  });
});
