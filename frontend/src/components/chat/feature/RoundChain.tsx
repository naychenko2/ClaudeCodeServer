import { C, FONT, R } from '../../../lib/design';
import { getPersonaById, usePersonasVersion } from '../../../lib/personas';
import { PersonaAvatar } from '../../../features/personas/PersonaAvatar';
import { FeatureGlyph } from './FeatureGlyphs';
import type { FeatureRun, FeatureStepChain, FeatureRound, FeatureTask } from '../../../types/featureRun';

// Цепочка кругов шага (макет v2 .tl2): вертикальная рельса, по которой в порядке
// времени идут звенья — работа/доработка исполнителя и круги ревью/проверки/финала.
//
// Звено исполнителя: глиф «сделал» (точка), аватар 18, безличная форма («работа»,
// «доработка», «дефект заведён», «дефект исправлен»), время (mono) и заголовок задачи.
// Звено круга: глиф по вердикту (готово/петля/пульс), «Круг N» + бусина вердикта +
// «Имя · ЧЧ:ММ–ЧЧ:ММ», причина второй строкой.
//
// Компонент «глупый»: порядок и данные — из бэка (chain.taskIds + chain.rounds),
// фронт только раскладывает. Задачи резолвятся по id из run.steps (FeatureTask[]).

// Звено цепочки: работа/доработка (task) или круг (round).
type ChainLink =
  | { kind: 'work'; task: FeatureTask }
  | { kind: 'round'; round: FeatureRound };

// Задача по id из run.steps (FeatureTask[]); не найдена — undefined (без неё не падаем).
function resolveTask(run: FeatureRun, id: string): FeatureTask | undefined {
  return (run.steps ?? []).find(t => t.id === id);
}

// Звенья в порядке времени: первая работа (шаг) всегда первая; каждый круг — за ним
// следующая работа (доработка), если она есть. Остаток работ (больше доработок, чем
// кругов) дописывается в конец. Так «D1c → Круг 1 · вернул → доработка → Круг 2 · принял».
function buildLinks(chain: FeatureStepChain, run: FeatureRun): ChainLink[] {
  const work = chain.taskIds.map(id => resolveTask(run, id));
  const links: ChainLink[] = [];
  let wi = 0;
  // Первая работа (шаг) — всегда первой.
  const first = work[0];
  if (first) { links.push({ kind: 'work', task: first }); wi = 1; }
  for (const round of chain.rounds) {
    links.push({ kind: 'round', round });
    if (wi < work.length) {
      const t = work[wi];
      if (t) { links.push({ kind: 'work', task: t }); wi++; }
    }
  }
  // Остаток работ (больше доработок, чем кругов) — дописывается в конец.
  while (wi < work.length) {
    const t = work[wi];
    if (t) links.push({ kind: 'work', task: t });
    wi++;
  }
  return links;
}

// === Время (mono 11, C.textMuted) ===

