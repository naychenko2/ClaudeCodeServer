// Раскладка полосы контролов композера («губы»): номиналы блоков и выбор ступени
// по бюджету ширины. Вынесено из Composer.tsx в чистый модуль ради юнит-теста
// лестницы (экспорт из файла компонента запрещён react-refresh-линтом; прецедент —
// lib/videoStrip). Компонентных зависимостей здесь нет.
//
// Номиналы блоков полосы (десктоп / мобила, px, «с учётом зазора»). Источник —
// docs/mockups/composer-strip-priority.md. Изменил число здесь — обнови спеку и
// макет (docs/mockups/composer-strip-priority.html) парой, иначе бюджет разойдётся
// с фактом.

export const STRIP_LEFT_NOMINAL = { d: 112, m: 48 } as const;
export const STRIP_MENU_NOMINAL = { d: 38, m: 46 } as const;
export const STRIP_BUTTON_NOMINAL = { d: 36, m: 42 } as const;
// Горизонтальный padding полосы (используется в бюджете). Десктоп берёт gap через
// фиксированные `gap`, мобил — `2px` padding с обеих сторон по макету
export const STRIP_PADX = { d: 16, m: 4 } as const;
export const STRIP_GAP = { d: 4, m: 6 } as const;

// Формы правой группы (плашка «модель · усилие» и собеседник). Лестница деградации
// подписей при сужении полосы — в порядке важности подписи для человека:
//   1. столбики уровня усилия уходят первыми (уровень виден в тултипе и в меню),
//   2. собеседник — вторым (кто ведёт чат важнее уровня рассуждения),
//   3. модель — последней (имя модели держится до предела).
// Порядок ЗАДАЁТСЯ последовательностью форм в pickLayout: B снимает уровень усилия,
// B2 добавляет к нему собеседника, C снимает и модель.
export type StripForm = 'A-wide' | 'A' | 'B' | 'B2' | 'C';

export const STRIP_RIGHT_NOMINAL = {
  'A-wide': 309,           // всё словами, собеседник до 160 px (имя над ролью, две строки)
  'A': 279,                // всё словами, собеседник короткий (≤140 px)
  'B': 254,                // столбики усилия сняты, модель+собеседник словами
  'B2': 163,               // собеседник иконкой, модель словом
  'C': { d: 100, m: 104 }, // всё иконками (мобильный номинал чуть больше за тач-цели)
} as const;

// MAX-ширины детей правой группы по форме. Плашка модели получает model + effort
// (ComposerMenu.maxTriggerWidth), собеседник — companionLabel (CompanionSelector.maxLabelWidth);
// зазор внутри правой группы ВКЛЮЧЁН в номинал. effort — это не отдельная кнопка, а
// столбики уровня внутри плашки модели: зазор 6 + пять столбиков по 3 px с просветом 1 = 25.
// Компактная плашка (иконка + шеврон) — 39 px: паддинги 6+6 + иконка 14 + зазор 3 +
// шеврон 10; компактный собеседник — 49: те же паддинги + аватар 24 + зазор 3 + шеврон 10.
//   A-wide: модель 120 + усилие 25 + зазор 4 + собеседник 160 = 309.
//   A:  модель 110 + усилие 25 + зазор 4 + собеседник 140 = 279.
//   B:  модель 110 + зазор 4 + собеседник 140 = 254.
//   B2: модель 110 + зазор 4 + собеседник compact 49 = 163.
//   C:  без maxWidth — плашка и собеседник compact (39 + 4 + 49 = 92, с запасом 100;
//     на мобиле плашка 40 — тач-цель, 40 + 6 + 49 = 95 при номинале 104).
// null = подпись снята (усилие) или ребёнок в компактной форме, потолок не нужен.
export const STRIP_RIGHT_MAX = {
  'A-wide': { model: 120, effort: 25, companionLabel: 160 },
  'A':      { model: 110, effort: 25, companionLabel: 140 },
  'B':      { model: 110, effort: null, companionLabel: 140 /* столбики усилия сняты */ },
  'B2':     { model: 110, effort: null, companionLabel: null /* и собеседник compact */ },
  'C':      { model: null, effort: null, companionLabel: null /* всё compact */ },
} as const;

