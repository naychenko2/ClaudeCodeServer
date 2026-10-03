// Лестница схлопывания строки контекста (макет composer-context-row-v1). Чистая функция:
// вход — ширина строки и дискретные факты, DOM не меряется. Открытость панели «Контекст»
// во вход не входит никогда (Р4): строка не мигает при открытии и закрытии панели.
//
// Номиналы — max-width на самом чипе: чип физически не шире номинала, поэтому сумма номиналов
// ступени — верхняя оценка её ширины.

export const NOM = {
  pad: 10, gap: 6, vsep: 1,
  g3: 262, g2: 150, g1: 76, g0: 34,
  o2: 150, o1: 112,
  e3: 320, e2: 196, e1: 112,
  plus: 14, ref: 104, more: 46,
} as const;

// Потолки ширины чипов на полной форме: номиналы нужны таблице макета, а реальный шрифт и имена файлов требуют
// больше — строка сама проверяет вёрстку (ContextRowView) и при тесноте переходит на следующую ступень
export const CAP = { g3: 300, o2: 190, ref: 150 } as const;

// Ветка на полной форме растёт по содержимому: потолок чипа и имени условный, реальную тесноту ловит лестница
// строки (после вёрстки не влезло — шаг назад на g2, где имя режется до 76). null у имени — без ограничения
export const GIT_CHIP_MAX_FREE = 560;
export function gitChipBox(form: 0 | 1 | 2 | 3): { chip: number; label: number | null } {
  if (form === 3) return { chip: GIT_CHIP_MAX_FREE, label: null };
  return { chip: form === 0 ? NOM.g0 : NOM[`g${form}` as 'g1'], label: form === 2 ? 76 : 0 };
}

export interface LadderFacts {
  // Проектный чат: слева чип ветки; в личном чате ветки нет
  project: boolean;
  hasPrimary: boolean;
  // «Чем» в строке только при выбранном действии-запуске (Р2); в «Чате» занимает 0 px
  hasExec: boolean;
  refs: number;
  // Телефон: ветка иконкой (g0), лестницы нет
  mobile?: boolean;
}

// Форма каждого чипа на ступени: g — ветка (3 полная … 0 телефон), o — объект, e — «Чем»,
// k — сколько референсов остаётся на виду (остальные уходят в «+N ›»)
export interface LadderForm { g: 0 | 1 | 2 | 3; o: 1 | 2; e: 1 | 2 | 3; k: number }

export interface LadderPick {
  form: LadderForm;
  index: number;
  count: number;
  need: number;
  // Ниже последней ступени строка прокручивается: чипы не режутся никогда
  scroll: boolean;
}

export function ladderRungs(f: LadderFacts): LadderForm[] {
  const exec = f.hasPrimary && f.hasExec;
  let cur: LadderForm = { g: 3, o: 2, e: 3, k: f.refs };
  const out: LadderForm[] = [cur];
  const step = (patch: Partial<LadderForm>) => { cur = { ...cur, ...patch }; out.push(cur); };
  if (exec) step({ e: 2 });
  if (f.project) step({ g: 2 });
  for (let k = f.refs - 1; k >= 0; k--) step({ k });
  if (f.project) step({ g: 1 });
  if (f.hasPrimary) step({ o: 1 });
  if (exec) step({ e: 1 });
  return out;
}

export function ladderNominal(f: LadderFacts, form: LadderForm): number {
  const exec = f.hasPrimary && f.hasExec;
  const items: number[] = [];
  const left: number[] = [];
  if (f.project) left.push(f.mobile ? NOM.g0 : NOM[`g${form.g}` as 'g1']);
  const rest: number[] = [];
  if (f.hasPrimary) rest.push(form.o === 1 ? NOM.o1 : NOM.o2);
  if (exec) rest.push(NOM[`e${form.e}` as 'e1']);
  if (f.refs > 0) {
    rest.push(NOM.plus);
    for (let i = 0; i < form.k; i++) rest.push(NOM.ref);
    rest.push(NOM.more);
  }
  if (left.length && rest.length) left.push(NOM.vsep);
  items.push(...left, ...rest);
  return NOM.pad + items.reduce((a, b) => a + b, 0) + NOM.gap * Math.max(0, items.length - 1);
}

export function contextRowLadder(width: number, f: LadderFacts): LadderPick {
  const rungs = ladderRungs(f);
  for (let i = 0; i < rungs.length; i++) {
    const need = ladderNominal(f, rungs[i]);
    if (need <= width) return { form: rungs[i], index: i, count: rungs.length, need, scroll: false };
  }
  const i = rungs.length - 1;
  return { form: rungs[i], index: i, count: rungs.length, need: ladderNominal(f, rungs[i]), scroll: true };
}
