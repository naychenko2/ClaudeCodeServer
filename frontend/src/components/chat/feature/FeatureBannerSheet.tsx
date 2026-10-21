import { useEffect, useMemo, useRef, useState } from 'react';
import { X } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../../lib/design';
import { roleLabel } from '../../../lib/roleLabel';
import { STAGE_NAME } from '../../../lib/chatGroups';
import { useCanHover } from '../../../lib/pointer';
import { NavTargetButton } from '../../feature/NavTargetButton';
import { openChatById } from '../../../lib/openChat';
import { openTaskInSection } from '../../../lib/tasks';
import { Modal } from '../../ui';
import { Tooltip } from '../../ui/Tooltip';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import type { FeatureRun, FeatureStage, FeatureStageExecutor } from '../../../types/featureRun';

// Шторка фичи (волна 3, Ф-pl-b; макет feature-banner-1, «А · Строка» + раскрытие из «Г»):
// тап по строке панели раскрывает РАЗВЁРНУТЫЙ СОСТАВ фичи. Состав (в порядке макета):
//   • переключатель фич — если у чата несколько фич, в том же порядке, что строка
//     (единый источник — порядок индекса, проп order; бэковский порядок runs не
//     пересортировывает, но совпадает с ним по первой фиче — PreferFirst);
//   • список 7 этапов со статусом и временем (границы + «≈ N мин»);
//   • исполнители ТЕКУЩЕГО (active) этапа: roleLabel, чип модели (подсказка по тапу),
//     NavTargetButton (чат · задача).
//
// Данные — ТОЛЬКО из списка `features?sessionId=` (runs, как в попапе), без догрузки
// `features/{id}`. Закрытие: Esc, тап вне, кнопка; фокус возвращается на строку.
//
// Подача: на мобиле (<768) — bottom-sheet (Modal, штатный паттерн продукта); на
// десктопе — раскрывающаяся панель под строкой (role=dialog, Esc, тап по подложке).
// Доступность: aria-expanded на строке (задаёт плашка, только когда шторка нарисована),
// role=dialog + aria-modal + focus-trap (M5: ловушка в самой шторке, не в ките) и
// возврат фокуса на строку при закрытии.

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

// Чип модели (mono) + подсказка с полной моделью. Минор 8: на мобиле нативный
// `title` по тапу не показывается (только по наведению) — чип-кнопка: тап
// раскрывает/закрывает подсказку (Tooltip с focused, тот же приём, что у ModelChip
// попапа, FeatureRunView); на десктопе наведение/клавиатура работают штатно.
function ModelChip({ label, full }: { label: string; full: string | null }) {
  const canHover = useCanHover();
  const [tapped, setTapped] = useState(false);
  const tip = `Модель чата: ${full && full !== label ? full : label}`;
  return (
    <Tooltip content={tip} focused={tapped}>
      <button
        type="button"
        data-testid="feature-sheet-executor-model"
        aria-label={tip}
        onClick={() => setTapped(t => !t)}
        // Десктоп: после клика чип сфокусирован — не «закрепляем» подсказку навсегда.
        onMouseLeave={() => { if (canHover) setTapped(false); }}
        style={{
          border: 'none', cursor: 'pointer', flexShrink: 0,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
          background: C.bgSelected, borderRadius: 6, padding: '0 5px', whiteSpace: 'nowrap',
        }}
      >
        {label}
      </button>
    </Tooltip>
  );
}

// Строка исполнителя: roleLabel + чип модели (подсказка по тапу) + NavTargetButton (чат · задача).
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
      {model && <ModelChip label={model} full={x.modelFull} />}
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

