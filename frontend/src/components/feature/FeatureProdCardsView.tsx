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
//
// GC-F8 (план «Гант на „Хронологии“», макет feature-gantt-7 §«Раскрывающаяся карточка»):
//   • в раскрытой части карточки — FeatureStepDetails («Приёмка», «Итог», «Время шага на оси
//     фичи») с кнопкой «Показать интервалы на дорожке ↑»; данные деталей — последняя задача
//     цепочки (макет: «критерии последней задачи шага»); ходы и ось приходят от вызывающего
//     (buildTurnsByTask / buildStageLanes().axis) — своей арифметики здесь нет;
//   • канал подсветки — проп/колбэк наверх (onShowIntervals → FeatureTimelineTab): кольцо у
//     интервалов «Производства» с ходами шага, остальные гаснут до 35 % на 2,4 с — живут у
//     вызывающего, глобального стора нет;
//   • шаг плана без задачи (planned) — пунктирная карточка «в плане, не заведён»: рамка,
//     плашка и имя опираются на признак planned, а не на state 'queue' (N4, GC-R-F3) —
//     обычный шаг в очереди и незаведённый шаг плана должны выглядеть по-разному;
//   • старые готовые карточки свёрнуты (foldProd): видны все незакрытые, последние 3 готовых
//     и раскрытая; над лентой «Показать все N шагов (скрыто готовых: K)», N — из свёртки;
//     выбор «показать все» живёт, пока лента смонтирована;
//   • карточка, чья цепочка содержит хвост после мержа (run.postMergeTails — тот же источник,
//     что у строк этапа, GC-F10a), помечается «· хвост после мержа».

import { Fragment, useEffect, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, ReactElement } from 'react';
import { MessageCircle, FileText, Check, Clock, Minus, ChevronDown } from 'lucide-react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Menu, MenuItem } from '../ui/Menu';
import { Tooltip } from '../ui/Tooltip';
import { PressableArea } from '../ui/PressableArea';
import { LiveDot } from '../ui/LiveDot';
import { Button } from '../ui/Button';
import { ExecutorAvatar, ModelChip, stepModel, buildTaskMap } from './FeatureTimelineBits';
import { buildProdCards, type ProdCard, type ProdCardState, type ProdNode } from './FeatureProdCards';
import { FeatureStepDetails, type StepDetailTurn } from './FeatureStepDetails';
import { foldProd } from './featureListFold';
import { formatDateTime } from './FeatureStatsBits';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type NavMarks } from '../../lib/featureNavMarks';
import { getPersonaById } from '../../lib/personas';
import type { LaneAxis } from './FeatureLaneModel';
import type { StageTurn } from '../../types/featureStats';
import type { FeatureRun, FeatureStepChain, FeatureStageExecutor, FeatureTask } from '../../types/featureRun';

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

