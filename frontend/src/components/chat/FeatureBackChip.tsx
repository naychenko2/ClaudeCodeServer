import { C, FONT, FS } from '../../lib/design';
import { ICON_SIZE } from '../ui/icons';
import { BackButton } from '../ui';
import { useFeatureBackNav } from '../../lib/featureBackNav';
import { setFeaturePageTab, FEATURE_TAB_LABEL, FEATURE_TAB_CHIP_LABEL } from '../../lib/featurePage';

// «← к фиче «{название}» · {вкладка}» (Ф-pl-b + Ф9b): чат открыт переходом ИЗ страницы
// фичи — в шапке кнопка возврата на ту же вкладку, с которой ушли («Чаты», «Статистика» …).
// Вкладку запоминает recordFeatureReturn в момент перехода; клик ставит её в стор страницы
// и открывает страницу (cc-open-feature, fromChat=false: крошка ведёт в список, не в чат).
// Нет перехода из фичи → null. useFeatureBackNav — useSyncExternalStore: хук неусловный.
export function FeatureBackChip({ sessionId, isCompact = false }: { sessionId: string; isCompact?: boolean }) {
  const featureBack = useFeatureBackNav(sessionId);
  if (!featureBack) return null;
  return (
    <BackButton
      iconSize={ICON_SIZE.sm}
      style={{ minHeight: isCompact ? 40 : undefined }}
      ariaLabel={`К фиче${featureBack.title ? ` «${featureBack.title}»` : ''}, вкладка «${FEATURE_TAB_LABEL[featureBack.tab]}»`}
      onClick={() => {
        // Вернуться на ТУ ЖЕ вкладку, с которой ушли: ставим явно — вкладку могли сменить,
        // пока страница была скрыта.
        setFeaturePageTab(featureBack.tab);
        window.dispatchEvent(new CustomEvent('cc-open-feature', {
          detail: { featureId: featureBack.featureId, fromChat: false },
        }));
      }}
    >
      <span data-testid="feature-back-chip" style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, whiteSpace: 'nowrap' }}>
        к фиче{featureBack.title ? ` «${featureBack.title}»` : ''}
        <span data-testid="feature-back-tab" style={{ fontWeight: 500, color: C.textMuted }}> · {FEATURE_TAB_CHIP_LABEL[featureBack.tab]}</span>
      </span>
    </BackButton>
  );
}
