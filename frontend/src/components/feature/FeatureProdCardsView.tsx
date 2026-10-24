// Верстка ленты «Производство» (волна 4, W4-2F2, вариант 2А2 по спецификации
// docs/mockups/features-overview-4-spec.md, точка 2).
//
// Модель — W4-2F1 (FeatureProdCards.buildProdCards): здесь только разметка, ничего
// не пересчитывается. Одна карточка на шаг в порядке старта; цепочка узлов
// «работа → вернул → доработка → принят», узлы с chatId — переходы в чат витка;
// счётчик циклов (2+ — тон предупреждения); текущий узел обведён акцентом; строки
// «старт · чат · задача» нет — переходы живут на коде шага (Menu) и на узлах.
//
// Замечания ревью (Глеб, R4c), держатся здесь:
//   • слово плашки — из card.state (корзина проектора), stepStatusView не используется;
//   • «текущий» узел берётся как есть из модели: state 'cur' стоит только на активном
//     действии (работа/доработка), рендер завершённые узлы не перетирает;
//   • «закрыта» задача — из одного источника: статус задачи (task.status === 'done'),
//     как в ExecutorRow, а не outcome цепочки.
// Пустого состояния у компонента нет: при пустой ленте он не рисует ничего —
// существующее пустое состояние остаётся у вкладки.

import { Fragment, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, ReactElement } from 'react';
import { MessageCircle, FileText, Check, Clock, Minus, ChevronDown } from 'lucide-react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Menu, MenuItem } from '../ui/Menu';
import { Tooltip } from '../ui/Tooltip';
import { PressableArea } from '../ui/PressableArea';
import { LiveDot } from '../ui/LiveDot';
import { ExecutorAvatar, ModelChip, stepModel } from './FeatureTimelineBits';
import { buildProdCards, type ProdCard, type ProdCardState, type ProdNode } from './FeatureProdCards';
import { formatDateTime } from './FeatureStatsBits';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type NavMarks } from '../../lib/featureNavMarks';
import { getPersonaById } from '../../lib/personas';
import type { FeatureRun, FeatureStepChain, FeatureStageExecutor } from '../../types/featureRun';

// 360 (возврат R4d): зона касания узла — 40 px на самой кнопке. Видимая капсула
// по факту 26 px (lineHeight 20 + поля 2×2 + рамка 1×2), реальный вылет — (40−26)/2
// = 7 px; OVERHANG = 10 — раскладочная константа (отступы и зазор считаются от неё),
// она больше фактического вылета — зоны соседних строк переноса не соприкасаются
// с запасом. Отступ цепочки от шапки (12 px) тоже больше фактического вылета.
const TOUCH_H = 40;
const NODE_H = 20; // раскладочная высота узла (отступы компенсируют вылет)
const OVERHANG = (TOUCH_H - NODE_H) / 2; // 10 — раскладочный, фактический вылет 7

// «Цикл» по числу: 1 — цикл, 2–4 — цикла, 5+ — циклов.
function cycleWord(n: number): string {
  if (n === 1) return 'цикл';
  if (n <= 4) return 'цикла';
  return 'циклов';
}

// === Узел цепочки ===

// Тон узла — строго по таблице спецификации (ok/ret/cur/pend), пары *Bg/*Text
// существующие; «текущий» — белая подложка, акцентная рамка и фокус-тень.
function nodeBaseStyle(state: ProdNode['state']): CSSProperties {
  const base: CSSProperties = {
    fontSize: FS.xs,
    lineHeight: '20px',
    borderRadius: R.max,
    padding: `${SP.xxs}px ${SP.sm}px`,
    whiteSpace: 'nowrap',
    display: 'inline-flex',
    alignItems: 'center',
  };
  if (state === 'ok') return { ...base, background: C.successBg, color: C.successText };
  if (state === 'ret') return { ...base, background: C.warningBg, color: C.warningText };
  if (state === 'cur') {
    return { ...base, background: C.bgWhite, color: C.textHeading, fontWeight: 600, border: `1px solid ${C.accent}`, boxShadow: SHADOW.focus };
  }
  return { ...base, background: 'transparent', color: C.textMuted, border: `1px dashed ${C.dashed}` };
}

