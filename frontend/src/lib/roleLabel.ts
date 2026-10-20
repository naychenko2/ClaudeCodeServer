// ОДНА подпись роли задачи фичи из сырых значений (taskRole/stepRef/lane) — для
// плашки (FeatureBanner), шапки (FeatureBannerSheet) и страницы фичи. Бэк роль НЕ
// подписывает, отдаёт сырые поля; фронт строит подпись. Без догадок: словарь по роли,
// для step — «{stepRef} · {бэк|фронт}» (линия нормализуется, как в laneOfChain).

// Нормализация линии: зеркалит laneOfChain (components/chat/feature/PipelineFlow.tsx):
// back/бэк → «бэк», front/фронт → «фронт», прочее/пусто → '' (без второй части).
// Держим в синхроне с laneOfChain — при правке одного правим и другой.
function laneWord(lane: string | null): string {
  const l = (lane ?? '').trim().toLowerCase();
  if (l === 'back' || l === 'backend' || l === 'бэк' || l === 'бэкенд') return 'бэк';
  if (l === 'front' || l === 'frontend' || l === 'фронт' || l === 'фронтенд') return 'фронт';
  return '';
}

// Подпись роли:
//   step     → «{stepRef} · {бэк|фронт}» (stepRef как есть; без линии — только stepRef);
//   rework   → «доработка {stepRef}»;
//   review   → «ревью»;  qa → «проверка»;  final → «финал»;  merge → «мерж»;
//   cut      → «разрез»;  plan → «план»;  consult → «консультация»;
//   прочее/null → пустая строка.
export function roleLabel(taskRole: string | null, stepRef: string | null, lane: string | null): string {
  switch (taskRole) {
    case 'step': {
      const w = laneWord(lane);
      if (!stepRef) return w;
      return w ? `${stepRef} · ${w}` : stepRef;
    }
    case 'rework':
      return stepRef ? `доработка ${stepRef}` : 'доработка';
    case 'review':
      return 'ревью';
    case 'qa':
      return 'проверка';
    case 'final':
      return 'финал';
    case 'merge':
      return 'мерж';
    case 'cut':
      return 'разрез';
    case 'plan':
      return 'план';
    case 'consult':
      return 'консультация';
    default:
      return '';
  }
}
