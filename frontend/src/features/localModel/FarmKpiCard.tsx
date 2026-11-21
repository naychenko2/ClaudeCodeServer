// «Сейчас на ферме» — объединённая карточка четырёх KPI (макет
// docs/mockups/local-model-llm-redesign-v1.html, строки 169–176 и 224; спецификация
// docs/mockups/local-model-llm-redesign-proposal.md, §v1, строки 121–127).
// Чистый компонент: без опросов и состояния — данные приходят пропом `data`.
// В сетке 2×2 ровно четыре KPI: сколько запросов идёт, сколько ждёт, скорость
// генерации «сейчас» и сквозная скорость ответа за 5 минут.
//
// Числа — FONT.mono FS.xl 700 C.textHeading (тот же облик, что у `Kpi` в TextTab);
// подписи — FS.xs C.textSecondary (макет .kpi .l). Отсутствующее, null или неfinite
// значение — «нет данных» только в своей ячейке: ноль — это измеренное значение,
// а здесь измерять было нечего; остальные KPI при этом не скрываются. При
// status === 'yielded' метрик живых нет вовсе — все четыре ячейки «нет данных»,
// включая нули, которые несёт снимок выгруженной модели.
// Очередь > 0 — окрашивается только число (C.warningText), подпись остаётся обычной.
// Скорости форматирует существующий formatTps; своих формул здесь нет.

import { type CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import type { LocalModelStats } from './api';
import { formatTps } from './format';

const NO_DATA = 'нет данных';

// Оболочка та же, что у карточек вкладки (TextTab.tsx: Card): C.bgWhite,
// C.borderLight, R.xl, SP.md; заголовок 11 / 700 / uppercase / C.textSecondary.
const CARD: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
};
const HEAD: CSSProperties = {
  margin: 0, marginBottom: SP.sm, fontSize: 11, fontWeight: 700, textTransform: 'uppercase',
  letterSpacing: '0.06em', color: C.textSecondary,
};
// Сетка 2×2 (макет .kpis: row-gap 12, column-gap 8)
const GRID: CSSProperties = { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: `${SP.md}px ${SP.sm}px` };
const LABEL: CSSProperties = { fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs };

// Значение с подписью: есть число — моно FS.xl 700 (warn — C.warningText), нет —
// спокойная «нет данных» (не «—»: здесь человек должен прочесть, что данных нет).
function KpiCell({ value, label, warn }: { value: string | null; label: string; warn?: boolean }) {
  const has = value != null;
  return (
    <div>
      <div style={has
        ? { fontFamily: FONT.mono, fontSize: FS.xl, fontWeight: 700, lineHeight: 1.2, color: warn ? C.warningText : C.textHeading }
        : { fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>
        {has ? value : NO_DATA}
      </div>
      <div style={LABEL}>{label}</div>
    </div>
  );
}

// «Есть число» — то же условие, что у `asCell` в TextTab: null и неfinite (NaN,
// Infinity — выброс поллера) не считаются значением и не превращаются в «0 ток/с».
function num(v: number | null | undefined): string | null {
  return v != null && Number.isFinite(v) ? String(v) : null;
}

// Окна скорости ответа берутся ПО КЛЮЧУ '5m' (их порядок в ответе не значим —
// types.ts: «окна — строки '5m' и '1h', их порядок в ответе не значим»).
function tps(v: number | null | undefined): string | null {
  return v != null && Number.isFinite(v) ? formatTps(v) : null;
}

export function FarmKpiCard({ data }: { data: LocalModelStats }) {
  // Выгруженная модель: метрик живых нет — ни очередей, ни скоростей (спека §«Состояния
  // всех вариантов», Yielded: «KPI … пишут отсутствие данных, а не нули»).
  const yielded = data.status === 'yielded';
  const w5 = data.answerSpeed?.windows.find(w => w.window === '5m');
  return (
    <section style={CARD}>
      <h3 style={HEAD}>Сейчас на ферме</h3>
      <div style={GRID}>
        <KpiCell value={yielded ? null : num(data.running)} label="запросов идёт" />
        <KpiCell value={yielded ? null : num(data.waiting)} label="ждут в очереди" warn={!yielded && (data.waiting ?? 0) > 0} />
        <KpiCell value={yielded ? null : tps(data.speed?.genPerStream)} label="пока пишет · 1 мин" />
        <KpiCell value={yielded ? null : tps(w5?.endToEndTps)} label="скорость ответа · 5 мин" />
      </div>
    </section>
  );
}
