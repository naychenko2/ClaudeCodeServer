import { useEffect, useState } from 'react';
import { C, FONT, FS, SP, TB } from '../../../lib/design';
import { useFeaturesIndex, featureOf } from '../../../lib/featuresIndex';
import { useFeatureRuns } from '../../../lib/featureRuns';
import { openChatById } from '../../../lib/openChat';
import { useFeatureBackNav } from '../../../lib/featureBackNav';
import { FEATURE_MAIN_TAB_LABEL, setFeatureMainTab } from '../../../lib/featurePage';
import { returnToFeature } from '../FeatureBackChip';
import { Badge, Button } from '../../ui';
import { FeatureGlyph } from './FeatureGlyphs';
import type { FeatureGlyph as GlyphKind } from './FeatureGlyphs';
import { StatusTag } from '../../feature/FeatureStatusTag';
import { FeatureTrackerBar, trackerBarAria } from './FeatureTrackerBar';
import { featureTruthBar } from './featureTruthModel';
import { featureChatCardModel, type ChatCardGlyph } from './featureChatCardModel';
import { stepProgressFromIndex } from '../../../lib/featureProgress';

// FS-FC · карточка фичи в шапке чата (макет feature-page-simplify-v5-chat-header, §2–§5):
// две строки + полоса трекера, БЕЗ шеврона и раскрытия (FeatureTruthBody в шапке больше
// нет — детали на странице фичи, до неё один клик). Встраивать в ChatPanel — FS-FCi.
//
// 1240 (71 px, у закрытой 65): строка 1 — глиф · название · тег · «6 из 44 · ≈ 2 д» …
// «Ключевой чат» · «К фиче ›»; строка 2 — фраза тоном · действие справа; полоса 4 px.
// 360 (77 px, у закрытой 71): строка 1 — глиф · название · счёт (тега нет: тон фразы
// ниже уже говорит о состоянии); строка 2 — фраза · действие · «К фиче» · «Ключевой»,
// все кнопки minHeight 40 (цель на тач).
//
// Источник правды — БЭК: строка индекса + run чата; слова, счёт, срок, тон — модель
// featureChatCardModel (FS-FCm), компонент ничего не выводит сам. Конкурентность:
// run приходит позже индекса — первый рендер честно деградирует до чисел индекса без
// срока; приход run перекрашивает строку через стор, без мигания кнопок.
//
// «К фиче» вбирает чип FeatureBackChip (FS-FC-chip, §4): есть указатель возврата на эту
// фичу — подпись с вкладкой и returnToFeature; нет — «Ход» через cc-open-feature.
// Инвариант макета: в карточке нет элементов с aria-expanded (ничего не сворачивается).

// Тон модели → форма общего глифа (FeatureGlyphs, те же состояния, что на странице):
// info → «run» (идёт), warning → «wait» (ждёт), danger → «stopped» (стоит),
// success → «done» (завершена: смержена/закрыта — залита C.success, как «ok» в макете).
const GLYPH_KIND: Record<ChatCardGlyph, GlyphKind> = {
  info: 'run', warning: 'wait', danger: 'stopped', success: 'done',
};

const TONE_COLOR: Record<'muted' | 'warn' | 'danger', string> = {
  muted: C.textSecondary, warn: C.warningText, danger: C.dangerText,
};

export interface FeatureChatCardProps {
  sessionId: string;
  projectId: string | null;
  isMobile: boolean;
  // Ждут В ЭТОМ чате: фокус в композере (ChatPanel), не переход.
  onFocusComposer: () => void;
}