// Ширины пилюль состояния. teamPill имеет «полную» и «компактную» формы,
// teamImplementBadge — «полную» и «без чипа Авто», loopPill — только полную (в
// компактную не сворачивается, она уезжает сразу в «⋯»). Алгоритм бюджета считает
// от максимума active-форм.
export const STRIP_PILL_NOMINAL = {
  teamPill:           { full: { d: 150, m: 54 }, compact: { d: 130, m: 72 } },
  teamImplementBadge: { full: { d: 180, m: 118 }, noauto: { d: 160, m: 118 } },
  loopPill:           { d: 155, m: 62 },
} as const;

export type StripLayout = {
  rightForm: StripForm;
  rightWidth: number;
  compactTeamPill: boolean;
  autoChipVisible: boolean;
  loopInMenu: boolean;
  krInMenu: boolean;
};

// Лестница ступеней полосы контролов (этап 2 composer-strip-priority). Единственный
// вход — `stripWidth` плюс три дискретных флага активных бейджей и `leftBudget` —
// суммарная ширина видимых левых кнопок; DOM не измеряется нигде, чтобы ширина правой
// группы не зависела от собственного результата (иначе вернётся петля из этапа 1).
// Шаг 1: перебираем форму правой группы A-wide → A → B → B2 → C, берём первую, при
// которой все активные бейджи в полной форме + левый блок + видимые левые кнопки
// + «⋯» (если в нём что-то есть) влезают. Шаг 2: если при C всё равно не влезает —
// понижаем бейджи по рангу снизу вверх (имя пилюли механики → чип «Авто» → цикл
// в «⋯» → КР в «⋯»), форма C остаётся. Пилюля механики остаётся всегда — ниже
// ранга 1 деградации нет.
//
// leftBudget — сумма ширин видимых левых кнопок + зазоры между ними, отдельным
// параметром (а не внутри STRIP_LEFT_NOMINAL): она зависит от useActionVisibility,
// и тянуть весь переключатель видимости в lib — лишняя связность. Смысл: пока
// левая полоса держится (приоритет «сначала правые»), правые должны учитывать её
// в бюджете и сжиматься раньше
export function pickLayout(
  stripWidth: number,
  hasTP: boolean,
  hasKR: boolean,
  hasLoop: boolean,
  isMobile: boolean,
  leftBudget: number = 0,
): StripLayout {
  const dKey = isMobile ? 'm' : 'd';
  // Номинал правой группы в текущей форме (для C — мобильный или десктопный отдельно)
  const rightW = (form: StripForm) =>
    form === 'C' ? STRIP_RIGHT_NOMINAL.C[dKey] : STRIP_RIGHT_NOMINAL[form];
  // Бейдж в текущей форме. Активный бейдж, для которого не указана форма, даёт максимум
  // («полную»), чтобы вписать худший случай — короткие имена (КС) не вылезали бы
  // неожиданно за бюджет
  const tpW = (compact: boolean) => STRIP_PILL_NOMINAL.teamPill[compact ? 'compact' : 'full'][dKey];
  const krW = (noAuto: boolean) => STRIP_PILL_NOMINAL.teamImplementBadge[noAuto ? 'noauto' : 'full'][dKey];
  const lpW = () => STRIP_PILL_NOMINAL.loopPill[dKey];
  // Левый блок + зазоры по обе стороны + правый блок = «несжимаемый бюджет» полосы без
  // учёта бейджей. Меню и его зазор добавляются только если в «⋯» реально что-то уехало.
  // leftBudget — место под видимые левые кнопки: пока левая держится, правая должна
  // сжиматься раньше, поэтому включаем её в бюджет
  const baseW = STRIP_LEFT_NOMINAL[dKey] + STRIP_PADX[dKey] + STRIP_GAP[dKey] * 2 + leftBudget;
  const menuW = STRIP_MENU_NOMINAL[dKey] + STRIP_GAP[dKey];
  // Шаг 1 — выбор формы правой группы. До замера (0) сразу даём «середину»: B на
  // десктопе, C на мобиле. Промах на ступень незаметен, мигание A→C бросается в глаза
  if (stripWidth === 0) {
    return isMobile
      ? { rightForm: 'C', rightWidth: rightW('C'), compactTeamPill: hasTP, autoChipVisible: hasKR,
          loopInMenu: hasLoop, krInMenu: hasKR }
      : { rightForm: 'B', rightWidth: rightW('B'), compactTeamPill: false, autoChipVisible: true,
          loopInMenu: false, krInMenu: false };
  }
  // На ступенях жертв правая группа остаётся C, а бейджи теряют по одному рангу снизу.
  // Самый «толстый» сценарий — все три бейджа в полной форме без меню, он задаёт верхнюю
  // границу для выбора правой группы
  const candidates: StripForm[] = isMobile
    ? ['C']
    : ['A-wide', 'A', 'B', 'B2', 'C'];
  for (const f of candidates) {
    // Меню нужно, только если в нём уже сейчас будет хотя бы один бейдж. На шаге 1 это
    // означает «найдётся форма, в которой один из ушедших бейджей не нужен» — но мы ещё
    // не знаем, какой. Считаем «полную», как если бы всё влезло в полосу: меню не
    // резервируем, потому что первый кадр оно не нужно, иначе пустая кнопка «⋯»
    // съедала бы себе драгоценное место
    const w = baseW + rightW(f) + (hasTP ? tpW(false) : 0) + (hasKR ? krW(false) : 0) + (hasLoop ? lpW() : 0);
    if (w <= stripWidth) return { rightForm: f, rightWidth: rightW(f),
      compactTeamPill: false, autoChipVisible: true, loopInMenu: false, krInMenu: false };
  }
  // Шаг 2 — ни одна форма правой группы не вместила всё в полной форме. Понижаем бейджи
  // по рангу снизу вверх. На каждой ступени считаем, помещается ли её бюджет: так
  // получаем ОДНУ первую «самую щедрую» ступень, которая влезает
  const ladder: Array<StripLayout> = [
    // 1) имя в пилюле механики → компактная
    { rightForm: 'C', rightWidth: rightW('C'), compactTeamPill: true, autoChipVisible: true, loopInMenu: false, krInMenu: false },
    // 2) чип «Авто» уезжает (переключатель переезжает в поповер)
    { rightForm: 'C', rightWidth: rightW('C'), compactTeamPill: true, autoChipVisible: false, loopInMenu: false, krInMenu: false },
    // 3) пилюля цикла → в «⋯» (появляется кнопка «⋯» — резервируем её ширину)
    { rightForm: 'C', rightWidth: rightW('C'), compactTeamPill: true, autoChipVisible: false, loopInMenu: true, krInMenu: false },
    // 4) бейдж КР → в «⋯» (предел; пилюля механики остаётся всегда)
    { rightForm: 'C', rightWidth: rightW('C'), compactTeamPill: true, autoChipVisible: false, loopInMenu: true, krInMenu: true },
  ];
  for (const step of ladder) {
    // Только те ступени, которые реально что-то меняют для активных бейджей, имеют смысл
    // (например, autoChipVisible=false ничего не даёт, если hasKR=false)
    if (!hasTP && step.compactTeamPill) continue;
    if (!hasKR && !step.autoChipVisible) continue;
    if (!hasKR && step.krInMenu) continue;
    if (!hasLoop && step.loopInMenu) continue;
    // На ступенях с ушедшими бейджами «⋯» появляется → учитываем menuW. Ступень 2 (без
    // авто) — бейдж КР ещё на полосе, меню не нужно. Ступени 3-4 — нужен «⋯»
    const needMenu = step.loopInMenu || step.krInMenu;
    const w = baseW + step.rightWidth + menuW * (needMenu ? 1 : 0)
      + (hasTP ? tpW(step.compactTeamPill) : 0)
      + (hasKR && !step.krInMenu ? krW(!step.autoChipVisible) : 0)
      + (hasLoop && !step.loopInMenu ? lpW() : 0);
    // Может, полоса настолько узкая, что и предельная ступень не влезает — на мобильных
    // номиналах ниже 360 не проектируем, оставляем как есть (визуальный оверфлоу
    // невозможен, см. таблицу проверки в спеке)
    if (w <= stripWidth || step === ladder[ladder.length - 1]) return step;
  }
  // Если hasTP+hasKR+hasLoop === false, лестница даст предельную ступень выше; в этом
  // месте код недостижим, но TS требует возврат
  return ladder[ladder.length - 1];
}