// Узел с chatId — контрол (переход в чат витка); без chatId — текст, не кнопка.
// Состояния: наведение — дополнительная обводка (размер не меняется), фокус —
// SHADOW.focus (у «текущего» — рамка 2 px вместо 1), нажатие — C.bgSelected там,
// где фон не статусный (ok/ret остаются своими тонами).
function ProdNodeChip({ node, featureId, featureTitle, isMobile }: {
  node: ProdNode; featureId: string; featureTitle?: string | null; isMobile: boolean;
}) {
  const [hover, setHover] = useState(false);
  const [focused, setFocused] = useState(false);
  const [pressed, setPressed] = useState(false);

  const open = () => {
    if (!node.chatId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: node.chatId }, featureTitle);
    void openChatById(node.chatId);
  };

  const style: CSSProperties = { ...nodeBaseStyle(node.state) };
  if (hover) style.boxShadow = `${node.state === 'cur' ? SHADOW.focus + ', ' : ''}0 0 0 1px ${C.accentMuted}`;
  if (focused && node.state !== 'cur') style.boxShadow = SHADOW.focus;
  if (focused && node.state === 'cur') style.border = `2px solid ${C.accent}`;
  if (pressed && node.state !== 'ok' && node.state !== 'ret') style.background = C.bgSelected;

  if (!node.chatId) {
    // Не контрол: ни курсора-руки, ни aria-label — смысл виден текстом.
    return <span data-testid="prod-node-plain" style={style}>{node.label}</span>;
  }

  // Общие обработчики: нажатие — по pointer-событиям (тач и мышь дают их одинаково),
  // фокус — по :focus-visible (кольцо только с клавиатуры, не после тапа).
  const handlers = {
    onClick: open,
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => { setHover(false); setPressed(false); },
    onPointerDown: () => setPressed(true),
    onPointerUp: () => setPressed(false),
    // Сход пальца с узла: pointerup вне узла, браузер посылает узлу pointercancel —
    // без него pressed залипает (минор R4f).
    onPointerCancel: () => setPressed(false),
    onFocus: (e: FocusEvent<HTMLButtonElement>) => setFocused(e.currentTarget.matches(':focus-visible')),
    onBlur: () => setFocused(false),
  };

  // 360: поле касания 40 px — на самой кнопке (minHeight + отрицательные отступы:
  // раскладка остаётся 20 px), видимая капсула — внутренний span с тоном узла.
  if (isMobile) {
    return (
      <button
        type="button"
        data-testid="prod-node"
        aria-label={`Открыть чат витка: ${node.label}`}
        {...handlers}
        style={{
          display: 'inline-flex', alignItems: 'center',
          minHeight: TOUCH_H, margin: `-${OVERHANG}px 0`,
          background: 'transparent', border: 'none', padding: 0, cursor: 'pointer',
        }}
      >
        <span style={style}>{node.label}</span>
      </button>
    );
  }
  return (
    <Tooltip content="Открыть чат витка" focused={focused}>
      <button
        type="button"
        data-testid="prod-node"
        aria-label={`Открыть чат витка: ${node.label}`}
        {...handlers}
        style={{ ...style, cursor: 'pointer' }}
      >
        {node.label}
      </button>
    </Tooltip>
  );
}

// === Код шага: триггер меню «старт · чат · задача» ===

// Есть ли у шага что открывать в меню «старт · чат · задача»: нет — код остаётся текстом.
function stepHasMenu(card: ProdCard, chain: FeatureStepChain | undefined): boolean {
  return !!chain && (!!card.startedAt || !!card.chatId);
}

