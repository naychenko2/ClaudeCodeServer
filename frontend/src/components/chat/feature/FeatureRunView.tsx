import { useEffect, useState } from 'react';
import { C, FONT, R, FS, SP, TB } from '../../../lib/design';
import { useIsMobile } from '../../../lib/breakpoints';
import { Button, ConfirmDialog } from '../../ui';
import { openTaskInSection } from '../../../lib/tasks';
import { openChatById } from '../../../lib/openChat';
import { prefillComposer } from '../../../lib/ai/startChat';
import { api } from '../../../lib/api';
import { getPersonaById, usePersonasVersion, ensurePersonasLoaded } from '../../../lib/personas';
import { showToast } from '../../../lib/toast';
import { PersonaAvatar } from '../../../features/personas/PersonaAvatar';
import type { FeatureRun, FeatureNow, FeatureAttention, FeatureSummary, FeatureTask } from '../../../types/featureRun';
import { PipelineFlow } from './PipelineFlow';
import { FeatureGlyph } from './FeatureGlyphs';
import { Rounds } from './Rounds';
import { dateTimeLabel } from './featureTime';

// Карточка фичи в режиме «Исполнение фичи»: сводка по агрегату `FeatureRun`.
//
// Источник данных — БЭК (REST GET /api/tasks/features): `epic`/`steps`/`reviews`/
// `qa`/`final`/`merge` — задачи трекера, сгруппированные проектором по роли;
// `now`/`attention`/`summary`/`closed`/`stepsAccepted`/`stepsTotal` — готовые поля
// (бэк считает, фронт только показывает). Компонент «глупый»: арифметики конвейера
// здесь нет, только раскладка, действия куратора и состояния.
//
// Состав сверху вниз (зазор 10):
//   1. шапка — имя фичи + ветка (mono) + прогресс;
//   2. плашка-тревога — «ждёт вас» (мерж/явное) или «исполнитель упал», с действиями;
//   3. итог — «Фича смержена» (только у закрытой, из `summary`);
//   4. «Сейчас» — карточка на каждую InProgress-задачу (у закрытой нет);
//   5. «Конвейер» — PipelineFlow (его переделывает Ф5);
//   6. пустое состояние (шагов 0) или легенда (когда шаги есть).

// Подпись секции (как CellLabel): 10 px, 700, uppercase, letter-spacing .06em, C.textMuted.
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, margin: '2px 0 -4px' }}>
      {children}
    </div>
  );
}

// Тик раз в минуту: «идёт N мин» / «ждёт N мин» не застывают, пока открыта карточка.
function useMinuteTick(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 60000);
    return () => clearInterval(t);
  }, []);
  return now;
}

// Минуты от ISO-момента до now (0, если момент в будущем/не задан).
function minutesSince(iso: string | null | undefined, now: number): number | null {
  if (!iso) return null;
  return Math.max(0, Math.floor((now - new Date(iso).getTime()) / 60000));
}

// «идёт 25 мин» / «идёт 2 ч 10 мин»; null — старт не задан.
function elapsedLabel(since: string | null | undefined, now: number): string | null {
  const min = minutesSince(since, now);
  if (min == null) return null;
  if (min < 1) return 'идёт';
  if (min < 60) return `идёт ${min} мин`;
  const h = Math.floor(min / 60);
  const rest = min % 60;
  return rest ? `идёт ${h} ч ${rest} мин` : `идёт ${h} ч`;
}

// Длительность итога: «6 ч 10 мин» / «45 мин».
function durationLabel(min: number): string {
  if (min < 60) return `${min} мин`;
  const h = Math.floor(min / 60);
  const rest = min % 60;
  return rest ? `${h} ч ${rest} мин` : `${h} ч`;
}

// «Ждёт вас …» (плашка wait): 0/нет since → без числа, >0 → «N мин»; без висящего пробела (Майя M9).
function waitPhrase(mins: number | null): string {
  if (mins == null || mins < 1) return 'Ждёт вас.';
  return `Ждёт вас ${mins} мин.`;
}