// Карточка фичи в слоте featureBar шапки чата. Возвращает null, когда карточки нет
// (чат не принадлежит ни одной фиче) — как FeatureBanner: пустой слот не добавляет ни пикселя.
export function FeatureChatCard({ sessionId, projectId, isMobile, onFocusComposer }: FeatureChatCardProps) {
  // Индекс — строки фич и связь «чат × фича» (стор, один запрос на проект).
  useFeaturesIndex(projectId);
  // Run'ы чата — счёт/срок/полоса; приходят позже индекса (стор, не props).
  const runs = useFeatureRuns(sessionId);
  // Указатель «со страницы фичи → этот чат» — живой стор (useSyncExternalStore):
  // запись после рендера перекрашивает подпись «К фиче» без перемонтирования.
  const featureBack = useFeatureBackNav(sessionId);
  const [selIdx, setSelIdx] = useState(0);
  // Смена чата — сброс выбора: ChatPanel в воркспейсе не перемонтируется (тот же
  // экземпляр на все чаты), иначе после «ещё N» в чате A карточка чата B показала бы
  // его N-ю фичу вместо первой. Тот же щит, что у FeatureBanner.
  useEffect(() => { setSelIdx(0); }, [sessionId]);

  // Ключ карточки: первая фича из featureOf, если не выбрана через «ещё N» (цикл — локальный индекс).
  const entries = featureOf(sessionId);
  const idx = Math.min(selIdx, Math.max(0, entries.length - 1));
  const feature = entries[idx]?.feature;
  if (!feature) return null;

  const run = runs.find(r => r.featureId === feature.featureId) ?? null;
  const model = featureChatCardModel(feature, run, sessionId, isMobile, Date.now());
  const more = Math.max(0, entries.length - 1);

  // Полоса: из run (с inMaster), фолбэк — строка индекса (те же числа без inMaster),
  // как в FeatureBanner. У закрытой модели полосы нет (showTracker=false).
  const sp = run?.stepProgress ?? stepProgressFromIndex(feature);
  const bar = model.showTracker ? featureTruthBar(sp, run?.stepProgress?.inMaster ?? null) : null;

  // «К фиче» = чип возврата карточки (v5 §4): берём указатель, только если он на эту фичу.
  const backHere = featureBack != null && featureBack.featureId === feature.featureId ? featureBack : null;
  const backTabName = backHere ? FEATURE_MAIN_TAB_LABEL[backHere.tab] : FEATURE_MAIN_TAB_LABEL.progress;

  const openFeaturePage = () => {
    // Вкладка ставится явно (v5 §4): карточка всегда ведёт на «Ход», когда возврата нет.
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
  // карточка с «К фиче», а системное «назад» вернёт в прежний чат (v5 §4).
  const onToKey = () => { if (model.keySessionId) void openChatById(model.keySessionId); };

  // Действие тревоги — тот же маршрут, что onAction в FeatureBanner (M2): цель — чат
  // тревоги; «К мержу» ведёт на страницу; ждут в ЭТОМ чате — фокус поля (модель уже
  // сказала «Ответить здесь» и сняла кнопку у стоящего здесь).
  const onAction = () => {
    const action = model.action;
    if (!action) return;
    if (action.toMerge) { openFeaturePage(); return; }
    if (action.sessionId === sessionId) { onFocusComposer(); return; }
    if (action.sessionId) { void openChatById(action.sessionId); return; }
    openFeaturePage();
  };

  // Кнопки на 360 — minHeight 40 в месте вызова (цель на тач); на 1240 — xs (24) из ui-kit.
  const btnStyle = isMobile ? { flexShrink: 0, minHeight: 40 } : { flexShrink: 0 };

  // Фраза: текст модели как есть; её name — <b> внутри текста (поле name модели — для этого).
  const { text: phraseText, name: phraseName, tone: phraseTone } = model.phrase;
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

  // Числаmono-счёта — жирным, остальное textMuted (макет §2). Выделение числа — тот же
  // приём, что «в очереди · N» в FeatureGroupHeader: набор для типографики, не новых слов.
  const countParts = model.count ? /^(\d+)([\s\S]*)$/.exec(model.count) : null;

  const cycleButton = more > 0 ? (
    <Button
      variant="ghost" size="xs" dataTestId="feature-card-cycle"
      ariaLabel={`У этого чата ещё ${more} фичи — показать следующую`}
      onClick={() => setSelIdx(i => (i + 1) % entries.length)}
      style={btnStyle}
    >
      ещё {more}
    </Button>
  ) : null;

  // В ключевом чате вместо кнопки — бейдж «это ключевой чат» (Badge без onClick — span,
  // без aria-expanded). Кнопки нет и когда ключевого чата у фичи нет (вести некуда).
  const keyControl = model.isKeyChat ? (
    <span data-testid="feature-card-key-badge">
      <Badge tone="accent" size="xs">{isMobile ? 'ключевой' : 'это ключевой чат'}</Badge>
    </span>
  ) : model.keySessionId ? (
    <Button
      variant="ghost" size="xs" dataTestId="feature-card-to-key"
      ariaLabel={`Ключевой чат фичи «${model.title}»`} title="Ключевой чат фичи"
      onClick={onToKey} style={btnStyle}
    >
      {isMobile ? 'Ключевой' : 'Ключевой чат'}
    </Button>
  ) : null;

  const toFeatureLabel = isMobile
    ? 'К фиче'
    : backHere ? `‹ К фиче${backHere.showTab ? ` · ${backTabName}` : ''}` : 'К фиче ›';
  const toFeatureButton = (
    <Button
      variant="ghost" size="xs" dataTestId="feature-card-to-feature"
      ariaLabel={`К фиче «${model.title}», вкладка «${backTabName}»`}
      title={`К фиче «${model.title}», вкладка «${backTabName}»`}
      onClick={onToFeature} style={btnStyle}
    >
      {toFeatureLabel}
    </Button>
  );

  return (
    <div
      data-testid="feature-chat-card"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.xxs, minWidth: 0,
        // Нижняя линия шапки переезжает на карточку (v5 §2): поля TB.padX / TB.padXMobile.
        padding: isMobile ? `0 ${TB.padXMobile}px ${SP.sm}px` : `0 ${TB.padX}px 10px`,
        background: C.bgMain, borderBottom: `1px solid ${C.border}`,
      }}
    >
      <div
        data-testid="feature-card-row1"
        style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0, minHeight: isMobile ? 20 : 28 }}
      >
        <span data-testid="feature-card-glyph" style={{ display: 'flex', flexShrink: 0 }}>
          <FeatureGlyph kind={GLYPH_KIND[model.glyph]} size={14} />
        </span>
        {/* Название — гибкий элемент строки (режется первым, как имя файла в шапке):
            полное название в title. */}
        <span
          data-testid="feature-card-title" title={model.title}
          style={{ flex: '0 1 auto', minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', fontSize: FS.base, fontWeight: 600, color: C.textHeading }}
        >
          {model.title}
        </span>
        {/* Тег статуса — только на 1240 (макет §2: на 360 «тон фразы ниже уже говорит
            о состоянии»), тот же, что в шапке страницы v4. */}
        {model.tag && <StatusTag tag={model.tag} testId="feature-card-tag" />}
        {(model.count || model.eta) && (
          <span data-testid="feature-card-count" style={{ flexShrink: 0, fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted, whiteSpace: 'nowrap' }}>
            {model.count && (countParts
              ? <><b style={{ fontWeight: 700, color: C.textHeading }}>{countParts[1]}</b>{countParts[2]}</>
              : model.count)}
            {model.eta ? ` · ${model.eta}` : ''}
          </span>
        )}
        {!isMobile && (
          <>
            <span style={{ flex: '1 1 0', minWidth: 0 }} />
            {cycleButton}
            {keyControl}
            {toFeatureButton}
          </>
        )}
      </div>
      <div
        data-testid="feature-card-row2"
        style={{ display: 'flex', alignItems: 'center', gap: isMobile ? SP.xs : SP.sm, minWidth: 0, minHeight: isMobile ? 40 : 24, fontSize: FS.sm, color: C.textSecondary }}
      >
        <span
          data-testid="feature-card-phrase"
          style={{ flex: '1 1 auto', minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', color: TONE_COLOR[phraseTone] }}
        >
          {phrase}
        </span>
        {model.action && (
          <Button variant="primary" size="xs" dataTestId="feature-card-action" onClick={onAction} style={btnStyle}>
            {model.action.label}
          </Button>
        )}
        {isMobile && (
          <>
            {cycleButton}
            {toFeatureButton}
            {keyControl}
          </>
        )}
      </div>
      {bar && (
        <FeatureTrackerBar bar={bar} ariaLabel={trackerBarAria(bar)} testId="feature-card-bar" style={{ width: '100%' }} />
      )}
    </div>
  );
}
