import { useState } from 'react';
import { C, FONT, R } from '../../../lib/design';
import { useIsMobile } from '../../../lib/breakpoints';
import { Button, ConfirmDialog } from '../../ui';
import type { FeatureRun } from '../../../types/featureRun';
import { PipelineFlow } from './PipelineFlow';

// Карточка фичи в режиме «Исполнение фичи»: сводка по агрегату `FeatureRun`.
//
// Источник данных — БЭК (REST GET /api/tasks/features): `epic`/`steps`/`reviews`/
// `qa`/`final`/`merge` — задачи трекера, сгруппированные проектором по роли;
// `verdicts`/`progress`/`ready` вычисляет бэк, фронт только показывает. Компонент
// «глупый»: арифметики конвейера здесь нет, только раскладка и действия куратора.
//
// Состав сверху вниз:
//   1. шапка — имя фичи + ветка + прогресс (доля решённых витков);
//   2. статус — «финал зелёный» / «ждёт вас» / «смержена»;
//   3. «Ревью не было» — тонкая строка, если ревью в трекере нет;
//   4. действия куратора — «Принять» (финал) + «Изменить ход» (модал);
//   5. конвейер — одна вертикальная диаграмма (стадии + линии бэк/фронт + дуги возврата).

// Алерт состояния: тонированная плашка.
function StatusAlert({ tone, children }: { tone: 'wait' | 'done'; children: React.ReactNode }) {
  const bg = tone === 'wait' ? C.infoBg : C.successBg;
  const fg = tone === 'wait' ? C.info : C.successText;
  return (
    <div role="status" style={{ display: 'flex', flexDirection: 'column', gap: 8, background: bg, color: fg, borderRadius: R.lg, padding: '8px 10px', fontSize: 12.5 }}>
      {children}
    </div>
  );
}

// Пока REST-эндпоинта accept (этап 2.6) нет — «Принять» не фиксирует эпик, а показывает
// заглушку: кнопка secondary + «Доступно с релизом 2.6». Поднять в true, когда появится
// POST /api/tasks/{finalId}/accept.
const ACCEPT_READY = false;

// Действия куратора: «Принять» (финал) + «Изменить ход» (модал-заглушка).
// «Принять» активно, когда финал закрыт (run.ready); «Изменить ход» — заглушка
// «Скоро» (реальные поля — в этапе 2.6). Диалог показывает последствия.
function CuratorActions({ run }: { run: FeatureRun }) {
  const [confirmAccept, setConfirmAccept] = useState(false);
  const [changePlan, setChangePlan] = useState(false);
  const [accepted, setAccepted] = useState(false);

  // run.ready — финал закрыт (кнопка активна). ACCEPT_READY — есть ли реальный эндпоинт
  // (этап 2.6): пока его нет, «Принять» — заглушка, не фиксация эпика.
  const ready = run.ready;

  // Тултип: финал не закрыт → «когда финал закрыт»; закрыт, но эндпоинта нет → «с релизом
  // 2.6»; всё готово → «Принять фичу».
  const acceptTitle = !ready
    ? 'Доступно, когда финал закрыт'
    : ACCEPT_READY
      ? 'Принять фичу'
      : 'Доступно с релизом 2.6';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
        <Button
          size="xs"
          variant={ready ? 'secondary' : 'ghost'}
          disabled={!ready}
          onClick={() => setConfirmAccept(true)}
          title={acceptTitle}
        >
          Принять
        </Button>
        <Button size="xs" variant="ghost" onClick={() => setChangePlan(true)} title="Изменить ход фичи">
          Изменить ход
        </Button>
      </div>
      {/* Текст под кнопкой: что будет с эпиком (и когда «Принять» станет реальной фиксацией) */}
      <div style={{ fontSize: 11, color: C.textMuted }}>
        {!ready
          ? '«Принять» станет доступно, когда финал закрыт.'
          : ACCEPT_READY
            ? 'После принятия эпик перейдёт в closed.'
            : 'Доступно с релизом 2.6 — пока не закроет эпик.'}
      </div>
      {accepted && (
        <div style={{ fontSize: 12, color: C.successText }}>Принято. Эпик закрыт.</div>
      )}

      {confirmAccept && (
        <ConfirmDialog
          title="Принять фичу?"
          subtitle={ACCEPT_READY
            ? 'После этого эпик перейдёт в closed.'
            : 'Доступно с релизом 2.6 — пока не закроет эпик.'}
          confirmLabel="Принять"
          confirmVariant={ACCEPT_READY ? 'primary' : 'secondary'}
          onConfirm={() => { setAccepted(true); setConfirmAccept(false); }}
          onCancel={() => setConfirmAccept(false)}
        />
      )}
      {changePlan && (
        <ConfirmDialog
          title="Изменить ход"
          subtitle="Смена приоритета, остановка, переразрезание — скоро (этап 2.6)."
          confirmLabel="Скоро"
          onConfirm={() => setChangePlan(false)}
          onCancel={() => setChangePlan(false)}
        />
      )}
    </div>
  );
}

// Главная карточка фичи.
export function FeatureRunView({ run }: { run: FeatureRun }) {
  const isMobile = useIsMobile();
  const pad = isMobile ? 8 : 10;

  // Прогресс: доля решённых витков (0..1) → %.
  const pct = Math.round(run.progress * 100);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, padding: pad }}>
      {/* Шапка: имя + ветка + прогресс */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading }}>{run.epic.title}</span>
        {run.epic.worktreeBranch && (
          <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>{run.epic.worktreeBranch}</span>
        )}
        <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap' }}>
          {pct}%
        </span>
      </div>

      {/* Статус: финал зелёный / ждёт вас / смержена */}
      {run.ready && (
        <StatusAlert tone="done">
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
            <span style={{ marginTop: 2 }}>
              <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
                <circle cx="8" cy="8" r="7" fill={C.success} />
                <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.onAccent} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
            <span><b>Финал зелёный.</b> Готово к принятию.</span>
          </div>
        </StatusAlert>
      )}
      {run.awaitingHumanSince && (
        <StatusAlert tone="wait">
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
            <span style={{ marginTop: 2 }}>
              <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.5 14.5 8 8 14.5 1.5 8Z" fill={C.info} /></svg>
            </span>
            <span><b>Ждёт вас.</b> {run.awaitingHumanReason ?? 'Нужно ваше решение.'}</span>
          </div>
        </StatusAlert>
      )}

      {/* «Ревью не было» — тонкая строка, если ревью в трекере нет (M3) */}
      {run.reviews.length === 0 && (
        <div style={{ fontSize: 11, color: C.textMuted, fontStyle: 'italic' }}>Ревью не было</div>
      )}

      {/* Действия куратора (M3) */}
      <CuratorActions run={run} />

      {/* Конвейер: вертикальная диаграмма — шкала стадий + линии бэк/фронт (толщина =
          проходы) + дуга возврата (ревью/проверка → производство) */}
      <SectionLabel>Конвейер</SectionLabel>
      <PipelineFlow run={run} />
    </div>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, margin: '2px 0 -4px' }}>
      {children}
    </div>
  );
}