// === Focus trap (M5) ===
//
// Минимальная ловушка фокуса в самой шторке (НЕ в ките: у китового Modal управления
// фокусом нет, а у десктопной панели aria-modal — модалка без ловушки = нарушение ARIA).
// Поведение:
//   • при открытии — фокус уходит на ПЕРВЫЙ фокусируемый элемент внутри панели
//     (крестик); если фокусируемых нет — на корень (tabIndex=-1);
//   • Tab / Shift+Tab — по кругу внутри панели (с последнего — на первый, с первого — на
//     последний);
//   • при закрытии — возврат на строку (close(), уже было).
// Корень ловушки: desktop — сама панель (ref на ней, крестик внутри); mobile (Modal) —
// оверлей (cc-overlay), найденный обходом вверх от контента: крестик Modal живёт в
// оверлее, и ловушка должна его включать.
type AnyNode = Node & { nodeType: number; parentNode: Node | null };

// Фокусируемые элементы внутри корня: BUTTON/A/INPUT/SELECT/TEXTAREA + [tabindex>=-1];
// disabled не считается. tabindex="-2" (и любые < -1) — НЕ фокусируемы.
export function focusablesIn(root: Element): Element[] {
  const out: Element[] = [];
  const FOCUSABLE = new Set(['BUTTON', 'A', 'INPUT', 'SELECT', 'TEXTAREA']);
  const walk = (n: Node) => {
    if (n.nodeType !== 1) return;
    const el = n as Element;
    const tag = el.tagName ? el.tagName.toUpperCase() : '';
    const tab = el.getAttribute ? el.getAttribute('tabindex') : null;
    const tabVal = tab != null ? parseInt(tab, 10) : null;
    const disabled = el.getAttribute ? el.getAttribute('disabled') != null : false;
    // tabindex < -1 (напр. -2) — явно НЕ фокусируемый, даже если тег в FOCUSABLE.
    const explicitlyNonFocusable = tabVal != null && tabVal < -1;
    const focusable = FOCUSABLE.has(tag) || (tabVal != null && tabVal >= -1);
    if (!disabled && !explicitlyNonFocusable && focusable) out.push(el);
    for (const c of el.childNodes) walk(c);
  };
  walk(root);
  return out;
}

// Корень ловушки: desktop — сам ref (панель); mobile — оверлей Modal (cc-overlay),
// найденный обходом вверх от контента.
function trapRoot(ref: React.RefObject<HTMLElement | null>, isMobile: boolean): Element | null {
  const start = ref.current;
  if (!start) return null;
  if (!isMobile) return start;
  let n: AnyNode | null = start;
  while (n) {
    if (n.nodeType === 1) {
      const el = n as Element;
      const cls = el.getAttribute ? el.getAttribute('class') ?? '' : '';
      if (cls.includes('cc-overlay')) return el;
    }
    n = n.parentNode;
  }
  return start; // оверлея нет (не портал) — сам контент
}

// Ловушка: на монтировании — фокус внутрь, далее Tab/Shift+Tab по кругу.
function useFocusTrap(ref: React.RefObject<HTMLElement | null>, isMobile: boolean) {
  useEffect(() => {
    const root = trapRoot(ref, isMobile);
    if (!root) return;
    // При открытии: фокус на первый фокусируемый (или на корень, если их нет).
    const els = focusablesIn(root);
    const target = (els[0] ?? root) as HTMLElement;
    if (els.length === 0) target.tabIndex = -1;
    try { target.focus({ preventScroll: true }); } catch { target.focus(); }
    // Tab/Shift+Tab — по кругу внутри панели.
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Tab') return;
      const list = focusablesIn(root);
      if (list.length === 0) { e.preventDefault(); return; }
      const active = (typeof document !== 'undefined' ? document.activeElement : null) as Element | null;
      const i = active ? list.indexOf(active) : -1;
      if (e.shiftKey) {
        if (i <= 0) { e.preventDefault(); (list[list.length - 1] as HTMLElement).focus(); }
      } else {
        if (i === -1 || i === list.length - 1) { e.preventDefault(); (list[0] as HTMLElement).focus(); }
      }
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); };
  }, [ref, isMobile]);
}