// ISO → «ЧЧ:ММ» (UTC, как в строке с Z); невалидная/пустая → null.
function hhmm(iso?: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}`;
}

// Длительность «N мин» / «1 ч 10 мин»; min < 0 → 0.
function minutesLabel(min: number): string {
  const m = Math.max(0, Math.round(min));
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const rest = m % 60;
  return rest ? `${h} ч ${rest} мин` : `${h} ч`;
}

// Старт задачи: ClaudeStartedAt (запуск исполнителя), иначе CreatedAt.
function taskStart(t: FeatureTask): string | null {
  return t.claudeStartedAt ?? t.createdAt ?? null;
}

// Строка времени звена исполнителя: «07:49–08:09 · 20 мин»; идёт (нет completedAt) —
// «идёт N мин»; без старта — null (время не рисуем, висящих разделителей не оставляем).
function workTime(t: FeatureTask, now: number): string | null {
  const start = taskStart(t);
  if (!start) return null;
  const s = new Date(start);
  if (Number.isNaN(s.getTime())) return null;
  const end = t.completedAt ? new Date(t.completedAt) : null;
  const validEnd = end && !Number.isNaN(end.getTime()) ? end : null;
  if (validEnd) {
    const dur = (validEnd.getTime() - s.getTime()) / 60000;
    return `${hhmm(start)}–${hhmm(t.completedAt!)} · ${minutesLabel(dur)}`;
  }
  // Идёт: от старта до now.
  return `идёт ${minutesLabel((now - s.getTime()) / 60000)}`;
}

// Строка времени круга: «08:36–08:45»; идёт (нет completedAt) — «идёт»; без старта — null.
function roundTime(r: FeatureRound, now: number): string | null {
  const start = hhmm(r.startedAt);
  if (!start) return null;
  const end = hhmm(r.completedAt);
  if (end) return `${start}–${end}`;
  if (r.verdict === 'running') {
    const s = new Date(r.startedAt!);
    return Number.isNaN(s.getTime()) ? null : `идёт ${minutesLabel((now - s.getTime()) / 60000)}`;
  }
  return start;
}

// === Безличные формы звена исполнителя ===
// Имени персоны недостаточно, чтобы угадать род глагола, а список имён в код зашивать
// нельзя — поэтому безличные формы: «работа» (шаг), «доработка» (rework),
// «дефект заведён» (defect-шаг), «дефект исправлен» (defect-доработка).
function workVerb(t: FeatureTask): string {
  if (t.kind === 'defect') return t.role === 'rework' ? 'дефект исправлен' : 'дефект заведён';
  return t.role === 'rework' ? 'доработка' : 'работа';
}

// === Глифы ===

// Глиф «сделал»: точка r 4 C.textMuted; идёт — пульс (.feature-pulse).
function WorkGlyph({ running }: { running: boolean }) {
  return (
    <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true" style={{ display: 'block' }}>
      <circle cx="7" cy="7" r="4" fill={C.textMuted} className={running ? 'feature-pulse' : undefined} />
    </svg>
  );
}

// Глиф круга 13 по вердикту: accepted — «готово» (C.success); returned/accepted_with_remarks
// — петля (C.warning); running — пульс; withdrawn — приглушённая точка.
function RoundGlyph({ verdict }: { verdict: FeatureRound['verdict'] }) {
  if (verdict === 'accepted') return <FeatureGlyph kind="done" size={13} />;
  if (verdict === 'returned' || verdict === 'accepted_with_remarks') return <FeatureGlyph kind="loop" size={13} />;
  if (verdict === 'running') {
    // Пульс: кольцо C.textHeading (viewBox 14, рендер 13 — как FeatureGlyph).
    return (
      <svg width="13" height="13" viewBox="0 0 14 14" aria-hidden="true" style={{ display: 'block' }}>
        <circle cx="7" cy="7" r="4.5" fill="none" stroke={C.textHeading} strokeWidth="1.6" className="feature-pulse" />
      </svg>
    );
  }
  // withdrawn — приглушённая точка (не «идёт», не «принят»).
  return (
    <svg width="13" height="13" viewBox="0 0 14 14" aria-hidden="true" style={{ display: 'block' }}>
      <circle cx="7" cy="7" r="3.5" fill={C.textMuted} />
    </svg>
  );
}

// === Бусина вердикта ===
// R.max (пилюля, не R.full), 11/700, line-height 18. Цвета по вердикту (макет v2 .bead):
// returned — warningBg/warningText; accepted_with_remarks — bgCard + пунктирная рамка C.warning;
// accepted — successBg/successText; running — bgSelected + пунктир C.dashed;
// withdrawn — bgSelected + C.textMuted.
const BEAD: Record<FeatureRound['verdict'], { bg: string; fg: string; border?: string; text: string }> = {
  returned:              { bg: C.warningBg, fg: C.warningText, text: 'вернул' },
  accepted_with_remarks: { bg: C.bgCard, fg: C.warningText, border: C.warning, text: 'принял с правками' },
  accepted:              { bg: C.successBg, fg: C.successText, text: 'принял' },
  running:               { bg: C.bgSelected, fg: C.textHeading, border: C.dashed, text: 'идёт' },
  withdrawn:             { bg: C.bgSelected, fg: C.textMuted, text: 'снят' },
};

function VerdictBead({ verdict }: { verdict: FeatureRound['verdict'] }) {
  const b = BEAD[verdict] ?? BEAD.running;
  return (
    <span
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 3,
        fontSize: 11, fontWeight: 700, borderRadius: R.max,
        padding: '0 7px', lineHeight: '18px', whiteSpace: 'nowrap',
        background: b.bg, color: b.fg,
        border: b.border ? `1px dashed ${b.border}` : '1px solid transparent',
      }}
    >
      {b.text}
    </span>
  );
}

// === Звенья ===

// Звено исполнителя: глиф «сделал» + аватар 18 + безличная форма + время (mono) +
// заголовок задачи (12, C.textSecondary, overflow-wrap anywhere).
function WorkLink({ t, now, isLast }: { t: FeatureTask; now: number; isLast: boolean }) {
  usePersonasVersion(); // реактивность: аватар/имя появятся, когда персона загрузится
  const persona = t.personaId ? getPersonaById(t.personaId) : undefined;
  const running = t.status === 'inProgress' && !t.completedAt;
  const time = workTime(t, now);
  return (
    <li
      style={{
        position: 'relative', display: 'grid', gridTemplateColumns: '14px 1fr', gap: 7,
        padding: '3px 0 5px', fontSize: 12, color: C.textPrimary,
      }}
    >
      {!isLast && <Rail />}
      <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1, marginTop: 2 }}>
        <WorkGlyph running={running} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 6px', alignItems: 'center' }}>
          {persona && <PersonaAvatar persona={persona} size={18} />}
          <span style={{ fontWeight: 600, color: C.textHeading }}>{workVerb(t)}</span>
          {time && <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap' }}>{time}</span>}
        </div>
        {t.title && (
          <div style={{ color: C.textSecondary, marginTop: 1, overflowWrap: 'anywhere' }}>{t.title}</div>
        )}
      </div>
    </li>
  );
}

// Звено круга: глиф по вердикту + «Круг N» (600) + бусина вердикта + «Имя · ЧЧ:ММ–ЧЧ:ММ»;
// имя — обычным шрифтом, время — mono 11 (Майя: моно только у времени, не всей строки);
// причина (reason) — второй строкой.
function RoundLink({ r, now, isLast, isBack }: { r: FeatureRound; now: number; isLast: boolean; isBack: boolean }) {
  usePersonasVersion();
  const persona = r.personaId ? getPersonaById(r.personaId) : undefined;
  const name = persona?.name;
  const time = roundTime(r, now);
  return (
    <li
      style={{
        position: 'relative', display: 'grid', gridTemplateColumns: '14px 1fr', gap: 7,
        padding: '3px 0 5px', fontSize: 12, color: C.textPrimary,
      }}
    >
      {!isLast && <Rail back={isBack} />}
      <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1, marginTop: 2 }}>
        <RoundGlyph verdict={r.verdict} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 6px', alignItems: 'center' }}>
          <span style={{ fontWeight: 700, color: C.textHeading }}>Круг {r.n}</span>
          <VerdictBead verdict={r.verdict} />
          {name && (
            <span style={{ color: C.textMuted }}>{name}{time ? ' · ' : ''}</span>
          )}
          {time && <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap' }}>{time}</span>}
        </div>
        {r.reason && (
          <div style={{ color: C.textSecondary, marginTop: 1, overflowWrap: 'anywhere' }}>{r.reason}</div>
        )}
      </div>
    </li>
  );
}

// Рельса цепочки: 2 px C.progressTrack, left 6 px; у звена после возврата — C.warning, opacity .55.
// Абсолютная: top — от центра глифа до низа звена, bottom — до верха следующего (рельса непрерывна).
function Rail({ back = false }: { back?: boolean }) {
  return (
    <span
      aria-hidden="true"
      style={{
        position: 'absolute', left: 6, top: 17, bottom: -3, width: 2,
        background: back ? C.warning : C.progressTrack,
        opacity: back ? 0.55 : 1, borderRadius: 2,
      }}
    />
  );
}

// === Цепочка ===
// `now` — «сейчас» (тик из StepDetails) для длительностей идущих звеньев.
export function RoundChain({ chain, run, now }: { chain: FeatureStepChain; run: FeatureRun; now: number }) {
  const links = buildLinks(chain, run);
  if (links.length === 0) return null;
  return (
    <ul style={{ listStyle: 'none', margin: 0, padding: '0 0 0 2px', display: 'flex', flexDirection: 'column' }}>
      {links.map((link, i) => {
        const isLast = i === links.length - 1;
        // Рельса звена-возврата (returned/accepted_with_remarks) — warning (макет v2 .tl2 li.back):
        // она соединяет круг с доработкой, то есть «путь возврата».
        const isBack = link.kind === 'round'
          && (link.round.verdict === 'returned' || link.round.verdict === 'accepted_with_remarks');
        if (link.kind === 'work') {
          return <WorkLink key={`w${i}`} t={link.task} now={now} isLast={isLast} />;
        }
        return <RoundLink key={`r${i}`} r={link.round} now={now} isLast={isLast} isBack={isBack} />;
      })}
    </ul>
  );
}
