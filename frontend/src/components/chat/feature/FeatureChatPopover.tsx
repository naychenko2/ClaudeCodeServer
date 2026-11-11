import { C, FONT, FS, SP } from '../../../lib/design';
import { Badge, Button, Menu, Modal } from '../../ui';
import { FeatureTrackerBar, trackerBarAria } from './FeatureTrackerBar';
import type { FeatureChatCardModel } from './featureChatCardModel';
import type { TruthBar } from './featureTruthModel';

// FP-C2 · поповер (1280) / шторка (360) строки фичи чата — вариант А (записка
// docs/mockups/chat-feature-fit-v1-proposal.md, §3 «А» «Под рукой»/«360», §5 строки
// «Поповер (1280)», «360: шторка», абзац «Состояния»; кадры a-wait-*-open.png).
// ЧИСТО ПРЕЗЕНТАЦИОННЫЙ слой: модель (featureChatCardModel) и полоса (featureTruthBar)
// приходят пропсами, все переходы — колбэки. Сторов, запросов и маршрутов здесь нет —
// их подключает FP-C3.
//
// 1280: Menu по якорю строки-триггера (minWidth/maxWidth 400): название 13/600, полная
// фраза тоном (имя — <b>), трекер 4 px, счёт mono, ряд Button xs: действие primary ·
// «Ключевой чат» ghost (или Badge «это ключевой чат») · «К фиче» ghost · «ещё N»
// последней. 360: Modal — на мобиле он сам шторка (R.sheet, SHADOW.sheet): те же блоки,
// кнопки на всю ширину с minHeight 40 (цель на тач).
// Закрытие — своё у Menu/Modal: Esc через handleMenuEscape (с возвратом фокуса на якорь)
// и клик по подложке; своей обёртки под Esc здесь нет.
//
// Конкурентность (записка §5 «Состояния», «run ещё грузится»): компонент без своего
// состояния данных — устаревшая модель (action стал null, пока поповер открыт) просто
// не рисует кнопку, пустой bar — FeatureTrackerBar сам не рисуется (нулевых сегментов
// нет). Ничего не роняется и не мигает: следующий рендер с живыми пропсами — живой экран.

// Тон фразы модели → цвет текста — тот же словарь, что у строки карточки (FeatureChatCard).
const TONE_COLOR: Record<'muted' | 'warn' | 'danger', string> = {
  muted: C.textSecondary, warn: C.warningText, danger: C.dangerText,
};

export interface FeatureChatPopoverProps {
  model: FeatureChatCardModel;
  bar: TruthBar;
  isMobile: boolean;
  // Строка-триггер в шапке: Menu позициониется по её rect и возвращает на неё фокус
  // по Escape. null — якоря нет (Menu встал бы в потоке; в продукте якорь есть всегда).
  anchor: HTMLElement | null;
  onClose: () => void;
  // Кнопка действия (подпись — model.action.label). null — действия нет: кнопки нет,
  // даже когда модель её ещё не сняла (решает вызывающий).
  onAction: (() => void) | null;
  // «Ключевой чат». null — вести некуда: это и есть ключевой чат (Badge) или ключевого нет.
  onKeyChat: (() => void) | null;
  // «К фиче ›»: переход и подпись приходят снаружи (с вкладкой возврата знает FP-C3).
  onToFeature: () => void;
  toFeatureLabel: string;
  // «ещё N» — последней кнопкой ряда; null — фича одна.
  more: { count: number; onNext: () => void } | null;
}