// «Исполнитель упал …» (плашка stopped): нет since → без числа, 0 → «только что», >0 → «N мин назад» (Майя M9).
function stoppedPhrase(mins: number | null): string {
  if (mins == null) return 'Исполнитель упал.';
  if (mins < 1) return 'Исполнитель упал только что.';
  return `Исполнитель упал ${mins} мин назад.`;
}

// Строка плашки «упал»: «{Имя} · {stepRef} {название} — {reason}» (Майя M8).
// Пустые части не дают «Денис — . Отчёта нет.»: имя и шаг склеиваются « · », reason — « — {…}»,
// только когда он есть; step не найден по att.taskId → его часть опускается.
function stoppedLine(name: string, step: FeatureTask | undefined, reason: string): string {
  const stepPart = step ? `${(step.stepRef ?? '').trim()} ${step.title}`.trim() : '';
  const who = [name, stepPart].filter(Boolean).join(' · ');
  const reasonPart = reason ? ` — ${reason}` : '';
  return (who + reasonPart).trim();
}

// Русские формы: задача/задачи/задач, шаг/шага/шагов.
function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}

// Итог смерженной фичи: «15.10 21:09 · 6 ч 10 мин · 14 задач · 9 шагов · ↺3».
function summaryLine(s: FeatureSummary): string {
  const parts: string[] = [];
  if (s.mergedAt) {
    // mergedAt приходит UTC-ISO; макет v2 считает по МСК (местный пояс) — dateTimeLabel
    // (featureTime) форматирует локальными геттерами, не getUTC*.
    const dt = dateTimeLabel(s.mergedAt);
    if (dt) parts.push(dt);
  }
  parts.push(durationLabel(s.durationMinutes));
  parts.push(`${s.tasks} ${plural(s.tasks, 'задача', 'задачи', 'задач')}`);
  parts.push(`${s.steps} ${plural(s.steps, 'шаг', 'шага', 'шагов')}`);
  if (s.loops > 0) parts.push(`↺${s.loops}`);
  return parts.join(' · ');
}

// Итог закрытой фичи: фон C.successBg, глиф «готово» 18, «Фича смержена» + строка из summary.
function SummaryBlock({ summary }: { summary: FeatureSummary }) {
  return (
    <div style={{ background: C.successBg, borderRadius: R.lg, padding: '8px 10px', display: 'flex', gap: 8, alignItems: 'flex-start' }}>
      <FeatureGlyph kind="done" size={18} />
      <div style={{ minWidth: 0 }}>
        <div style={{ fontSize: FS.base, fontWeight: 700, color: C.successText }}>Фича смержена</div>
        {/* Перенос разрешён (без nowrap/ellipsis): на 360px хвост «↺3» не обрезается (Майя M6). */}
        <div style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'anywhere' }}>
          {summaryLine(summary)}
        </div>
      </div>
    </div>
  );
}

// «Сейчас»: карточка на одну InProgress-задачу. Аватар 22, строка 1 — кто · какой шаг,
// строка 2 — заголовок задачи, справа — «идёт N мин» (тик раз в минуту).
function NowCard({ item }: { item: FeatureNow }) {
  usePersonasVersion(); // реактивность: имя/аватар появятся, когда персона загрузится
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const now = useMinuteTick();
  const persona = item.personaId ? getPersonaById(item.personaId) : undefined;
  const line1 = [persona?.name, item.stepRef].filter(Boolean).join(' · ');
  const elapsed = elapsedLabel(item.startedAt, now);
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: '8px 10px' }}>
      {persona && <PersonaAvatar persona={persona} size={22} />}
      <div style={{ flex: 1, minWidth: 0 }}>
        {/* line1 (имя + stepRef) пуст — строку 1 не рисуем, пустую подпись не оставляем (Глеб). */}
        {line1 && (
          <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{line1}</div>
        )}
        <div style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{item.title}</div>
      </div>
      {elapsed && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>{elapsed}</span>
      )}
    </div>
  );
}

