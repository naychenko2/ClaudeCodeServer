import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../../lib/design';
import { roleLabel } from '../../../lib/roleLabel';
import { STAGE_NAME } from '../../../lib/chatGroups';
import { NavTargetButton } from '../../feature/NavTargetButton';
import { openChatById } from '../../../lib/openChat';
import { openTaskInSection } from '../../../lib/tasks';
import { Modal } from '../../ui';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import type { FeatureRun, FeatureStage, FeatureStageExecutor } from '../../../types/featureRun';

// Шторка фичи (волна 3, Ф-pl-b; макет feature-banner-1, «А · Строка» + раскрытие из «Г»):
// тап по строке панели раскрывает РАЗВЁРНУТЫЙ СОСТАВ фичи. Состав (в порядке макета):
//   • переключатель фич — если у чата несколько фич, в порядке Б-ord (первая — фича
//     задачи этого чата; порядок даёт бэк, фронт не пересортировывает);
//   • список 7 этапов со статусом и временем (границы + «≈ N мин»);
//   • исполнители ТЕКУЩЕГО (active) этапа: roleLabel, чип модели, NavTargetButton (чат · задача).
//
// Данные — ТОЛЬКО из списка `features?sessionId=` (runs, как в попапе), без догрузки
// `features/{id}`. Закрытие: Esc, тап вне, кнопка; фокус возвращается на строку.
//
// Подача: на мобиле (<768) — bottom-sheet (Modal, штатный паттерн продукта); на
// десктопе — раскрывающаяся панель под строкой (role=dialog, Esc, тап по подложке).
// Доступность: aria-expanded на строке (задаёт плашка), role=dialog + aria-modal,
// focus-trap на мобиле (Modal) и возврат фокуса на строку при закрытии.

// Подпись состояния этапа (единая с вкладкой «Хронология»): done/active/pending/absent.
function stageStatusText(s: FeatureStage): string {
  switch (s.state) {
    case 'done': return 'пройдено';
    case 'active':
      if (s.tone === 'wait') return 'ждёт вас';
      if (s.tone === 'stopped') return 'стоит';
      if (s.tone === 'loop') return 'доработка';
      return 'идёт';
    case 'pending': return 'впереди';
    case 'absent': return 'нет задач';
    default: return '';
  }
}