// Нет данных шага (не начат: ни старта, ни чата, ни задачи) — код остаётся обычным
// текстом без рамки, не кнопкой: открывать нечего.
function StepCode({ card, chain, featureId, featureTitle, marks, isMobile, nameOf, text }: {
  text: string;
  card: ProdCard; chain: FeatureStepChain | undefined; featureId: string; featureTitle?: string | null; marks: NavMarks; isMobile: boolean;
  nameOf: (id: string) => string | null;
}) {
  const plain: CSSProperties = {
    fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary, flexShrink: 0,
  };
  // «Нет данных шага» — ни старта, ни чата (спецификация, краевые): открывать нечего.
  if (!chain || !stepHasMenu(card, chain)) return null;

  const [open, setOpen] = useState(false);
  const [hover, setHover] = useState(false);
  const ref = useRef<HTMLButtonElement | null>(null);

  // Подпись пункта «чат»: последний узел-действие цепочки — работа или доработка.
  const lastAction = [...card.nodes].reverse().find(n => n.kind === 'work' || n.kind === 'rework');
  const actionWord = lastAction?.kind === 'rework' ? 'доработка' : 'работа';
  // Имя — через тот же резолвер, что и подписи узлов (единый источник, R4d-минор).
  const who = card.personaId ? nameOf(card.personaId) ?? 'персона недоступна' : '';
  const taskId = chain.taskIds[0] ?? null;

  const style: CSSProperties = {
    ...plain,
    fontFamily: FONT.sans,
    fontWeight: 600,
    background: C.bgWhite,
    border: `1px solid ${open || hover ? C.accentMuted : C.borderLight}`,
    color: hover || open ? C.textHeading : C.textSecondary,
    borderRadius: R.sm,
    padding: `0 ${SP.xs}px`,
    cursor: 'pointer',
    ...(isMobile ? { minWidth: 40, minHeight: 40, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' } : null),
    ...(open ? { background: C.bgSelected } : null),
  };

  return (
    <>
      <button
        ref={ref}
        type="button"
        data-testid={`prod-code-${card.stepRef}`}
        aria-label={`Шаг ${card.stepRef}: старт, чат и задача`}
        aria-expanded={open}
        onClick={() => setOpen(v => !v)}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        style={style}
      >
        {text}
      </button>
      {open && (
        <Menu onClose={() => setOpen(false)} align="left" minWidth={230} anchor={ref.current?.getBoundingClientRect()}>
          {card.startedAt && (
            <div
              data-testid="prod-menu-start"
              style={{ display: 'grid', gridTemplateColumns: '46px minmax(0, 1fr)', gap: SP.sm, alignItems: 'baseline', padding: '9px 10px' }}
            >
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>старт</span>
              <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textPrimary }}>{formatDateTime(card.startedAt)}</span>
            </div>
          )}
          {/* data-testid на обёртке: у MenuItem своего атрибута нет, а тесту нужен
              точный адрес пункта (как в FeatureTimelineTab.test.ts). */}
          {card.chatId && (
            <span data-testid="prod-menu-chat" style={{ display: 'flex' }}>
              <MenuItem
                icon={<MessageCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden />}
                label={`Чат ${who || 'исполнителя'} · ${actionWord}`}
                hint={marks.chatArchived(card.chatId) ? 'в архиве' : undefined}
                isMobile={isMobile}
                onClick={() => {
                  setOpen(false);
                  recordFeatureReturn(featureId, { kind: 'chat', id: card.chatId! }, featureTitle);
                  void openChatById(card.chatId!);
                }}
              />
            </span>
          )}
          {taskId && (
            <span data-testid="prod-menu-task" style={{ display: 'flex' }}>
              <MenuItem
                icon={<FileText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden />}
                label={`Карточка задачи ${card.stepRef}`}
                hint={marks.taskClosed(taskId) ? 'задача закрыта' : undefined}
                isMobile={isMobile}
                onClick={() => {
                  setOpen(false);
                  recordFeatureReturn(featureId, { kind: 'task', id: taskId }, featureTitle);
                  openTaskInSection({ id: taskId });
                }}
              />
            </span>
          )}
        </Menu>
      )}
    </>
  );
}

// === Карточка шага ===

// Тоны состояния: плашка (фон/текст), иконка и кромка карточки.
const STATE_WORD: Record<ProdCardState, string> = { done: 'Готово', run: 'Идёт', wait: 'Ждёт', queue: 'В очереди', withdrawn: 'Снят' };