// Плашка-тревога: «ждёт вас» (мерж/явное) или «исполнитель упал» — с действиями.
// Фон/цвет/глиф по тону (wait → info + ромб, stopped → danger + квадрат), R.md, padding 8/10.
function AttentionPlaque({ att, run, isMobile }: { att: FeatureAttention; run: FeatureRun; isMobile: boolean }) {
  const now = useMinuteTick();
  const [confirmRestart, setConfirmRestart] = useState(false);
  const [restarting, setRestarting] = useState(false);
  // M-5: кнопки sm (minHeight 32); на мобиле minHeight 40 (TB.iconHitMobile) — тач-цель.
  const size: 'xs' | 'sm' = isMobile ? 'sm' : 'xs';
  const btnStyle = isMobile ? { minHeight: TB.iconHitMobile } : undefined;
  const mins = minutesSince(att.since, now);

  if (att.kind === 'wait') {
    // Мерж: все шаги приняты, мержа нет — «Отдать фичу Марку на мерж?». Явное: причина ожидания.
    const isMerge = att.reason === 'мерж';
    return (
      <div role="status" style={{ background: C.infoBg, color: C.info, borderRadius: R.md, padding: '8px 10px', display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: 12.5 }}>
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
          <span style={{ marginTop: 2, flexShrink: 0 }}><FeatureGlyph kind="wait" size={14} /></span>
          <span>
            <b>{waitPhrase(mins)}</b>{' '}
            {isMerge ? 'Все шаги приняты, мержа ещё нет. Отдать фичу Марку на мерж?' : att.reason}
          </span>
        </div>
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
          {isMerge ? (
            <>
              {/* «Попросить Марка» — затравка в композере оркестратора; саму задачу Марку не создаёт. */}
              <Button size={size} variant="secondary" style={btnStyle} onClick={() => prefillComposer(mergePrefill(run))}>
                Попросить Марка
              </Button>
              {/* У мержа att.taskId — это финал: «Итог финала» открывает его; без taskId кнопки нет (Майя M4). */}
              {att.taskId && (
                <Button size={size} variant="ghost" style={btnStyle} onClick={() => { if (att.taskId) openTaskInSection({ id: att.taskId }); }}>
                  Итог финала
                </Button>
              )}
            </>
          ) : (
            // Явное ожидание (reason ≠ «мерж»): одна кнопка «Открыть задачу»; без taskId кнопок нет (Майя M4).
            att.taskId && (
              <Button size={size} variant="secondary" style={btnStyle} onClick={() => { if (att.taskId) openTaskInSection({ id: att.taskId }); }}>
                Открыть задачу
              </Button>
            )
          )}
        </div>
      </div>
    );
  }

  // stopped: исполнитель упал — «Перезапустить» (ConfirmDialog → api.tasks.execute) +
  // «Чат исполнителя» (ghost → openChatById); без linkedSessionId вторая кнопка не рендерится.
  const persona = att.personaId ? getPersonaById(att.personaId) : undefined;
  const name = persona?.name ?? '';
  const reason = (att.reason ?? '').trim();
  // Шаг упавшей задачи: att.taskId → run.steps → stepRef + title; не найден → его часть опускается (Майя M8).
  const step = att.taskId ? (run.steps ?? []).find(t => t.id === att.taskId) : undefined;
  return (
    <div role="status" style={{ background: C.dangerBg, color: C.dangerText, borderRadius: R.md, padding: '8px 10px', display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: 12.5 }}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
        <span style={{ marginTop: 2, flexShrink: 0 }}><FeatureGlyph kind="stopped" size={14} /></span>
        <span>
          <b>{stoppedPhrase(mins)}</b>{' '}
          {stoppedLine(name, step, reason)}
        </span>
      </div>
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
        <Button size={size} variant="secondary" style={btnStyle} loading={restarting} onClick={() => setConfirmRestart(true)}>
          Перезапустить
        </Button>
        {att.linkedSessionId && (
          <Button size={size} variant="ghost" style={btnStyle} onClick={() => { if (att.linkedSessionId) void openChatById(att.linkedSessionId); }}>
            Чат исполнителя
          </Button>
        )}
      </div>
      {confirmRestart && (
        <ConfirmDialog
          title="Перезапустить исполнителя?"
          subtitle="Задача уйдёт на исполнение заново."
          confirmLabel="Перезапустить"
          confirmVariant="secondary"
          onConfirm={async () => {
            if (!att.taskId) { setConfirmRestart(false); return; }
            setRestarting(true);
            try {
              await api.tasks.execute(att.taskId);
            } catch {
              // Тихий сбой: показать штатный тост, а не глотать (Майя M10, Глеб).
              showToast('Не удалось перезапустить исполнителя', 'Попробуйте ещё раз.', 'error');
            }
            setRestarting(false);
            setConfirmRestart(false);
          }}
          onCancel={() => setConfirmRestart(false)}
        />
      )}
    </div>
  );
}