// Время этапа: границы (dd.mm · ЧЧ:ММ – ЧЧ:ММ) + длительность «≈ N мин» (Б2).
// null/0 — без времени (стадия ещё не шла).
function stageTimeText(s: FeatureStage): string | null {
  const parts: string[] = [];
  if (s.firstStartedAt) {
    const d = new Date(s.firstStartedAt);
    if (!isNaN(d.getTime())) {
      const dd = `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
      const hh = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
      parts.push(`с ${dd} · ${hh}`);
    }
  }
  if (s.lastCompletedAt) {
    const d = new Date(s.lastCompletedAt);
    if (!isNaN(d.getTime())) {
      const hh = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
      parts.push(`до ${hh}`);
    }
  }
  if (s.approxMinutes > 0) parts.push(`≈ ${s.approxMinutes} мин`);
  return parts.length > 0 ? parts.join(' · ') : null;
}

// Чистая: исполнители ТЕКУЩЕГО (active) этапа; null — active-этапа нет (всё впереди/закрыто).
function activeStage(run: FeatureRun): FeatureStage | null {
  const stages = run.stages ?? [];
  return stages.find(s => s.state === 'active') ?? null;
}

// Чистая: подписи исполнителей (roleLabel + модель) — для тестов без DOM.
export function executorLines(stage: FeatureStage): { role: string; model: string | null }[] {
  return (stage.executors ?? []).map(x => ({
    role: roleLabel(x.taskRole, x.stepRef, x.lane) || (x.personaId ? 'исполнитель' : 'исполнитель'),
    model: x.modelLabel ?? null,
  }));
}

// Строка исполнителя: roleLabel + чип модели (подсказка «Авто → …») + NavTargetButton (чат · задача).
function ExecutorRow({ x, isMobile, projectId }: { x: FeatureStageExecutor; isMobile: boolean; projectId: string | null }) {
  const role = roleLabel(x.taskRole, x.stepRef, x.lane);
  const model = x.modelLabel;
  return (
    <div
      data-testid="feature-sheet-executor"
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs + 2, flexWrap: 'wrap',
        padding: '6px 0', borderTop: `1px solid ${C.borderLight}`, minWidth: 0,
      }}
    >
      <span
        data-testid="feature-sheet-executor-role"
        style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}
      >
        {role || (x.personaId ? 'исполнитель' : '—')}
      </span>
      {model && (
        <span
          data-testid="feature-sheet-executor-model"
          title={x.modelFull ?? model}
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
            background: C.bgSelected, borderRadius: 6, padding: '0 5px', whiteSpace: 'nowrap',
          }}
        >
          {model}
        </span>
      )}
      <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: 4 }}>
        <NavTargetButton
          kind="chat"
          chatId={x.chatId}
          chatArchived={x.chatArchived}
          isMobile={isMobile}
          onOpen={() => { if (x.chatId) void openChatById(x.chatId); }}
        />
        <NavTargetButton
          kind="task"
          taskId={x.taskId}
          isMobile={isMobile}
          onOpen={() => { openTaskInSection({ id: x.taskId, projectId: projectId ?? undefined }); }}
        />
      </span>
    </div>
  );
}

// Переключатель фич (мультифича): одна строка на фичу, в порядке Б-ord (от бэка).
// Первая — фича задачи этого чата (бэк ставит её первой). Клик — сменить выбранную фичу.
function FeatureSwitcher({ runs, selected, onSelect, isMobile }: {
  runs: FeatureRun[]; selected: number; onSelect: (i: number) => void; isMobile: boolean;
}) {
  return (
    <div
      data-testid="feature-sheet-switcher"
      role="tablist"
      aria-label="Фичи этого чата"
      style={{ display: 'flex', flexDirection: 'column', gap: 4, marginBottom: SP.sm }}
    >
      {runs.map((r, i) => {
        const title = r.epic?.title ?? r.featureId;
        const on = i === selected;
        return (
          <button
            key={r.featureId}
            type="button"
            role="tab"
            aria-selected={on}
            data-testid={`feature-sheet-feature-${i}`}
            onClick={() => onSelect(i)}
            style={{
              display: 'flex', alignItems: 'center', gap: 6, textAlign: 'left',
              border: `1px solid ${on ? C.accent : C.borderLight}`,
              background: on ? C.accentLight : C.bgWhite,
              borderRadius: R.md, padding: isMobile ? '0 10px' : '6px 10px',
              minHeight: isMobile ? 40 : undefined, cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: FS.sm,
              color: on ? C.accent : C.textSecondary, fontWeight: on ? 700 : 500,
              overflow: 'hidden',
            }}
          >
            <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1 }}>{title}</span>
          </button>
        );
      })}
    </div>
  );
}

// Тело шторки: переключатель (если мультифича) + 7 этапов + исполнители текущего этапа.
function SheetBody({ run, runs, selected, onSelect, isMobile, projectId }: {
  run: FeatureRun; runs: FeatureRun[]; selected: number; onSelect: (i: number) => void;
  isMobile: boolean; projectId: string | null;
}) {
  const stages = run.stages ?? [];
  const current = activeStage(run);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      {runs.length > 1 && (
        <FeatureSwitcher runs={runs} selected={selected} onSelect={onSelect} isMobile={isMobile} />
      )}

      {/* Список 7 этапов: имя · статус · время. */}
      <div data-testid="feature-sheet-stages" style={{ display: 'flex', flexDirection: 'column' }}>
        {stages.map(s => {
          const time = stageTimeText(s);
          const muted = s.state === 'pending' || s.state === 'absent';
          return (
            <div
              key={s.key}
              data-testid={`feature-sheet-stage-${s.key}`}
              style={{
                display: 'flex', alignItems: 'baseline', gap: 8, padding: '5px 0',
                borderBottom: `1px solid ${C.borderLight}`, minWidth: 0,
              }}
            >
              <span style={{
                fontSize: FS.sm, fontWeight: muted ? 500 : 700,
                color: muted ? C.textMuted : C.textHeading, whiteSpace: 'nowrap', flexShrink: 0,
              }}>
                {STAGE_NAME[s.key]}
              </span>
              <span style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap', flexShrink: 0 }}>
                {stageStatusText(s)}
              </span>
              {time && (
                <span style={{
                  marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs,
                  color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0,
                }}>
                  {time}
                </span>
              )}
            </div>
          );
        })}
      </div>

      {/* Исполнители ТЕКУЩЕГО (active) этапа: roleLabel + чип модели + (чат · задача). */}
      {current && (
        <div data-testid="feature-sheet-executors" style={{ display: 'flex', flexDirection: 'column' }}>
          <div style={{
            fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
            color: C.textMuted, margin: '2px 0 4px',
          }}>
            Сейчас · {STAGE_NAME[current.key]}
          </div>
          {current.executors.length > 0 ? (
            current.executors.map((x, i) => (
              <ExecutorRow key={`${x.taskId}-${i}`} x={x} isMobile={isMobile} projectId={projectId} />
            ))
          ) : (
            <div style={{ fontSize: FS.sm, color: C.textMuted }}>Исполнителей нет</div>
          )}
        </div>
      )}
    </div>
  );
}

// Раскрывающаяся панель на десктопе: role=dialog, Esc, тап по подложке, возврат фокуса.
// На мобиле подаёт Modal (bottom-sheet) — там Esc/подложка/крестик уже в Modal.
export function FeatureBannerSheet({ runs, isMobile = false, onClose, focusTarget, projectId }: {
  runs: FeatureRun[];
  isMobile?: boolean;
  onClose: () => void;
  // Элемент строки панели: на закрытие фокус возвращается на него.
  focusTarget?: React.RefObject<HTMLElement | null>;
  // Проект чата (область запроса; null — чат вне проектов).
  projectId?: string | null;
}) {
  // Выбранная фича (индекс в runs, порядок Б-ord от бэка). Кламп, если фич стало меньше.
  const [sel, setSel] = useState(0);
  const idx = Math.min(sel, Math.max(0, runs.length - 1));
  const run = runs[idx];

  // Возврат фокуса на строку при закрытии (desktop: панель; mobile: Modal сам не возвращает).
  const close = () => {
    onClose();
    const el = focusTarget?.current;
    if (el && typeof el.focus === 'function') {
      try { el.focus({ preventScroll: true }); } catch { el.focus(); }
    }
  };

  // Esc (desktop): закрывает панель. На мобиле Esc обрабатывает Modal.
  useEffect(() => {
    if (isMobile) return;
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); close(); } };
    document.addEventListener('keydown', h);
    return () => document.removeEventListener('keydown', h);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isMobile, onClose]);

  if (!run) return null;

  const body = (
    <SheetBody
      run={run}
      runs={runs}
      selected={idx}
      onSelect={setSel}
      isMobile={isMobile}
      projectId={projectId ?? null}
    />
  );

  if (isMobile) {
    // Bottom-sheet: Modal сам рисует drag-handle, крестик, Esc и подложку.
    return (
      <ModalLike onClose={close} title={run.epic?.title ?? run.featureId}>
        {body}
      </ModalLike>
    );
  }

  // Desktop: раскрывающаяся панель под строкой. role=dialog + aria-modal; тап по подложке
  // (fixed, inset 0) закрывает; Esc — в эффекте выше.
  return (
    <div style={{ position: 'relative' }}>
      <div
        data-testid="feature-sheet-panel"
        role="dialog"
        aria-modal="true"
        aria-label={run.epic?.title ?? 'Фича'}
        style={{
          position: 'relative', zIndex: 2,
          background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
          padding: '10px 12px', marginTop: 6,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', marginBottom: SP.xs }}>
          <span style={{ fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {run.epic?.title ?? run.featureId}
          </span>
          <button
            type="button"
            data-testid="feature-sheet-close"
            aria-label="Закрыть"
            onClick={close}
            style={{
              border: 'none', background: 'transparent', cursor: 'pointer',
              padding: 4, borderRadius: R.sm, color: C.textSecondary, flexShrink: 0,
            }}
          >
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </button>
        </div>
        {body}
      </div>
      {/* Подложка: тап вне панели закрывает (zIndex 1 < 2 у панели). */}
      <div
        data-testid="feature-sheet-backdrop"
        onPointerDown={(e) => { if (e.target === e.currentTarget) close(); }}
        style={{ position: 'fixed', inset: 0, zIndex: 1, background: 'transparent' }}
      />
    </div>
  );
}

// Тонкая обёртка над Modal (bottom-sheet на мобиле): передаёт заголовок и onClose.
function ModalLike({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  return (
    <Modal title={title} onClose={onClose}>
      {children}
    </Modal>
  );
}
