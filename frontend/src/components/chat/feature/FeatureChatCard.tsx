import { useEffect, useState, type ReactNode } from 'react';
import { useFeaturesIndex, featureOf } from '../../../lib/featuresIndex';
import { useFeatureRuns } from '../../../lib/featureRuns';
import { openChatById } from '../../../lib/openChat';
import { useFeatureBackNav } from '../../../lib/featureBackNav';
import { setFeatureMainTab } from '../../../lib/featurePage';
import { returnToFeature } from '../FeatureBackChip';
import { Button } from '../../ui';
import { FeatureTrackerBar, trackerBarAria } from './FeatureTrackerBar';
import { featureTruthBar, type TruthBar } from './featureTruthModel';
import { featureChatCardModel } from './featureChatCardModel';
import { FeatureChatSubtitle, MOBILE_ROW_H } from './FeatureChatSubtitle';
import { FeatureChatPopover } from './FeatureChatPopover';
import { stepProgressFromIndex } from '../../../lib/featureProgress';

// FP-C3 · фича как часть шапки чата (макет chat-feature-fit-v1, вариант А; записка
// docs/mockups/chat-feature-fit-v1-proposal.md §3 «А», §5). Отдельной полосы больше нет:
// контейнер отдаёт ШАПКЕ три части, а шапка (ChatHeaderBar) ставит каждую на своё место —
//   subtitle — строка FeatureChatSubtitle на месте подзаголовка героя (вместе с поповером
//              1280 / шторкой 360 — состояние «открыт» живёт здесь);
//   edge     — кромка-трекер 2 px на ширину колонки шапки (нет у закрытой фичи);
//   alarm    — кнопка действия тревоги в правом кластере (только 1280; на 360 действие
//              живёт в шторке).
// Контракт render-prop: children(parts) зовётся ВСЕГДА, у чата без фичи — с null (шапка
// ровно как без фичи, а дерево не меняется — ChatHeaderBar не перемонтируется).
//
// Источник правды — БЭК: строка индекса + run чата; слова, счёт, срок, тон — модель
// featureChatCardModel, компонент ничего не выводит сам. Конкурентность: run приходит
// позже индекса — первый рендер честно деградирует до чисел индекса без срока; приход run
// дорисовывает срок и трекер через стор, без перемонтирования строки. Смена чата — сброс
// выбора фичи и закрытие поповера.

export interface FeatureHeaderParts {
  subtitle: ReactNode;
  edge: ReactNode;
  alarm: ReactNode;
}

export interface FeatureChatCardProps {
  sessionId: string;
  projectId: string | null;
  isMobile: boolean;
  // Ждут В ЭТОМ чате: фокус в композере (ChatPanel), не переход.
  onFocusComposer: () => void;
  children: (parts: FeatureHeaderParts | null) => ReactNode;
}

const EMPTY_BAR: TruthBar = { percent: 0, segments: [], inMaster: 0, masterDraw: 0 };

