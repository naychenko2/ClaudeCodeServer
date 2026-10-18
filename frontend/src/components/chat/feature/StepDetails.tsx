import { useEffect, useState } from 'react';
import { C, R, SP } from '../../../lib/design';
import { openTaskInSection } from '../../../lib/tasks';
import { ArrowRight } from 'lucide-react';
import { Rounds } from './Rounds';
import { RoundChain } from './RoundChain';
import type { FeatureRun, FeatureStepChain } from '../../../types/featureRun';

// Детали шага (макет v2 .det + stepDetails): карточка на C.bgWhite (внутри «Производства»
// на C.bgCard), R.lg, margin-left 0 на всех ширинах. Состав сверху вниз (gap 8):
//   1. шапка — «D1c · 1 ч 10 мин · 3 круга · **принят**» (+ « · из: ревью»);
//   2. пружина витков (Rounds, max 9);
//   3. подпись «Цепочка кругов»;
//   4. сама цепочка (RoundChain);
//   5. справа — ссылка «Открыть задачу →» (на задачу шага; нет задачи — ссылки нет).
//
// Компонент «глупый»: данные — из бэка (chain.status/durationMinutes/rounds/source/taskIds),
// фронт только раскладывает. Подключение в конвейер — Ф8.

// Тик раз в минуту: «идёт N мин» в цепочке не застывает, пока открыты детали.
function useMinuteTick(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 60000);
    return () => clearInterval(t);
  }, []);
  return now;
}

// Длительность: «1 ч 10 мин» / «20 мин».
function durationLabel(min: number): string {
  const m = Math.max(0, Math.round(min));
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const rest = m % 60;
  return rest ? `${h} ч ${rest} мин` : `${h} ч`;
}

// Русские формы: круг/круга/кругов.
function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}

// Метка источника возврата: review → «ревью», qa → «проверка», final → «финал».
const SOURCE_LABEL: Record<string, string> = {
  review: 'ревью',
  qa: 'проверка',
  final: 'финал',
};

// Шапка: «D1c · 1 ч 10 мин · 3 круга · **принят**» (+ « · из: ревью»).
// 12, C.textSecondary; статус — 600 C.textHeading. Число кругов — из chain.rounds.length.
function Header({ chain }: { chain: FeatureStepChain }) {
  const rounds = chain.rounds.length;
  const src = chain.source ? SOURCE_LABEL[chain.source] : null;
  return (
    <div style={{ fontSize: 12, color: C.textSecondary, overflowWrap: 'anywhere' }}>
      {chain.stepRef} · {durationLabel(chain.durationMinutes)} · {rounds} {plural(rounds, 'круг', 'круга', 'кругов')}
      {' '}· <b style={{ color: C.textHeading, fontWeight: 600 }}>{chain.status}</b>
      {src ? ` · из: ${src}` : ''}
    </div>
  );
}

// Подпись секции «Цепочка кругов»: 10, 700, uppercase, letter-spacing .06em, C.textMuted.
function SectionLabel() {
  return (
    <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted }}>
      Цепочка кругов
    </div>
  );
}

// Цель ссылки: задача шага (chain.taskIds[0]) — id + projectId (из run.steps).
// null — задачи нет (ссылку не рисуем). Чистая функция: тестируется без DOM.
export function openTaskTarget(chain: FeatureStepChain, run: FeatureRun): { id: string; projectId?: string } | null {
  if (chain.taskIds.length === 0) return null;
  const id = chain.taskIds[0];
  const first = (run.steps ?? []).find(t => t.id === id);
  return { id, projectId: first?.projectId ?? undefined };
}

// Ссылка «Открыть задачу →»: 12/600 C.accent, ArrowRight 13, справа; minHeight 24 (мобила 40).
// Действие — openTaskInSection на задачу шага (openTaskTarget); нет задачи — ссылки нет.
function OpenTaskLink({ chain, run, isMobile }: { chain: FeatureStepChain; run: FeatureRun; isMobile: boolean }) {
  const [hovered, setHovered] = useState(false);
  if (chain.taskIds.length === 0) return null;
  const open = () => {
    const t = openTaskTarget(chain, run);
    if (t) openTaskInSection(t);
  };
  return (
    <button
      onClick={open}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      title="Открыть задачу шага"
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 5,
        alignSelf: 'flex-end', border: 'none', background: 'transparent', cursor: 'pointer',
        minHeight: isMobile ? 40 : 24, padding: 0,
        fontSize: 12, fontWeight: 600, color: C.accent, fontFamily: 'inherit',
      }}
    >
      Открыть задачу
      <span style={{ display: 'flex', transform: hovered ? 'translateX(3px)' : 'none', transition: 'transform 0.15s' }}>
        <ArrowRight size={13} strokeWidth={2} />
      </span>
    </button>
  );
}

// Детали шага. `isMobile` — для minHeight ссылки (мобила 40).
export function StepDetails({ chain, run, isMobile }: { chain: FeatureStepChain; run: FeatureRun; isMobile: boolean }) {
  const now = useMinuteTick();
  const noRounds = chain.rounds.length === 0;
  return (
    <div
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.sm,
        background: C.bgWhite, border: `1px solid ${C.borderLight}`,
        borderRadius: R.lg, padding: `${SP.sm}px ${SP.sm + 2}px`,
        marginLeft: 0,
      }}
    >
      <Header chain={chain} />
      {/* Пружина: только когда витки есть (Rounds без rounds → null). */}
      {!noRounds && <Rounds rounds={chain.rounds} max={9} />}
      <SectionLabel />
      {/* Цепочка: работа/доработка + круги. Шаг без витков — одно звено «работа». */}
      <RoundChain chain={chain} run={run} now={now} />
      {noRounds && (
        // Шаг без витков (ревью не было) — подпись под цепочкой.
        <div style={{ fontSize: 12, color: C.textMuted }}>ревью ещё не было</div>
      )}
      <OpenTaskLink chain={chain} run={run} isMobile={isMobile} />
    </div>
  );
}