// «Шаг» по числу (тот же приём, что pluralIntervals в FeatureTimelineTab): 1 шаг / 2–4 шага /
// 5+ шагов; 11–14 — шагов.
function stepWord(n: number): string {
  const m100 = n % 100;
  const m10 = n % 10;
  if (m100 >= 11 && m100 <= 14) return `${n} шагов`;
  if (m10 === 1) return `${n} шаг`;
  if (m10 >= 2 && m10 <= 4) return `${n} шага`;
  return `${n} шагов`;
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
function StepCode({ card, chain, featureId, featureTitle, projectId, marks, isMobile, nameOf, text }: {
  text: string;
  card: ProdCard; chain: FeatureStepChain | undefined; featureId: string; featureTitle?: string | null; projectId?: string; marks: NavMarks; isMobile: boolean;
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
                  openTaskInSection({ id: taskId, projectId });
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

// Шаг плана без задачи (planned — бэковый notCreated): имя, плашка и кромка берутся из этого
// признака, а не из state 'queue' (N4, GC-R-F3): обычный шаг в очереди и незаведённый шаг
// плана должны читаться по-разному.
const PLANNED_WORD = 'в плане, не заведён';
function stateWordOf(card: ProdCard): string {
  return card.planned ? PLANNED_WORD : STATE_WORD[card.state];
}

function stateTone(state: ProdCardState): CSSProperties {
  if (state === 'done') return { background: C.successBg, color: C.successText };
  if (state === 'run') return { background: C.accentLight, color: C.accent };
  if (state === 'wait') return { background: C.infoBg, color: C.info };
  if (state === 'withdrawn') return { background: C.bgPanel, color: C.textMuted };
  return { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.dashed}` };
}

// Иконка состояния 20 px: форма и цвет различают состояние и без цвета. У planned-карточки
// та же пунктирная форма, но имя — «в плане, не заведён» (N4).
function StateIcon({ state, planned }: { state: ProdCardState; planned: boolean }) {
  const box: CSSProperties = {
    width: 20, height: 20, borderRadius: R.full, flexShrink: 0,
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', ...stateTone(state), boxShadow: undefined,
  };
  if (state === 'run') {
    return <span data-testid="prod-state-icon" data-state={state} style={box}><LiveDot color={C.accent} size={8} label={STATE_WORD.run} /></span>;
  }
  if (state === 'queue') {
    return <span data-testid="prod-state-icon" data-state={state} role="img" aria-label={planned ? 'В плане, не заведён' : STATE_WORD.queue} style={{ ...box, background: 'transparent', border: `1px dashed ${C.dashed}`, boxSizing: 'border-box' }} />;
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
// planned — раньше состояния: её слово — из признака плана (N4).
function stateBadge(card: ProdCard, chain: FeatureStepChain | undefined): { word: string; rest: string } {
  const sep = (parts: Array<string | null>) => parts.filter(Boolean).map(p => ` · ${p}`).join('');
  if (card.planned) return { word: PLANNED_WORD, rest: '' };
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

// Строка вердикта (макет: «Приёмка … и вердикт ревью»): последний завершённый виток цепочки.
// Слова вердикта — те же, что у подписей узлов (roundLabel в FeatureProdCards); имя — через
// тот же резолвер ленты. Витков (или завершённых витков) нет — строки нет: не выдумываем.
function verdictLineOf(chain: FeatureStepChain | undefined, nameOf: (id: string) => string | null): string | null {
  if (!chain) return null;
  for (let i = (chain.rounds ?? []).length - 1; i >= 0; i--) {
    const r = chain.rounds[i];
    if (!r || r.verdict === 'running') continue;
    const word = r.verdict === 'accepted' ? 'принят'
      : r.verdict === 'accepted_with_remarks' ? 'принят с замечаниями'
      : r.verdict === 'returned' ? 'вернул' : 'снят';
    const who = r.personaId ? nameOf(r.personaId) : null;
    const head = r.reviewTaskId ? `Ревью ${r.reviewTaskId.slice(0, 8)}` : 'Ревью';
    return [`${head}: ${word}`, who, shortDate(r.completedAt ?? null)].filter(Boolean).join(' · ');
  }
  return null;
}

function ProdCardView({ card, chain, executors, featureId, featureTitle, projectId, marks, isMobile, nameOf, expanded, onToggle, taskMap, axis, turnsByTask, now, isTail, onShowIntervals }: {
  card: ProdCard; chain: FeatureStepChain | undefined; executors: FeatureStageExecutor[];
  featureId: string; featureTitle?: string | null; projectId?: string; marks: NavMarks; isMobile: boolean;
  nameOf: (id: string) => string | null;
  // Раскрытие живёт на уровне ленты: свёртка обязана знать раскрытую карточку — она не
  // прячется никогда (foldProd, expandedKey).
  expanded: boolean; onToggle: () => void;
  // Задачи ленты — для блока деталей (приёмка, свидетельства, роль задачи).
  taskMap: Map<string, FeatureTask>;
  // Ось фичи — та же шкала, что у дорожек этапа. null — блока «Время шага» нет.
  axis: LaneAxis | null;
  // Ходы задач — buildTurnsByTask вызывающего (единственный источник). null — полоса пуста.
  turnsByTask: Map<string, StageTurn[]> | null;
  // Фиксированный «сейчас» — тот же момент, что у buildTurnsByTask вызывающего (определение
  // живых ходов, тот же приём, что у ExecutorRow).
  now: number;
  // Хвост после мержа: задача цепочки из run.postMergeTails (тот же источник, что у строк
  // этапа, GC-F10a) — карточка помечается, это не обычная работа стадии.
  isTail: boolean;
  // «Показать интервалы на дорожке ↑» → подсветка вызывающего с taskIds шага.
  onShowIntervals?: (taskIds: string[]) => void;
}) {
  const { state } = card;

  // Тон карточки: готово — без фона, приглушённый текст; шаг плана без задачи — пунктирная
  // рамка по признаку planned (N4: не по state 'queue'); очередь (без плана) — тоже пунктир;
  // идёт — кромка accent; ждёт — кромка info.
  const style: CSSProperties = {
    background: state === 'run' || state === 'wait' ? C.bgCard : 'transparent',
    border: card.planned || state === 'queue' ? `1px dashed ${C.dashed}` : `1px solid ${C.borderLight}`,
    borderRadius: R.xl,
    padding: `${SP.sm}px ${SP.md}px`,
    boxShadow: state === 'run' ? `inset 3px 0 0 ${C.accent}` : state === 'wait' ? `inset 3px 0 0 ${C.info}` : undefined,
  };
  const dim = state === 'done' || state === 'queue' || state === 'withdrawn';

  const badge = stateBadge(card, chain);
  const name = card.personaId ? nameOf(card.personaId) ?? 'персона недоступна' : null;
  const model = stepModel(executors, card.stepRef);
  const hasMenu = stepHasMenu(card, chain);

  // Детали шага (GC-F8): последняя задача цепочки (макет: «критерии последней задачи шага») —
  // её приёмка, итог и вердикт. У planned-карточки задачи нет — блока деталей нет.
  const lastId = chain ? chain.taskIds[chain.taskIds.length - 1] : undefined;
  const mainTask = lastId ? taskMap.get(lastId) : undefined;
  const verdict = verdictLineOf(chain, nameOf);
  // Ходы шага для полосы «Время шага на оси фичи»: те же ходы (buildTurnsByTask), только
  // этой цепочки, в хронологическом порядке. Вид — по роли задачи — тому же источнику, по
  // которому бэк атрибутирует ход стадии (FeatureTimeMath.RoleToStage); живой — ход, чей
  // конец не раньше «сейчас» (тот же приём, что у ExecutorRow, GC-F10b).
  const stripTurns: StepDetailTurn[] = chain
    ? chain.taskIds.flatMap(id => turnsByTask?.get(id) ?? [])
      .map(t => {
        const toMs = new Date(t.to).getTime();
        const role = taskMap.get(t.taskId)?.role;
        const review = role === 'review' || role === 'qa' || role === 'final';
        const live = Number.isFinite(toMs) && toMs >= now;
        return { from: t.from, to: t.to, kind: live ? 'live' as const : review ? 'review' as const : 'work' as const };
      })
      .sort((a, b) => (a.from < b.from ? -1 : a.from > b.from ? 1 : 0))
    : [];

  return (
    <PressableArea
      data-testid={`prod-card-${card.stepRef || 'no-ref'}`}
      className="cc-prod-card"
      onPress={onToggle}
      aria-expanded={expanded}
      aria-label={`Шаг ${card.stepRef}, ${stateWordOf(card)}: ${card.title}`}
      style={style}
    >
      <div data-testid="prod-row1" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
        <StateIcon state={state} planned={card.planned} />
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
          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: expanded ? 'rotate(180deg)' : undefined }} />
        </span>
      </div>

      <div data-testid="prod-row2" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs, minWidth: 0 }}>
        <span
          data-testid="prod-status"
          style={{ ...stateTone(state), fontSize: FS.xs, fontWeight: 600, padding: `0 ${SP.xs}px`, borderRadius: R.sm, whiteSpace: 'nowrap', lineHeight: '16px' }}
        >
          {badge.word}{badge.rest}{isTail ? ' · хвост после мержа' : ''}
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

      {expanded && (
        // Раскрытая часть живёт своей жизнью: клики по узлам цепочки и меню кода (портал
        // всплывает сюда по дереву React) не сворачивают карточку.
        <div data-testid="prod-details" onClick={e => e.stopPropagation()} style={{ cursor: 'default' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.sm }}>
            <ExecutorAvatar personaId={card.personaId} />
            {name && <span style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap' }}>{name}</span>}
            {model && <ModelChip label={model.label} full={model.full} isMobile={isMobile} />}
            <StepCode text="старт · чат · задача" card={card} chain={chain} featureId={featureId} featureTitle={featureTitle} projectId={projectId} marks={marks} isMobile={isMobile} nameOf={nameOf} />
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

          {/* Блоки деталей шага (GC-F7/GC-F8): «Приёмка», «Итог», «Время шага на оси фичи»
              и «Показать интервалы на дорожке ↑» — только с колбэком вызывающего. */}
          {mainTask && (
            <FeatureStepDetails
              taskId={mainTask.id}
              acceptance={mainTask.acceptance ?? null}
              evidence={mainTask.evidence ?? null}
              verdictLine={verdict}
              turns={stripTurns}
              axis={axis}
              isTail={isTail}
              isMobile={isMobile}
              nowMs={now}
              onShowIntervals={onShowIntervals && chain ? () => onShowIntervals([...chain.taskIds]) : undefined}
            />
          )}
        </div>
      )}
    </PressableArea>
  );
}

// Сводка этапа над лентой: «2 готово · 1 идёт · 1 ждёт · 1 в плане» и полоса сегментов.
// Числа — по состояниям самих карточек, чтобы сводка не спорила с ними (корзина work без
// идущей задачи — «ждёт», а не «идёт»); нет агрегата stepProgress — сводки нет.
function StageSummary({ run, cards }: { run: FeatureRun; cards: ProdCard[] }) {
  const p = run.stepProgress;
  if (!p || p.total <= 0) return null;
  const count = (s: ProdCardState) => cards.filter(c => c.state === s).length;
  // Слово корзины queue — «в плане» (не «в очереди»): после H1/GC-R-F3 состояние 'queue'
  // бывает только у planned-карточек, и сводка обязана говорить то же, что карточка.
  const parts: Array<{ n: number; word: string; color: string | null }> = [
    { n: count('done'), word: 'готово', color: C.success },
    { n: count('run'), word: 'идёт', color: C.accent },
    { n: count('wait'), word: 'ждёт', color: C.info },
    { n: count('queue'), word: 'в плане', color: null },
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
// «сейчас»-строка — только над текущей карточкой. Старые готовые свёрнуты (foldProd):
// видны все незакрытые, последние 3 готовых и раскрытая карточка; над лентой — «Показать
// все N шагов (скрыто готовых: K)», N — из свёртки (сходится с тем, что откроется, тот же
// приём, что у строк этапа, GC-F10c); выбор живёт, пока лента смонтирована.
type ProdViewProps = {
  run: FeatureRun; featureId: string; featureTitle?: string | null;
  personaName?: (id: string) => string | null;
  marks?: NavMarks;
  isMobile?: boolean;
  // Ось фичи — та же шкала, что у дорожек этапа (buildStageLanes().axis). null — блок
  // «Время шага на оси» не рисуется: ставить его на несуществующую ось — ложь.
  axis?: LaneAxis | null;
  // Ходы задач — buildTurnsByTask вызывающего (единственный источник, GC-F10b).
  // null — полоса времени пустая.
  turnsByTask?: Map<string, StageTurn[]> | null;
  // Фиксированный «сейчас» — момент рендера вызывающего, тот же, что у его buildTurnsByTask
  // (определение живых ходов). Без него — фиксированный на монтирование (тот же чистый
  // приём, что у FeatureStepDetails/useNow: Date.now только в ленивом инициализаторе).
  now?: number;
  // Id хвостов после мержа. По умолчанию — из run.postMergeTails: тот же источник, что у
  // строк этапа (GC-F10a), своей арифметики «что такое хвост» здесь нет.
  tailIds?: Set<string> | null;
  // «Показать интервалы на дорожке ↑» → подсветка вызывающего с taskIds шага: кольцо у
  // интервалов «Производства» с ходами шага, остальные гаснут до 35 % на 2,4 с. Состояние
  // подсветки живёт у вызывающего (FeatureTimelineTab), глобального стора нет.
  onShowIntervals?: (taskIds: string[]) => void;
  // GC-F11: начальная раскрытая карточка «возврата по чипу» (expandedKey из снимка).
  // Применяется при монтировании и при смене пропа (случай живой ленты): карточка
  // раскрывается и получает фокус шапки. Это однократное восстановление, а не контроль
  // сверху: состояние раскрытия по-прежнему живёт в ленте.
  initialExpandedKey?: string | null;
  // Канал чтения текущего expandedKey для снимка вызывающего (recordFeatureReturn):
  // состояние живёт в ленте, ref только отражает его — включая сброс при unmount
  // (схлопнутый этап не имеет раскрытой карточки).
  expandedKeyRef?: { current: string | null } | null;
};

// Публичный вход — обычная функция БЕЗ хуков: FeatureTimelineTab зовёт её напрямую
// (`FeatureProdCardsView({...}) ?? ExecutorRow`, не JSX-элементом). Хуки внутри этой функции
// стали бы хуками вызывающего и сломали бы их порядок при схлопывании этапа
// («Rendered fewer hooks», находка связного прогона). Поэтому проверка «нет карточек → null»
// (фолбэк вызывающего на сирот, фикс R4e) — здесь, в бесхуковой части; сама лента с
// состоянием — отдельный компонент, со своим fiber'ом.
export function FeatureProdCardsView(props: ProdViewProps): ReactElement | null {
  // Имя персоны — один источник для всей ленты: резолвер, который получает
  // buildProdCards (по умолчанию — getPersonaById). Шапка и меню зовут его же.
  const nameOf = props.personaName ?? ((id: string) => getPersonaById(id)?.name ?? null);
  const cards = buildProdCards(props.run, nameOf);
  if (cards.length === 0) return null;
  return <ProdCardsBody {...props} nameOf={nameOf} cards={cards} />;
}

// Сама лента. Состояние (раскрытая карточка, «показать все», «сейчас») живёт здесь —
// пока лента смонтирована.
function ProdCardsBody({
  run, featureId, featureTitle = null, marks = EMPTY_NAV_MARKS, isMobile = false,
  axis = null, turnsByTask = null, now, tailIds, onShowIntervals, nameOf, cards,
  initialExpandedKey = null, expandedKeyRef = null,
}: ProdViewProps & { nameOf: (id: string) => string | null; cards: ProdCard[] }): ReactElement {
  // У шага проекта в данных нет — берём проект эпика, иначе переход уводит в личный «Календарь».
  const projectId = run.epic?.projectId ?? undefined;
  // «Сейчас» для живых ходов: вызывающего, иначе — фиксированный на монтирование.
  const [mountNow] = useState(() => Date.now());
  const nowMs = now ?? mountNow;
  // Одна раскрытая карточка за раз (тот же порядок, что у строк этапа): свёртка обязана знать
  // раскрытую карточку — она не прячется никогда. Ключ — stepRef (без stepRef — id карточки;
  // такая карточка свёрткой не защищена: у неё нет ключа шага). Начальное значение —
  // из восстановления по чипу (GC-F11): свежая лента после раскрытия этапа встаёт сразу
  // с раскрытой карточкой, без мигания.
  const [expandedKey, setExpandedKey] = useState<string | null>(initialExpandedKey ?? null);
  // «Показать все» живёт, пока лента смонтирована (до ухода со страницы).
  const [showAll, setShowAll] = useState(false);
  // Ref вызывающего отражает текущее раскрытие (снимок берётся в момент перехода;
  // состояние по-прежнему живёт здесь).
  const toggleKey = (k: string): void => {
    setExpandedKey(cur => {
      const next = cur === k ? null : k;
      if (expandedKeyRef) expandedKeyRef.current = next;
      return next;
    });
  };
  const stageRef = useRef<HTMLDivElement | null>(null);
  // Применение начальной карточки (GC-F11): и при монтировании (свежая лента), и при
  // смене пропа (живая лента): раскрыть + фокус на шапку карточки (её PressableArea).
  // Ключ — stepRef или card-N — безопасные символы для атрибутного селектора.
  useEffect(() => {
    if (!initialExpandedKey) return;
    setExpandedKey(initialExpandedKey);
    if (expandedKeyRef) expandedKeyRef.current = initialExpandedKey;
    const el = stageRef.current?.querySelector(`[data-testid="prod-card-${initialExpandedKey}"]`);
    if (el && typeof (el as HTMLElement).focus === 'function') (el as HTMLElement).focus();
  }, [initialExpandedKey, expandedKeyRef]);
  // Лента ушла (этап схлопнут) — раскрытой карточки нет: снимок не должен нести старую.
  useEffect(() => () => { if (expandedKeyRef) expandedKeyRef.current = null; }, [expandedKeyRef]);

  // Цепочка по коду шага — для статуса и пункта меню «задача».
  const chainByRef = new Map<string, FeatureStepChain>();
  for (const ch of run.stepChains ?? []) if (ch.stepRef) chainByRef.set(ch.stepRef, ch);
  const prodExecutors = (run.stages ?? []).find(s => s.key === 'prod')?.executors ?? [];
  // Задачи ленты — для блока деталей (приёмка, свидетельства, роль задачи для вида хода).
  const taskMap = buildTaskMap(run);
  const tails = tailIds ?? new Set(run.postMergeTails ?? []);
  const fold = foldProd(cards, expandedKey, showAll);

  return (
    <div ref={stageRef} data-testid="prod-stage" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <StageSummary run={run} cards={cards} />
      {/* Кнопка свёртки — над лентой (макет). Её N = visible + hiddenDone: равен тому, что
          откроется; без скрытых кнопки нет. */}
      {fold.hiddenDone.length > 0 && (
        <Button
          variant="ghost"
          size="md"
          fullWidth
          dataTestId="prod-showall"
          onClick={() => setShowAll(true)}
          style={{
            border: `1px solid ${C.border}`, background: C.bgSelected,
            borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
            fontSize: FS.sm, color: C.textHeading,
          }}
        >
          {`Показать все ${stepWord(fold.visible.length + fold.hiddenDone.length)} (скрыто готовых: ${fold.hiddenDone.length})`}
        </Button>
      )}
      <div data-testid="prod-lane" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {fold.visible.map(card => {
          const chain = chainByRef.get(card.stepRef);
          const key = card.stepRef || `card-${card.order}`;
          // Хвост после мержа: задача цепочки из бэкового списка.
          const isTail = !!chain && chain.taskIds.some(id => tails.has(id));
          return (
            <Fragment key={key}>
              {card.isCurrent && (
                <div data-testid="prod-nowline" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, fontWeight: 700, color: C.accent }}>
                  <span>сейчас</span>
                  <span style={{ flex: 1, height: 1, background: C.accentMuted }} />
                </div>
              )}
              <ProdCardView card={card} chain={chain} executors={prodExecutors} featureId={featureId} featureTitle={featureTitle} projectId={projectId} marks={marks} isMobile={isMobile} nameOf={nameOf}
                expanded={expandedKey === key} onToggle={() => toggleKey(key)}
                taskMap={taskMap} axis={axis} turnsByTask={turnsByTask} now={nowMs} isTail={isTail} onShowIntervals={onShowIntervals} />
            </Fragment>
          );
        })}
      </div>
    </div>
  );
}