export function FeatureChatCard({ sessionId, projectId, isMobile, onFocusComposer, children }: FeatureChatCardProps) {
  // Индекс — строки фич и связь «чат × фича» (стор, один запрос на проект).
  useFeaturesIndex(projectId);
  // Run'ы чата — счёт/срок/полоса; приходят позже индекса (стор, не props).
  const runs = useFeatureRuns(sessionId);
  // Указатель «со страницы фичи → этот чат» — живой стор (useSyncExternalStore):
  // запись после рендера перекрашивает подпись «К фиче» без перемонтирования.
  const featureBack = useFeatureBackNav(sessionId);
  const [selIdx, setSelIdx] = useState(0);
  // Поповер «открыт» хранит id чата, в котором открыт: смена чата на том же экземпляре
  // (ChatPanel в воркспейсе не перемонтируется) закрывает его В ТОМ ЖЕ рендере, без кадра
  // с чужим поповером. Строка-триггер — якорь поповера (state, а не ref: Menu читает rect).
  const [openFor, setOpenFor] = useState<string | null>(null);
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const open = openFor === sessionId;
  // Смена чата — сброс выбора фичи: иначе после «ещё N» в чате A шапка чата B показала бы
  // его N-ю фичу вместо первой. Тот же щит, что у FeatureBanner.
  useEffect(() => { setSelIdx(0); setOpenFor(null); }, [sessionId]);

  // Ключ: первая фича из featureOf, если не выбрана через «ещё N» (цикл — локальный индекс).
  const entries = featureOf(sessionId);
  const idx = Math.min(selIdx, Math.max(0, entries.length - 1));
  const feature = entries[idx]?.feature;
  if (!feature) return <>{children(null)}</>;

  const run = runs.find(r => r.featureId === feature.featureId) ?? null;
  const model = featureChatCardModel(feature, run, sessionId, isMobile, Date.now());
  const more = Math.max(0, entries.length - 1);

  // Полоса: из run (с inMaster), фолбэк — строка индекса (те же числа без inMaster).
  // У закрытой модели полосы нет (showTracker=false).
  const sp = run?.stepProgress ?? stepProgressFromIndex(feature);
  const bar = model.showTracker ? featureTruthBar(sp, run?.stepProgress?.inMaster ?? null) : null;

  // «К фиче» = чип возврата: берём указатель, только если он на эту фичу.
  const backHere = featureBack != null && featureBack.featureId === feature.featureId ? featureBack : null;
  // Имя вкладки — из записи возврата (дефект 762b7c03): то же, что у страницы и чипа
  // («Процесс» у фичи с маршрутом); без указателя имя не показывается вовсе.
  const backTabName = backHere ? backHere.tabLabel : '';

  const openFeaturePage = () => {
    // Вкладка ставится явно: без возврата всегда ведём на «Ход».
    setFeatureMainTab('progress');
    const detail: { featureId: string; fromChatId: string; fromChat: boolean; handled?: boolean } =
      { featureId: feature.featureId, fromChatId: sessionId, fromChat: true };
    window.dispatchEvent(new CustomEvent('cc-open-feature', { detail }));
    // Вне проекта (раздел «Чаты») воркспейса нет, и событие никто не принял: открываем
    // проект фичи со страницей фичи — тот же запасный путь, что у returnToFeature.
    if (!detail.handled) {
      const pid = projectId ?? feature.projectId;
      if (pid) {
        window.dispatchEvent(new CustomEvent('cc-open-project-feature', {
          detail: { projectId: pid, featureId: feature.featureId, chatId: sessionId },
        }));
      }
    }
  };

  const onToFeature = () => {
    if (backHere) { returnToFeature(backHere, projectId ?? feature.projectId ?? '', sessionId); return; }
    openFeaturePage();
  };

  // Ключевой чат: openChatById, указатель возврата не пишем — в ключевом чате своя
  // строка с «К фиче», а системное «назад» вернёт в прежний чат.
  const onToKey = () => { if (model.keySessionId) void openChatById(model.keySessionId); };

  // Действие тревоги — тот же маршрут, что onAction в FeatureBanner (M2): цель — чат
  // тревоги; «К мержу» ведёт на страницу; ждут в ЭТОМ чате — фокус поля.
  const onAction = () => {
    const action = model.action;
    if (!action) return;
    if (action.toMerge) { openFeaturePage(); return; }
    if (action.sessionId === sessionId) { onFocusComposer(); return; }
    if (action.sessionId) { void openChatById(action.sessionId); return; }
    openFeaturePage();
  };

  const close = () => setOpenFor(null);
  // Переход из поповера закрывает его: страница/чат меняются, висящая карточка не нужна.
  const thenClose = (fn: () => void) => () => { close(); fn(); };

  const toFeatureLabel = isMobile
    ? 'К фиче'
    : backHere ? `‹ К фиче${backHere.showTab ? ` · ${backTabName}` : ''}` : 'К фиче ›';

  // Строка + поповер/шторка под одним якорем. Строка стоит ВНУТРИ блока заголовка: на 360 это
  // кнопка переключателя чатов, на 1280 у чата с персоной — ссылка на её карточку
  // (personaCardLink). Клик по строке и по поповеру/шторке (React-события всплывают и через
  // портал) не должен уходить выше — иначе откроется ещё и переключатель / карточка персоны.
  // Клавиши гасим тем же щитом, КРОМЕ Escape и Tab: Escape слушает Modal/Menu на document
  // (stopPropagation React-события обрывает его на корне), а Tab — обход фокуса.
  // Высота слота на 360 — подпись 11 px (16), цель 40 растёт padding'ом, не раздувая тулбар.
  const stop = (e: { stopPropagation: () => void }) => e.stopPropagation();
  const stopKey = (e: { key: string; stopPropagation: () => void }) => {
    if (e.key !== 'Escape' && e.key !== 'Tab') e.stopPropagation();
  };
  const subtitle = (
    <span
      ref={setAnchor}
      data-testid="feature-chat-subtitle-slot"
      onClick={stop}
      onKeyDown={stopKey}
      style={isMobile
        ? { display: 'flex', alignItems: 'flex-start', height: MOBILE_ROW_H, minWidth: 0 }
        : { display: 'inline-flex', minWidth: 0, maxWidth: '100%' }}
    >
      <FeatureChatSubtitle model={model} isMobile={isMobile} expanded={open} onOpen={() => setOpenFor(open ? null : sessionId)} />
      {open && (
        <FeatureChatPopover
          model={model}
          bar={bar ?? EMPTY_BAR}
          isMobile={isMobile}
          anchor={anchor}
          onClose={close}
          onAction={model.action ? thenClose(onAction) : null}
          onKeyChat={!model.isKeyChat && model.keySessionId ? thenClose(onToKey) : null}
          onToFeature={thenClose(onToFeature)}
          toFeatureLabel={toFeatureLabel}
          more={more > 0 ? { count: more, onNext: () => setSelIdx(i => (i + 1) % entries.length) } : null}
        />
      )}
    </span>
  );

  // Кромка шапки: трекер 2 px без зазоров и скругления (вместо линии шапки).
  const edge = bar ? (
    <FeatureTrackerBar
      bar={bar} ariaLabel={trackerBarAria(bar)} testId="feature-card-bar"
      style={{ height: 2, gap: 0, borderRadius: 0, width: '100%' }}
    />
  ) : null;

  const alarm = !isMobile && model.action ? (
    <Button variant="primary" size="xs" dataTestId="feature-card-action" onClick={onAction} style={{ flexShrink: 0 }}>
      {model.action.label}
    </Button>
  ) : null;

  return <>{children({ subtitle, edge, alarm })}</>;
}