export function FeatureChatPopover(props: FeatureChatPopoverProps) {
  const { model, bar, isMobile, anchor, onClose, onAction, onKeyChat, onToFeature, toFeatureLabel, more } = props;

  // Фраза — текст модели как есть (в поповере — полный, без многоточия: за него и платим
  // кликом); её name — <b> внутри текста, приём тот же, что в строке карточки.
  const { label: phraseLabel, text: phraseText, name: phraseName, tone: phraseTone } = model.phrase;
  const at = phraseName ? phraseText.indexOf(phraseName) : -1;
  const phrase = at < 0 || !phraseName
    ? phraseText
    : (
      <>
        {phraseText.slice(0, at)}
        <b style={{ color: C.textHeading, fontWeight: 600 }}>{phraseName}</b>
        {phraseText.slice(at + phraseName.length)}
      </>
    );

  // Числа mono-счёта — жирным, остальное textMuted (тот же приём, что строка карточки).
  const countParts = model.count ? /^(\d+)([\s\S]*)$/.exec(model.count) : null;

  // Кнопки: десктоп — xs из ряда; мобила — на всю ширину с minHeight 40 (тот же порог
  // тач-цели, что кнопки карточки на 360).
  const btnProps = isMobile ? { fullWidth: true, style: { minHeight: 40 } } : {};

  const actionButton = model.action && onAction ? (
    <Button variant="primary" size="xs" dataTestId="feature-popover-action" onClick={onAction} {...btnProps}>
      {model.action.label}
    </Button>
  ) : null;

  // В ключевом чате вместо кнопки — бейдж «это ключевой чат» (Badge без onClick — span);
  // ключевого чата нет (onKeyChat null, isKeyChat false) — control'а нет вовсе.
  const keyControl = model.isKeyChat ? (
    <span data-testid="feature-popover-key-badge">
      <Badge tone="accent" size="xs">это ключевой чат</Badge>
    </span>
  ) : onKeyChat ? (
    <Button
      variant="ghost" size="xs" dataTestId="feature-popover-to-key"
      ariaLabel={`Ключевой чат фичи «${model.title}»`} title="Ключевой чат фичи"
      onClick={onKeyChat} {...btnProps}
    >
      Ключевой чат
    </Button>
  ) : null;

  const toFeatureButton = (
    <Button
      variant="ghost" size="xs" dataTestId="feature-popover-to-feature"
      ariaLabel={`К фиче «${model.title}»`} title={`К фиче «${model.title}»`}
      onClick={onToFeature} {...btnProps}
    >
      {toFeatureLabel}
    </Button>
  );

  const cycleButton = more && more.count > 0 ? (
    <Button
      variant="ghost" size="xs" dataTestId="feature-popover-cycle"
      ariaLabel={`У этого чата ещё ${more.count} фичи — показать следующую`}
      onClick={more.onNext} {...btnProps}
    >
      ещё {more.count}
    </Button>
  ) : null;

  const content = (
    <div data-testid="feature-popover" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <span
        data-testid="feature-popover-title" title={model.title}
        style={{ fontSize: FS.base, fontWeight: 600, color: C.textPrimary, overflowWrap: 'anywhere' }}
      >
        {model.title}
      </span>
      <div
        data-testid="feature-popover-phrase"
        style={{ fontSize: FS.sm, lineHeight: 1.45, color: TONE_COLOR[phraseTone], overflowWrap: 'anywhere' }}
      >
        {phraseLabel ? (
          <>
            <span data-testid="feature-popover-phrase-label" style={{ fontWeight: 600 }}>{phraseLabel}:</span>
            {' '}
          </>
        ) : null}
        {phrase}
      </div>
      {model.showTracker && (
        <FeatureTrackerBar bar={bar} ariaLabel={trackerBarAria(bar)} testId="feature-popover-bar" style={{ width: '100%' }} />
      )}
      {(model.count || model.eta) && (
        <span data-testid="feature-popover-count" style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>
          {model.count && (countParts
            ? <><b style={{ fontWeight: 700, color: C.textHeading }}>{countParts[1]}</b>{countParts[2]}</>
            : model.count)}
          {/* «часы стоят» — C.warningText (тот же признак модели, что в строке карточки). */}
          {model.eta ? <span style={model.etaPaused ? { color: C.warningText } : undefined}>{model.count ? ' · ' : ''}{model.eta}</span> : ''}
        </span>
      )}
      <div
        data-testid="feature-popover-actions"
        style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', flexWrap: 'wrap', gap: isMobile ? SP.xs : SP.sm }}
      >
        {actionButton}
        {keyControl}
        {toFeatureButton}
        {cycleButton}
      </div>
    </div>
  );

  if (isMobile) {
    // Шторку даёт Modal: на мобиле он сам bottom-sheet (R.sheet, SHADOW.sheet, ручка и
    // крестик), Esc и клик по оверлею — его собственные. Своего title не просим: шапка
    // Modal набрана serif-заголовком диалога, а название фичи здесь — блок содержимого.
    return <Modal onClose={onClose}>{content}</Modal>;
  }

  // 1280: Menu по якорю строки-триггера (fixed, порталом в body — шапка чата не обрезала бы
  // absolute); его подложка ловит клик мимо, его handler — Escape с возвратом фокуса на якорь.
  return (
    <Menu onClose={onClose} anchor={anchor?.getBoundingClientRect() ?? undefined} minWidth={400} maxWidth={400}>
      <div style={{ padding: `${SP.md}px ${SP.lg - 2}px` }}>{content}</div>
    </Menu>
  );
}