// Раскрывающаяся панель на десктопе: role=dialog + aria-modal + focus-trap, Esc, тап по
// подложке, возврат фокуса. На мобиле подаёт Modal (bottom-sheet) — там Esc/подложка/
// крестик уже в Modal; ловушка — на оверлее (крестик Modal внутри).
export function FeatureBannerSheet({ runs, isMobile = false, onClose, focusTarget, projectId, loading, error, order, initialSel = 0 }: {
  runs: FeatureRun[];
  isMobile?: boolean;
  onClose: () => void;
  // Элемент строки панели: на закрытие фокус возвращается на него.
  focusTarget?: React.RefObject<HTMLElement | null>;
  // Проект чата (область запроса; null — чат вне проектов).
  projectId?: string | null;
  // Состояния запроса runs (F-pop-a): loading → «Загрузка…», error → причина.
  loading?: boolean;
  error?: string | null;
  // Порядок фич (id) — единый источник с строкой (индекс); runs пересортировывается
  // под него, чтобы переключатель и «· ещё N» шли в одном порядке (минор 7).
  order?: string[];
  // Минор 4: индекс фичи, выбранной в строке («· ещё N») — шторка открывает её,
  // а не первую по order.
  initialSel?: number;
}) {
  // Минор 7: порядок переключателя = порядок строки (индекс), не бэковый порядок runs.
  const orderedRuns = useMemo(() => {
    if (!order || order.length === 0) return runs;
    const pos = new Map(order.map((id, i) => [id, i]));
    return [...runs].sort((a, b) => (pos.get(a.featureId) ?? Infinity) - (pos.get(b.featureId) ?? Infinity));
  }, [runs, order]);

  // Выбранная фича (индекс в orderedRuns). Кламп, если фич стало меньше.
  // Минор 4: инициализация из строки (selected feature in the row).
  const [sel, setSel] = useState(initialSel);
  const idx = Math.min(sel, Math.max(0, orderedRuns.length - 1));
  const run = orderedRuns[idx];

  // Корень ловушки: desktop — панель (ref на ней); mobile — контент (обход к оверлею).
  const trapRef = useRef<HTMLDivElement | null>(null);
  useFocusTrap(trapRef, isMobile);

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

  // Минор 6: runs пуст — не молчать: loading → «Загрузка…», error → причина;
  // только если и loading, и error нет — шторка не рисуется (aria-expanded закрывает плашка).
  if (!run) {
    if (!loading && !error) return null;
    const stateText = loading ? 'Загрузка…' : (error ?? 'Не удалось загрузить');
    // run ещё нет (loading/error) — названия нет: «Фича».
    const title = 'Фича';
    if (isMobile) {
      return (
        <ModalLike onClose={close} title={title}>
          <div ref={trapRef} data-testid="feature-sheet-state" style={{ fontSize: FS.sm, color: C.textSecondary }}>{stateText}</div>
        </ModalLike>
      );
    }
    return (
      <div style={{ position: 'relative' }}>
        <div
          ref={trapRef}
          data-testid="feature-sheet-panel"
          role="dialog"
          aria-modal="true"
          aria-label={title}
          style={{
            position: 'relative', zIndex: 2,
            background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
            padding: '10px 12px', marginTop: 6,
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', marginBottom: SP.xs }}>
            <span style={{ fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {title}
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
          <div data-testid="feature-sheet-state" style={{ fontSize: FS.sm, color: C.textSecondary }}>{stateText}</div>
        </div>
        <div
          data-testid="feature-sheet-backdrop"
          onPointerDown={(e) => { if (e.target === e.currentTarget) close(); }}
          style={{ position: 'fixed', inset: 0, zIndex: 1, background: 'transparent' }}
        />
      </div>
    );
  }

  const body = (
    <SheetBody
      run={run}
      runs={orderedRuns}
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
        <div ref={trapRef}>{body}</div>
      </ModalLike>
    );
  }

  // Desktop: раскрывающаяся панель под строкой. role=dialog + aria-modal (честно:
  // focus-trap выше запирает фокус внутри); тап по подложке (fixed, inset 0) закрывает;
  // Esc — в эффекте выше.
  return (
    <div style={{ position: 'relative' }}>
      <div
        ref={trapRef}
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