function stateTone(state: ProdCardState): CSSProperties {
  if (state === 'done') return { background: C.successBg, color: C.successText };
  if (state === 'run') return { background: C.accentLight, color: C.accent };
  if (state === 'wait') return { background: C.infoBg, color: C.info };
  if (state === 'withdrawn') return { background: C.bgPanel, color: C.textMuted };
  return { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.dashed}` };
}

// Иконка состояния 20 px: форма и цвет различают состояние и без цвета.
function StateIcon({ state }: { state: ProdCardState }) {
  const box: CSSProperties = {
    width: 20, height: 20, borderRadius: R.full, flexShrink: 0,
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', ...stateTone(state), boxShadow: undefined,
  };
  if (state === 'run') {
    return <span data-testid="prod-state-icon" data-state={state} style={box}><LiveDot color={C.accent} size={8} label={STATE_WORD.run} /></span>;
  }
  if (state === 'queue') {
    return <span data-testid="prod-state-icon" data-state={state} role="img" aria-label={STATE_WORD.queue} style={{ ...box, background: 'transparent', border: `1px dashed ${C.dashed}`, boxSizing: 'border-box' }} />;
  }
  const Icon = state === 'done' ? Check : state === 'withdrawn' ? Minus : Clock;
  return (
    <span data-testid="prod-state-icon" data-state={state} role="img" aria-label={STATE_WORD[state]} style={box}>
      <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden />
    </span>
  );
}

// «N мин» / «N ч» от ISO-метки; нет метки или она в будущем — null (времени не пишем).
function elapsedLabel(since: string | null): string | null {
  if (!since) return null;
  const ms = Date.now() - Date.parse(since);
  if (!Number.isFinite(ms) || ms < 0) return null;
  const min = Math.floor(ms / 60000);
  return min < 60 ? `${min} мин` : `${Math.floor(min / 60)} ч`;
}

function shortDate(iso: string | null): string | null {
  const d = iso ? new Date(iso) : null;
  if (!d || Number.isNaN(d.getTime())) return null;
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
}

// Плашка состояния: слово + «чем занят» + время. Времени нет в агрегате — только слово.
function stateBadge(card: ProdCard, chain: FeatureStepChain | undefined): { word: string; rest: string } {
  const sep = (parts: Array<string | null>) => parts.filter(Boolean).map(p => ` · ${p}`).join('');
  if (card.state === 'run') {
    return { word: 'идёт', rest: sep([card.runKind === 'rework' ? 'доработка' : 'работа', elapsedLabel(card.runningSince)]) };
  }
  if (card.state === 'wait') {
    const word = card.stopped ? 'остановлен'
      : card.awaitingReview ? (chain?.outcome === 'running' ? 'идёт ревью' : 'ждёт ревью')
      : chain?.outcome === 'returned' && chain.status ? chain.status : 'ждёт запуска';
    return { word, rest: sep([elapsedLabel(card.waitingSince)]) };
  }
  if (card.state === 'withdrawn') return { word: 'снят с выкладки', rest: '' };
  if (card.state === 'done') return { word: 'принят', rest: sep([shortDate(card.acceptedAt)]) };
  return { word: 'в очереди', rest: '' };
}

// Мини-трек цепочки: сегмент 12×6 на узел. Текущий дышит (класс cc-live-dot, при
// prefers-reduced-motion неподвижен), впереди — пунктир, возврат — warning.
function MiniTrack({ nodes }: { nodes: ProdNode[] }) {
  return (
    <span data-testid="prod-track" aria-hidden style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, flexShrink: 0 }}>
      {nodes.map((n, i) => {
        const seg: CSSProperties = { width: 12, height: 6, borderRadius: R.sm, boxSizing: 'border-box' };
        const style: CSSProperties =
          n.state === 'ok' ? { ...seg, background: C.success } :
          n.state === 'ret' ? { ...seg, background: C.warning } :
          n.state === 'cur' ? { ...seg, background: C.accent } :
          { ...seg, border: `1px dashed ${C.dashed}` };
        return <span key={i} data-testid={`prod-seg-${n.state}`} className={n.state === 'cur' ? 'cc-live-dot' : undefined} style={style} />;
      })}
    </span>
  );
}

function ProdCardView({ card, chain, executors, featureId, featureTitle, marks, isMobile, nameOf }: {
  card: ProdCard; chain: FeatureStepChain | undefined; executors: FeatureStageExecutor[];
  featureId: string; featureTitle?: string | null; marks: NavMarks; isMobile: boolean;
  nameOf: (id: string) => string | null;
}) {
  const [open, setOpen] = useState(false);
  const { state } = card;

  // Тон карточки: готово — без фона, приглушённый текст; очередь — ещё и пунктирная рамка;
  // идёт — кромка accent; ждёт — кромка info.
  const style: CSSProperties = {
    background: state === 'run' || state === 'wait' ? C.bgCard : 'transparent',
    border: state === 'queue' ? `1px dashed ${C.dashed}` : `1px solid ${C.borderLight}`,
    borderRadius: R.xl,
    padding: `${SP.sm}px ${SP.md}px`,
    boxShadow: state === 'run' ? `inset 3px 0 0 ${C.accent}` : state === 'wait' ? `inset 3px 0 0 ${C.info}` : undefined,
  };
  const dim = state === 'done' || state === 'queue' || state === 'withdrawn';

  const badge = stateBadge(card, chain);
  const name = card.personaId ? nameOf(card.personaId) ?? 'персона недоступна' : null;
  const model = stepModel(executors, card.stepRef);
  const hasMenu = stepHasMenu(card, chain);

  return (
    <PressableArea
      data-testid={`prod-card-${card.stepRef || 'no-ref'}`}
      className="cc-prod-card"
      onPress={() => setOpen(v => !v)}
      aria-expanded={open}
      aria-label={`Шаг ${card.stepRef}, ${STATE_WORD[state]}: ${card.title}`}
      style={style}
    >
      <div data-testid="prod-row1" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
        <StateIcon state={state} />
        <span
          data-testid={hasMenu ? `prod-code-text-${card.stepRef}` : `prod-code-plain-${card.stepRef}`}
          style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary, flexShrink: 0 }}
        >
          {card.stepRef}
        </span>
        <span
          data-testid="prod-title"
          style={{
            fontWeight: 700, color: dim ? C.textSecondary : C.textHeading,
            flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}
        >
          {card.title}
        </span>
        <span
          aria-hidden
          style={{
            width: isMobile ? TOUCH_H : 20, height: isMobile ? TOUCH_H : 20, margin: isMobile ? `-${OVERHANG}px -${SP.sm}px -${OVERHANG}px 0` : 0,
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, color: C.textMuted,
          }}
        >
          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: open ? 'rotate(180deg)' : undefined }} />
        </span>
      </div>

      <div data-testid="prod-row2" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs, minWidth: 0 }}>
        <span
          data-testid="prod-status"
          style={{ ...stateTone(state), fontSize: FS.xs, fontWeight: 600, padding: `0 ${SP.xs}px`, borderRadius: R.sm, whiteSpace: 'nowrap', lineHeight: '16px' }}
        >
          {badge.word}{badge.rest}
        </span>
        <MiniTrack nodes={card.nodes} />
        {card.cycles > 0 ? (
          <span
            data-testid="prod-cycles"
            title="Циклов ревью"
            style={{
              fontSize: FS.xs, fontWeight: 700,
              color: card.cycles >= 2 ? C.warningText : C.textSecondary,
              border: `1px solid ${card.cycles >= 2 ? C.warning : C.border}`,
              borderRadius: R.sm, padding: `0 ${SP.xs}px`, lineHeight: '18px', whiteSpace: 'nowrap',
            }}
          >
            {card.cycles} {cycleWord(card.cycles)}
          </span>
        ) : null}
      </div>

      {open && (
        // Раскрытая часть живёт своей жизнью: клики по узлам цепочки и меню кода (портал
        // всплывает сюда по дереву React) не сворачивают карточку.
        <div data-testid="prod-details" onClick={e => e.stopPropagation()} style={{ cursor: 'default' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.sm }}>
            <ExecutorAvatar personaId={card.personaId} />
            {name && <span style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap' }}>{name}</span>}
            {model && <ModelChip label={model.label} full={model.full} isMobile={isMobile} />}
            <StepCode text="старт · чат · задача" card={card} chain={chain} featureId={featureId} featureTitle={featureTitle} marks={marks} isMobile={isMobile} nameOf={nameOf} />
          </div>

          {/* 360: строки цепочки не наезжают зонами касания — вертикальный зазор
              2×OVERHANG = 20 px, фактический вылет капсулы 7 px (зазор с запасом);
              отступ от шапки 12 px — тоже больше фактического вылета. На десктопе зоны
              не выступают. */}
          <div data-testid="prod-chain" style={{ display: 'flex', alignItems: 'center', gap: isMobile ? `${OVERHANG * 2}px ${SP.xs}px` : SP.xs, flexWrap: 'wrap', marginTop: isMobile ? SP.md : SP.sm }}>
            {card.nodes.map((node, i) => (
              <span key={`${card.stepRef}-${i}`} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
                <ProdNodeChip node={node} featureId={featureId} featureTitle={featureTitle} isMobile={isMobile} />
                {i < card.nodes.length - 1 && <span aria-hidden style={{ fontSize: FS.xs, color: C.textMuted }}>→</span>}
              </span>
            ))}
          </div>

          {card.returnReason && (
            <div
              data-testid="prod-reason"
              style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textSecondary, paddingLeft: SP.sm, borderLeft: `2px solid ${C.warning}`, overflowWrap: 'anywhere' }}
            >
              {card.returnReason}
            </div>
          )}
        </div>
      )}
    </PressableArea>
  );
}

// Сводка этапа над лентой: «2 готово · 1 идёт · 1 ждёт · 1 в очереди» и полоса сегментов.
// Числа — из stepProgress (бэк считает), здесь только показ; нет агрегата — сводки нет.
function StageSummary({ run }: { run: FeatureRun }) {
  const p = run.stepProgress;
  if (!p || p.total <= 0) return null;
  const parts: Array<{ n: number; word: string; color: string | null }> = [
    { n: p.accepted, word: 'готово', color: C.success },
    { n: p.work, word: 'идёт', color: C.accent },
    { n: p.review, word: 'ждёт', color: C.info },
    { n: p.notCreated, word: 'в очереди', color: null },
  ];
  const shown = parts.filter(x => x.n > 0);
  return (
    <div data-testid="prod-summary" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
        {shown.map(x => `${x.n} ${x.word}`).join(' · ')}
      </span>
      <div aria-hidden style={{ display: 'flex', gap: SP.xxs }}>
        {shown.flatMap(x => Array.from({ length: x.n }, (_, i) => (
          <span
            key={`${x.word}-${i}`}
            data-testid="prod-summary-seg"
            style={{
              flex: 1, height: 6, borderRadius: R.sm, boxSizing: 'border-box',
              ...(x.color ? { background: x.color } : { border: `1px dashed ${C.dashed}` }),
            }}
          />
        )))}
      </div>
    </div>
  );
}

// Лента «Производство»: карточки на шаг в порядке старта (порядок даёт модель).
// «сейчас»-строка — только над текущей карточкой.
export function FeatureProdCardsView({
  run, featureId, featureTitle = null, personaName, marks = EMPTY_NAV_MARKS, isMobile = false,
}: {
  run: FeatureRun; featureId: string; featureTitle?: string | null;
  personaName?: (id: string) => string | null;
  marks?: NavMarks;
  isMobile?: boolean;
}): ReactElement | null {
  // Имя персоны — один источник для всей ленты: резолвер, который получает
  // buildProdCards (по умолчанию — getPersonaById). Шапка и меню зовут его же.
  const nameOf = personaName ?? ((id: string) => getPersonaById(id)?.name ?? null);
  const cards = buildProdCards(run, nameOf);
  if (cards.length === 0) return null;

  // Цепочка по коду шага — для статуса и пункта меню «задача».
  const chainByRef = new Map<string, FeatureStepChain>();
  for (const ch of run.stepChains ?? []) if (ch.stepRef) chainByRef.set(ch.stepRef, ch);
  const prodExecutors = (run.stages ?? []).find(s => s.key === 'prod')?.executors ?? [];

  return (
    <div data-testid="prod-stage" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <StageSummary run={run} />
      <div data-testid="prod-lane" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {cards.map(card => {
          const chain = chainByRef.get(card.stepRef);
          return (
            <Fragment key={card.stepRef || `card-${card.order}`}>
              {card.isCurrent && (
                <div data-testid="prod-nowline" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, fontWeight: 700, color: C.accent }}>
                  <span>сейчас</span>
                  <span style={{ flex: 1, height: 1, background: C.accentMuted }} />
                </div>
              )}
              <ProdCardView card={card} chain={chain} executors={prodExecutors} featureId={featureId} featureTitle={featureTitle} marks={marks} isMobile={isMobile} nameOf={nameOf} />
            </Fragment>
          );
        })}
      </div>
    </div>
  );
}
