import { describe, expect, it } from 'vitest';
import {
  pickLayout, STRIP_RIGHT_MAX, STRIP_RIGHT_NOMINAL,
  STRIP_LEFT_NOMINAL, STRIP_PADX, STRIP_GAP, STRIP_PILL_NOMINAL,
} from '../composerStrip';

// Лестница полосы контролов композера. Порядок деградации подписей правой группы:
// усилие (B) → собеседник (B2) → модель (C). Граничные ширины ниже посчитаны
// руками от номиналов: база = левый блок + паддинг + два зазора.
//   база            = 112 + 16 + 4×2          = 136
//   A-wide влезает с = 136 + 534               = 670
//   A       влезает с = 136 + 384               = 520
//   B       влезает с = 136 + 313               = 449
//   B2      влезает с = 136 + 222               = 358
//   C       влезает с = 136 + 160               = 296
const NO_BADGES = { hasTP: false, hasKR: false, hasLoop: false } as const;
const form = (w: number, isMobile = false) =>
  pickLayout(w, NO_BADGES.hasTP, NO_BADGES.hasKR, NO_BADGES.hasLoop, isMobile).rightForm;

describe('pickLayout: лестница правой группы (десктоп)', () => {
  it('широкая полоса — всё словами (A-wide)', () => {
    expect(form(1200)).toBe('A-wide');
    expect(form(670)).toBe('A-wide'); // ровно на границе номинала
  });

  it('сужение — подпись собеседника укорачивается первой (A)', () => {
    expect(form(669)).toBe('A');
    expect(form(520)).toBe('A');
  });

  it('дальше — усилие иконкой, собеседник ещё с подписью (B)', () => {
    expect(form(519)).toBe('B');
    expect(form(449)).toBe('B');
  });

  it('ещё уже — усилие и собеседник иконками, модель словом (B2)', () => {
    expect(form(448)).toBe('B2');
    expect(form(358)).toBe('B2');
  });

  it('предел — всё иконками (C)', () => {
    expect(form(357)).toBe('C');
    expect(form(296)).toBe('C');
    expect(form(100)).toBe('C'); // и ниже предела — тоже C, оверфлоу невозможен
  });

  it('ширина правой группы совпадает с номиналом выбранной формы', () => {
    expect(pickLayout(600, false, false, false, false).rightWidth).toBe(STRIP_RIGHT_NOMINAL.A);
    expect(pickLayout(500, false, false, false, false).rightWidth).toBe(STRIP_RIGHT_NOMINAL.B);
    expect(pickLayout(400, false, false, false, false).rightWidth).toBe(STRIP_RIGHT_NOMINAL.B2);
  });
});

describe('pickLayout: потолок подписей по форме (STRIP_RIGHT_MAX)', () => {
  it('усилие теряет подпись с формы B — раньше собеседника', () => {
    expect(STRIP_RIGHT_MAX['A'].effort).toBe(110);        // ещё с подписью
    expect(STRIP_RIGHT_MAX['B'].effort).toBe(null);       // уже иконка…
    expect(STRIP_RIGHT_MAX['B'].companionLabel).toBe(140); // …а собеседник ещё с подписью
  });

  it('собеседник теряет подпись с формы B2 — вторым', () => {
    expect(STRIP_RIGHT_MAX['B2'].companionLabel).toBe(null);
    expect(STRIP_RIGHT_MAX['B2'].model).toBe(110); // модель держится дольше всех
  });

  it('форма C — всё иконками', () => {
    expect(STRIP_RIGHT_MAX['C']).toEqual({ model: null, effort: null, companionLabel: null });
  });
});

describe('pickLayout: мобила и первый кадр', () => {
  it('мобила всегда в форме C — лестница правой группы десктопная', () => {
    expect(form(1200, true)).toBe('C');
    expect(form(360, true)).toBe('C');
  });

  it('до первого замера (0) — середина лестницы: B на десктопе, C на мобиле', () => {
    expect(form(0)).toBe('B');
    expect(form(0, true)).toBe('C');
  });
});

describe('pickLayout: лестница жертв бейджей (регрессия этапа 2)', () => {
  const base = STRIP_LEFT_NOMINAL.d + STRIP_PADX.d + STRIP_GAP.d * 2;
  const menu = 38 + STRIP_GAP.d;
  const tp = STRIP_PILL_NOMINAL.teamPill;
  const kr = STRIP_PILL_NOMINAL.teamImplementBadge;
  const lp = STRIP_PILL_NOMINAL.loopPill.d;
  const cRight = STRIP_RIGHT_NOMINAL.C.d;

  it('три бейджа в полной форме + C — если влезает, жертв нет', () => {
    const w = base + cRight + tp.full.d + kr.full.d + lp;
    const l = pickLayout(w, true, true, true, false);
    expect(l.rightForm).toBe('C');
    expect(l.compactTeamPill).toBe(false);
    expect(l.autoChipVisible).toBe(true);
    expect(l.loopInMenu).toBe(false);
    expect(l.krInMenu).toBe(false);
  });

  it('не влезает — сначала компактится имя пилюли механики (ступень 1)', () => {
    const w = base + cRight + tp.compact.d + kr.full.d + lp;
    const l = pickLayout(w, true, true, true, false);
    expect(l.compactTeamPill).toBe(true);
    expect(l.autoChipVisible).toBe(true); // чип «Авто» ещё на полосе
    expect(l.loopInMenu).toBe(false);
    expect(l.krInMenu).toBe(false);
  });

  it('ещё уже — чип «Авто» уезжает в поповер бейджа (ступень 2)', () => {
    const w = base + cRight + tp.compact.d + kr.noauto.d + lp;
    const l = pickLayout(w, true, true, true, false);
    expect(l.compactTeamPill).toBe(true);
    expect(l.autoChipVisible).toBe(false);
    expect(l.loopInMenu).toBe(false);
    expect(l.krInMenu).toBe(false);
  });

  it('ещё уже — цикл уезжает в «⋯» (меню резервируется в бюджете)', () => {
    const w = base + cRight + menu + tp.compact.d + kr.noauto.d;
    const l = pickLayout(w, true, true, true, false);
    expect(l.loopInMenu).toBe(true);
    expect(l.krInMenu).toBe(false);
  });

  it('предел — бейдж КР тоже в «⋯», пилюля механики остаётся всегда', () => {
    const l = pickLayout(50, true, true, true, false);
    expect(l.rightForm).toBe('C');
    expect(l.compactTeamPill).toBe(true);
    expect(l.krInMenu).toBe(true);
    expect(l.loopInMenu).toBe(true);
  });
});