// Затравка для «Попросить Марка»: мерж фичи — задача Марку (оркестратор сам её создаст).
function mergePrefill(run: FeatureRun): string {
  const branch = run.epic.worktreeBranch ? ` (ветка ${run.epic.worktreeBranch})` : '';
  return `Сделай мерж фичи «${run.epic.title}»${branch}. Все шаги приняты, ревью пройдено — отдавай в master.`;
}

// Пустое состояние (шагов 0): «План появится, когда {исполнитель разреза} закончит разрез».
function EmptyState({ run }: { run: FeatureRun }) {
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const cutPersona = run.cut?.[0]?.personaId ? getPersonaById(run.cut[0].personaId) : undefined;
  const who = cutPersona?.name;
  return (
    <div style={{ background: C.bgCard, border: `1px dashed ${C.dashed}`, borderRadius: R.lg, padding: '8px 10px', fontSize: 12.5, color: C.textSecondary }}>
      Шагов пока нет. План появится, когда {who ? `${who} закончит разрез` : 'закончится разрез'} — тогда здесь будут шаги по линиям.
    </div>
  );
}

// Легенда (когда шаги есть): кольца исходов витков (Rounds без подписи) + глиф «нет в трекере».
function Legend() {
  const sample = (verdict: 'returned' | 'accepted_with_remarks' | 'accepted') => (
    <Rounds caption={false} rounds={[{ n: 1, verdict }]} />
  );
  const item = (node: React.ReactNode, label: string) => (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
      {node}
      <span>{label}</span>
    </span>
  );
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: '4px 12px', flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
      {item(sample('returned'), 'вернул')}
      {item(sample('accepted_with_remarks'), 'принял с правками')}
      {item(sample('accepted'), 'принял')}
      {item(<FeatureGlyph kind="absent" size={14} />, 'нет в трекере')}
    </div>
  );
}

// Главная карточка фичи.
export function FeatureRunView({ run }: { run: FeatureRun }) {
  const isMobile = useIsMobile();
  const hasSteps = (run.steps?.length ?? 0) > 0;

  return (
    // M-2: собственный padding убран — поля задаёт поверхность (меню: headerPad/bodyPad);
    // остаётся только вертикальный зазор 10 между блоками.
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {/* Шапка: имя + ветка (M-3: счётчик acc/tot · ↺N — в FeatureModeBody, % из шапки убран) */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>{run.epic.title}</span>
        {run.epic.worktreeBranch && (
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{run.epic.worktreeBranch}</span>
        )}
      </div>

      {/* Плашка-тревога: ждёт вас / исполнитель упал (с действиями) */}
      {run.attention && <AttentionPlaque att={run.attention} run={run} isMobile={isMobile} />}

      {/* Итог смерженной фичи (только у закрытой) */}
      {run.closed && run.summary && <SummaryBlock summary={run.summary} />}

      {/* «Сейчас»: по карточке на каждую InProgress-задачу; у закрытой фичи нет */}
      {run.now && run.now.length > 0 && !run.closed && (
        <>
          <SectionLabel>Сейчас</SectionLabel>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            {run.now.map(n => <NowCard key={n.taskId} item={n} />)}
          </div>
        </>
      )}

      {/* Конвейер: вертикальная диаграмма (PipelineFlow; его переделывает Ф5) */}
      <SectionLabel>Конвейер</SectionLabel>
      <PipelineFlow run={run} />

      {/* Пустое состояние (шагов 0) или легенда (когда шаги есть) */}
      {hasSteps ? <Legend /> : <EmptyState run={run} />}
    </div>
  );
}
