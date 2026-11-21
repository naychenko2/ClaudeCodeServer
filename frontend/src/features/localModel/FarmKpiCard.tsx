// «Сейчас на ферме» — объединённая карточка четырёх KPI (макет
// docs/mockups/local-model-llm-redesign-v1.html, строки 169–176 и 224; спецификация
// docs/mockups/local-model-llm-redesign-proposal.md, §v1, строки 121–127).
// Чистый компонент: без опросов и состояния — данные приходят пропом `data`.
// В сетке 2×2 ровно четыре KPI: сколько запросов идёт, сколько ждёт, скорость
// генерации «сейчас» и сквозная скорость ответа за 5 минут.
//
// Числа — FONT.mono FS.xl 700 C.textHeading (тот же облик, что у `Kpi` в TextTab);
// единицы скорости (« ток/с») — отдельным span мелким шрифтом (макет .kpi .n small,
// строка 77: C.textMuted 11px 400); подписи — FS.xs C.textSecondary (макет .kpi .l).
// Отсутствующее, null или неfinite значение — «нет данных» только в своей ячейке:
// ноль — это измеренное значение,
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
// Правая подпись заголовка «локально» — только на десктопе (макет строка 169;
// в мобильной версии, строка 224, подписи нет): прижимается к краю margin-left:auto,
// цвет — приглушённый C.textMuted (макет .right, строка 74; та же конвенция мелких
// приписок, что в TextTab.tsx:642), размер FS.xs, вес 400 и textTransform none —
// чтобы не наследовал caps и жирность заголовка.
const CARD: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
};
const HEAD: CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.sm,
  margin: 0, marginBottom: SP.sm, fontSize: 11, fontWeight: 700, textTransform: 'uppercase',
  letterSpacing: '0.06em', color: C.textSecondary,
};
const HEAD_RIGHT: CSSProperties = {
  marginLeft: 'auto', fontSize: FS.xs, color: C.textMuted, fontWeight: 400, textTransform: 'none',
};
// Сетка 2×2 (макет .kpis: row-gap 12, column-gap 8)
const GRID: CSSProperties = { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: `${SP.md}px ${SP.sm}px` };
const LABEL: CSSProperties = { fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs };
// Единицы скорости — мелким шрифтом по макету (.kpi .n small, строка 77): приглушённый
// C.textMuted, FS.xs, вес 400; само число остаётся моно FS.xl 700.
const UNIT: CSSProperties = { fontSize: FS.xs, fontWeight: 400, color: C.textMuted };

// Значение с подписью: есть число — моно FS.xl 700 (warn — C.warningText), единицы
// (если есть) — отдельным span по UNIT; нет — спокойная «нет данных» (не «—»: здесь
// человек должен прочесть, что данных нет).
function KpiCell({ value, unit, label, warn }: { value: string | null; unit?: string; label: string; warn?: boolean }) {
  const has = value != null;
  return (
    <div>
      <div style={has
        ? { fontFamily: FONT.mono, fontSize: FS.xl, fontWeight: 700, lineHeight: 1.2, color: warn ? C.warningText : C.textHeading }
        : { fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>
        {has ? <>{value}{unit ? <span style={UNIT}>{unit}</span> : null}</> : NO_DATA}
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
// Формат скорости остаётся у formatTps («38 ток/с», format.ts:25); здесь он только
// разрывается на число и единицы — по макету единицы печатаются мельче числа.
function tps(v: number | null | undefined): { num: string; unit: string } | null {
  if (v == null || !Number.isFinite(v)) return null;
  const s = formatTps(v);
  const i = s.lastIndexOf(' ');
  return i <= 0 ? { num: s, unit: '' } : { num: s.slice(0, i), unit: s.slice(i) };
}

// isMobile — опционален с дефолтом false: вызывающий код в TextTab (Ф-3, параллельная
// задача) уже есть без него; на мобиле вызывающий обязан передать isMobile={isMobile},
// иначе десктопная подпись окажется в листе.
export function FarmKpiCard({ data, isMobile = false }: { data: LocalModelStats; isMobile?: boolean }) {
  // Выгруженная модель: метрик живых нет — ни очередей, ни скоростей (спека §«Состояния
  // всех вариантов», Yielded: «KPI … пишут отсутствие данных, а не нули»).
  const yielded = data.status === 'yielded';
  const w5 = data.answerSpeed?.windows.find(w => w.window === '5m');
  const gen = yielded ? null : tps(data.speed?.genPerStream);
  const answer5m = yielded ? null : tps(w5?.endToEndTps);
  return (
    <section style={CARD}>
      <h3 style={HEAD}>Сейчас на ферме{!isMobile && <span style={HEAD_RIGHT}>локально</span>}</h3>
      <div style={GRID}>
        <KpiCell value={yielded ? null : num(data.running)} label="запросов идёт" />
        <KpiCell value={yielded ? null : num(data.waiting)} label="ждут в очереди" warn={!yielded && (data.waiting ?? 0) > 0} />
        <KpiCell value={gen?.num ?? null} unit={gen?.unit} label="пока пишет · 1 мин" />
        <KpiCell value={answer5m?.num ?? null} unit={answer5m?.unit} label="скорость ответа · 5 мин" />
      </div>
    </section>
  );
}
