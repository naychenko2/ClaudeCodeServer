import { C, FS, SP, R } from '../../../lib/design';
import type { FeatureRun } from '../../../types/featureRun';

// Линейка стадий конвейера фичи (режим «Исполнение фичи»).
//
// 7 стадий жизненного цикла (рез → план → производство → ревью → проверка → финал → мерж)
// горизонтальной линейкой: пройденные — приглушённые, текущая — подсвечена токенами
// дизайн-системы (C.*) с пульсом «в работе», будущие — ещё приглушённее. На мобиле линейка
// переносится на несколько строк (flexWrap). Компонент «глупый»: фазы считает чистая функция
// `stagePhases(run)` (тестируется отдельно), рендер только раскладывает.

type StageKey = 'cut' | 'plan' | 'prod' | 'review' | 'check' | 'final' | 'merge';
type Phase = 'done' | 'active' | 'pending';

// Стадии в порядке жизненного цикла. `label` — короткая подпись ячейки.
const STAGES: { key: StageKey; label: string }[] = [
  { key: 'cut',    label: 'Рез' },
  { key: 'plan',   label: 'План' },
  { key: 'prod',   label: 'Производство' },
  { key: 'review', label: 'Ревью' },
  { key: 'check',  label: 'Проверка' },
  { key: 'final',  label: 'Финал' },
  { key: 'merge',  label: 'Мерж' },
];

// Все задачи роли завершены (done)? Пустой набор — false (стадия ещё не начата).
function allDone(list: FeatureRun['steps']): boolean {
  return list.length > 0 && list.every(t => t.status === 'done');
}

// Фазы 7 стадий по агрегату `run`. Пройденные — done, первая непройденная — active
// (текущая стадия), остальные — pending. Чистая функция: рендер от неё не зависит.
//
// «Пройдена» = есть завершённая работа роли: рез — эпик есть, план — разложен в шаги,
// производство/ревью/проверка — все задачи роли done, финал — закрыт, мерж — смержен
// (или финал зелёный как маркер закрытия). Текущая стадия — первая, где работа ещё открыта.
export function stagePhases(run: FeatureRun): Phase[] {
  const done: boolean[] = [
    !!run.epic,                                                        // Рез: эпик фичи есть
    run.steps.length > 0,                                            // План: разложен в шаги
    allDone(run.steps),                                              // Производство: все шаги завершены
    allDone(run.reviews),                                            // Ревью: все ревью закрыты
    allDone(run.qa),                                                 // Проверка: все проверки закрыты
    run.final != null && run.final.status === 'done',               // Финал: закрыт
    (run.merge != null && run.merge.status === 'done') || run.ready, // Мерж: смержен / финал зелёный
  ];
  const firstNotDone = done.findIndex(d => !d);
  return done.map((d, i) => {
    if (firstNotDone === -1) return 'done';      // всё пройдено — активной стадии нет
    if (i === firstNotDone) return 'active';     // текущая стадия
    return d ? 'done' : 'pending';
  });
}

// Точка-индикатор стадии. Active — акцентная с пульсом «в работе» (feature-pulse),
// done — приглушённая галочка, pending — пунктирный контур.
function StageDot({ phase }: { phase: Phase }) {
  if (phase === 'active') {
    return (
      <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true" className="feature-pulse">
        <circle cx="6" cy="6" r="5" fill={C.accent} />
        <circle cx="6" cy="6" r="2" fill={C.onAccent} />
      </svg>
    );
  }
  if (phase === 'done') {
    return (
      <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
        <circle cx="6" cy="6" r="5" fill="none" stroke={C.textMuted} strokeWidth="1.4" />
        <path d="M3.6 6.1 5.3 7.8 8.4 4.4" fill="none" stroke={C.textMuted} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }
  return (
    <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
      <circle cx="6" cy="6" r="4.5" fill="none" stroke={C.dashed} strokeWidth="1.4" strokeDasharray="2 1.8" />
    </svg>
  );
}

// Ячейка стадии: точка + подпись. Активная подсвечена (accentLight/accent),
// остальные приглушены (borderLight/dashed + muted-текст).
function StageCell({ label, phase }: { label: string; phase: Phase }) {
  const active = phase === 'active';
  const style: React.CSSProperties = {
    display: 'inline-flex',
    alignItems: 'center',
    gap: SP.xs,
    padding: `${SP.xs}px ${SP.sm}px`,
    borderRadius: R.lg,
    border: `1px solid ${active ? C.accent : phase === 'done' ? C.borderLight : C.dashed}`,
    background: active ? C.accentLight : 'transparent',
    color: active ? C.textHeading : C.textMuted,
    fontSize: FS.xs,
    fontWeight: active ? 700 : 400,
    whiteSpace: 'nowrap',
  };
  return (
    <span style={style} aria-current={active ? 'step' : undefined}>
      <StageDot phase={phase} />
      {label}
    </span>
  );
}

// Линейка 7 стадий. На мобиле переносится на несколько строк (flexWrap).
export function PipelineStages({ run }: { run: FeatureRun }) {
  const phases = stagePhases(run);
  return (
    <div
      style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}
      aria-label="Стадии конвейера фичи"
    >
      {STAGES.map((s, i) => (
        <StageCell key={s.key} label={s.label} phase={phases[i]} />
      ))}
    </div>
  );
}
