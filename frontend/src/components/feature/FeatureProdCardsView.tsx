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
//   • подпись статуса — из stepStatusView (FeatureTimelineBits), не из своей эвристики;
//   • «текущий» узел берётся как есть из модели: state 'cur' стоит только на активном
//     действии (работа/доработка), рендер завершённые узлы не перетирает;
//   • «закрыта» задача — из одного источника: статус задачи (task.status === 'done'),
//     как в StepRow/ExecutorRow, а не outcome цепочки.
// Пустого состояния у компонента нет: при пустой ленте он не рисует ничего —
// существующее пустое состояние остаётся у вкладки.

import { Fragment, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, ReactElement } from 'react';
import { MessageCircle, FileText } from 'lucide-react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Menu, MenuItem } from '../ui/Menu';
import { Tooltip } from '../ui/Tooltip';
import { ExecutorAvatar, ModelChip, stepModel, stepStatusView } from './FeatureTimelineBits';
import { buildProdCards, type ProdCard, type ProdNode } from './FeatureProdCards';
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

// Нет данных шага (не начат: ни старта, ни чата, ни задачи) — код остаётся обычным
// текстом без рамки, не кнопкой: открывать нечего.
function StepCode({ card, chain, featureId, featureTitle, marks, isMobile, nameOf }: {
  card: ProdCard; chain: FeatureStepChain | undefined; featureId: string; featureTitle?: string | null; marks: NavMarks; isMobile: boolean;
  nameOf: (id: string) => string | null;
}) {
  const plain: CSSProperties = {
    fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary, flexShrink: 0,
  };
  // «Нет данных шага» — ни старта, ни чата (спецификация, краевые): открывать нечего.
  if (!chain || (!card.startedAt && !card.chatId)) {
    return <span data-testid={`prod-code-plain-${card.stepRef}`} style={plain}>{card.stepRef}</span>;
  }

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
        {card.stepRef}
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

function ProdCardView({ card, chain, executors, featureId, featureTitle, marks, isMobile, nameOf }: {
  card: ProdCard; chain: FeatureStepChain | undefined; executors: FeatureStageExecutor[];
  featureId: string; featureTitle?: string | null; marks: NavMarks; isMobile: boolean;
  nameOf: (id: string) => string | null;
}) {
  const style: CSSProperties = {
    background: C.bgCard,
    border: `1px solid ${card.isCurrent ? C.accentMuted : C.borderLight}`,
    borderRadius: R.xl,
    padding: `${SP.sm}px ${SP.md}px`,
    // Кромка: текущая карточка — акцент, «ждёт ревью» — info. Обе сразу не могут:
    // акцент сильнее и отвечает на «где сейчас».
    boxShadow: card.isCurrent ? `inset 3px 0 0 ${C.accent}` : card.awaitingReview ? `inset 3px 0 0 ${C.info}` : undefined,
  };

  // Статус — ОДНИМ источником: stepStatusView (label и tone вместе), как в StepRow.
  const status = chain ? stepStatusView(chain) : null;
  const statusStyle: CSSProperties =
    status?.tone === 'success' ? { background: C.successBg, color: C.successText } :
    status?.tone === 'active' ? { background: C.bgSelected, color: C.textHeading } :
    { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.border}` };

  // Имя — через тот же резолвер, что и подписи узлов (единый источник, R4d-минор).
  const name = card.personaId ? nameOf(card.personaId) ?? 'персона недоступна' : null;
  const model = stepModel(executors, card.stepRef);

  return (
    <div data-testid={`prod-card-${card.stepRef || 'no-ref'}`} style={style}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <StepCode card={card} chain={chain} featureId={featureId} featureTitle={featureTitle} marks={marks} isMobile={isMobile} nameOf={nameOf} />
        {/* На 360 название уходит на вторую строку (flexBasis 100%), код остаётся первым. */}
        <span
          style={{
            fontWeight: 700, color: C.textHeading,
            flex: isMobile ? '1 1 100%' : '1',
            minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}
        >
          {card.title}
        </span>
        <ExecutorAvatar personaId={card.personaId} />
        {name && <span style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap' }}>{name}</span>}
        {model && <ModelChip label={model.label} full={model.full} isMobile={isMobile} />}
        {card.cycles > 0 && (
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
        )}
        {status?.label && (
          <span
            data-testid="prod-status"
            style={{ ...statusStyle, fontSize: FS.xs, fontWeight: 600, padding: `0 ${SP.xs}px`, borderRadius: R.sm, whiteSpace: 'nowrap', lineHeight: '16px' }}
          >
            {status.label}
          </span>
        )}
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

  // Цепочка по коду шага — для статуса (stepStatusView) и пункта меню «задача».
  const chainByRef = new Map<string, FeatureStepChain>();
  for (const ch of run.stepChains ?? []) if (ch.stepRef) chainByRef.set(ch.stepRef, ch);
  const prodExecutors = (run.stages ?? []).find(s => s.key === 'prod')?.executors ?? [];

  return (
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
  );
}
