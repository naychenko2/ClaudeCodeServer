import { C, FONT, FS } from '../../lib/design';
import { ICON_SIZE } from '../ui/icons';
import { BackButton } from '../ui';
import { useFeatureBackNav, useFeatureBackNavForTask } from '../../lib/featureBackNav';
import { setFeaturePageTab, FEATURE_TAB_LABEL } from '../../lib/featurePage';

// «← к фиче «{название}» · {вкладка}» (Ф-pl-b + Ф9b): чат открыт переходом ИЗ страницы
// фичи — в шапке кнопка возврата на ту же вкладку, с которой ушли («Чаты», «Статистика» …).
// Вкладку запоминает recordFeatureReturn в момент перехода; клик ставит её в стор страницы
// и открывает страницу (cc-open-feature, fromChat=false: крошка ведёт в список, не в чат).
// Нет перехода из фичи → null. useFeatureBackNav — useSyncExternalStore: хук неусловный.
//
// major 1: из попапа/панели/шторки (showTab=false) — чип БЕЗ вкладки («← к фиче {название}»),
// но возврат ведёт на последнюю использованную вкладку (featureBack.tab).
// minor 1: название вкладки — из ЕДИНОГО словаря FEATURE_TAB_LABEL (чип и полоса не разъезжаются).
// minor 4: длинное название — обрезка с многоточием (min-width:0 + text-overflow), полное
// название в title/aria-label (кнопка может сжиматься: flexShrink:1 + overflow:hidden).
//
// дефект 4c4645e2: тот же чип стоит и на экране открытой задачи (taskId — цель перехода
// {kind:'task'}): тот же компонент, тот же клик (cc-open-feature), без нового стиля.
// Оба хука зовутся безусловно (useSyncExternalStore), чип берётся из того, что совпало.
export function FeatureBackChip({ sessionId, taskId, projectId, isCompact = false }: { sessionId?: string | null; taskId?: string | null; projectId?: string | null; isCompact?: boolean }) {
  // Оба хука — безусловно, без `??` в одном выражении: иначе при совпадении чата
  // второй не вызывается, и React роняет рендер («fewer hooks than expected»).
  const chatBack = useFeatureBackNav(sessionId ?? null);
  const taskBack = useFeatureBackNavForTask(taskId ?? null);
  const featureBack = chatBack ?? taskBack;
  // Личный чат (нет проекта): воркспейса нет, запасному переходу некуда вести — чип был бы
  // мёртвым. Не рисуем.
  if (!featureBack || !projectId) return null;
  const tabName = FEATURE_TAB_LABEL[featureBack.tab];
  // Полное название (title/aria-label): название фичи + (если есть вкладка) её имя.
  const fullLabel = `К фиче${featureBack.title ? ` «${featureBack.title}»` : ''}${featureBack.showTab ? `, вкладка «${tabName}»` : ''}`;
  return (
    <BackButton
      iconSize={ICON_SIZE.sm}
      // minor 4: кнопка сжимается (flexShrink:1) и обрезает контент (overflow:hidden),
      // чтобы длинное название не рвало шапку на 360.
      style={{ minHeight: isCompact ? 40 : undefined, flexShrink: 1, overflow: 'hidden' }}
      title={fullLabel}
      ariaLabel={fullLabel}
      onClick={() => {
        // Вернуться на ТУ ЖЕ вкладку, с которой ушли: ставим явно — вкладку могли сменить,
        // пока страница была скрыта.
        setFeaturePageTab(featureBack.tab);
        const detail: { featureId: string; fromChat: boolean; handled?: boolean } = { featureId: featureBack.featureId, fromChat: false };
        window.dispatchEvent(new CustomEvent('cc-open-feature', { detail }));
        // Вне проекта (раздел «Чаты») воркспейса нет, и событие никто не принял: открываем
        // проект фичи со страницей фичи через App.
        if (!detail.handled) {
          window.dispatchEvent(new CustomEvent('cc-open-project-feature', { detail: { projectId, featureId: featureBack.featureId, chatId: sessionId } }));
        }
      }}
    >
      <span
        data-testid="feature-back-chip"
        style={{
          fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textSecondary,
          whiteSpace: 'nowrap', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis',
        }}
      >
        к фиче{featureBack.title ? ` «${featureBack.title}»` : ''}
        {featureBack.showTab && (
          <span data-testid="feature-back-tab" style={{ fontWeight: 500, color: C.textMuted }}> · {tabName}</span>
        )}
      </span>
    </BackButton>
  );
}
