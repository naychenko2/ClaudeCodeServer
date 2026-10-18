import { C, FS, R, SP, FONT } from '../../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { Button } from '../../../components/ui/Button';
import { ArrowRight } from 'lucide-react';
import { useFeature, FLAGS } from '../../../lib/featureFlags';
import { useFeaturesIndex, featureOf } from '../../../lib/featuresIndex';
import { openChatById } from '../../../lib/openChat';
import { openTaskInSection } from '../../../lib/tasks';
import { featureDisplayState } from '../../../lib/chatGroups';
import { FeatureGlyph, type FeatureGlyph as GlyphKind } from './FeatureGlyphs';
import { FeatureRail } from './FeatureRail';
import { dateOnly } from './featureTime';
import type { FeatureIndexFeature, FeatureChatLink, FeatureChatKind } from '../../../types/featureIndex';

// Плашка фичи в чате (макет features-overview.html, .fs; записка
// features-overview-proposal.md, «Общее для всех вариантов: плашка фичи»).
//
// Сценарий: «я открыл чат Дениса и не понимаю, где фича и где главный чат».
// Плашка отвечает: глиф состояния · название фичи · мини-рельса · статус ·
// счётчик · роль ЭТОГО чата в фиче · кнопка перехода в ключевой чат.
//
// Фронт НИЧЕГО не считает: источник правды — индекс фич (lib/featuresIndex.ts,
// бэк считает bucket/attention/rail/summary/ключевой чат). Компонент только
// раскладывает строку фичи в глиф/тон/статус/счётчик/роль.
//
// Показывается под ДВУМЯ флагами: turn-plan-feature-mode (поверхность режима
// «Исполнение фичи») и features-overview (загрузка индекса). Dark launch:
// оба выключены — плашки нет, лента как раньше.
//
// Тон фона (записка Майи): ждёт вас/к мержу — C.infoBg, упала — C.dangerBg,
// закрытая/смержена — C.bgInset (серая, второй план), идёт/доработка — C.bgCard
// с рамкой C.borderLight.

// Тон фона плашки.
type BannerTone = 'wait' | 'stop' | 'closed' | 'run';

// Кнопка справа: 'key' — в ключевой чат (openChatById), 'feature' — открыть
// фичу в трекере (openTaskInSection), когда живого ключевого чата нет.
interface BannerButton {
  label: string;
  kind: 'key' | 'feature';
}

// Модель плашки: чистая функция от строки фичи + связи чата. Тестируется
// без рендера (SSR не нужен для логики).
export interface FeatureBannerModel {
  tone: BannerTone;
  glyph: GlyphKind;
  status: string;
  counter: string;
  // Роль этого чата: «ключевой» (сам ключевой) или «этот чат: Ш2 · бэк».
  role: string;
  isKey: boolean;
  button: BannerButton;
}

// Подпись роли чата по виду связи (для curator/orchestrator без stepRef/lane).
const KIND_LABEL: Record<FeatureChatKind, string> = {
  key: 'ключевой',
  curator: 'куратор',
  orchestrator: 'оркестратор',
  executor: 'исполнитель',
};

// Чистая модель: строка фичи + связь «этот чат × фича» → что рисует плашка.
// sessionId — id текущего чата: «этот чат ключевой» = (sessionId === keySessionId)
// (ADR-031 §1: бэк вид key в chats не отдаёт).
export function featureBannerModel(
  feature: FeatureIndexFeature,
  link: FeatureChatLink,
  sessionId: string,
): FeatureBannerModel {
  const st = featureDisplayState(feature);
  const isKey = feature.keySessionId != null && sessionId === feature.keySessionId;

  // Тон фона: тревога (wait/stop) → цветной, закрыта/смержена → серая, иначе нейтр.
  let tone: BannerTone;
  if (st.alarm === 'wait') tone = 'wait';
  else if (st.alarm === 'stop') tone = 'stop';
  else if (st.kind === 'closed' || st.kind === 'done') tone = 'closed';
  else tone = 'run';

  // Статус: у смерженной — «смержена <дата>» (локальное время, featureTime),
  // у закрытой без мержа — «закрыта», иначе статус из featureDisplayState.
  let status: string;
  if (st.kind === 'done') {
    const d = dateOnly(feature.summary?.mergedAt ?? null);
    status = d ? `смержена ${d}` : 'смержена';
  } else if (st.kind === 'closed') {
    status = 'закрыта';
  } else {
    status = st.status;
  }

  // Счётчик: принятые/все шаги + число возвратов (↺N).
  const counter = feature.loops > 0
    ? `${feature.stepsAccepted}/${feature.stepsTotal} ↺${feature.loops}`
    : `${feature.stepsAccepted}/${feature.stepsTotal}`;

  // Роль этого чата: ключевой — «ключевой», иначе «этот чат: Ш2 · бэк»
  // (stepRef · lane из связи; у curator/orchestrator без шага — роль по виду).
  let role: string;
  if (isKey) {
    role = 'ключевой';
  } else {
    const parts = [link.stepRef, link.lane].filter(Boolean) as string[];
    role = parts.length ? parts.join(' · ') : (KIND_LABEL[link.kind] ?? 'участник');
  }

  // Кнопка: есть живой ключевой чат → «Ключевой чат →» (openChatById, сам
  // переключает проект); живого нет (keySessionId = null) → «открыть фичу»
  // (openTaskInSection на эпик, без перехода в чат).
  const button: BannerButton = feature.keySessionId != null
    ? { label: 'Ключевой чат', kind: 'key' }
    : { label: 'Открыть фичу', kind: 'feature' };

  return { tone, glyph: st.kind, status, counter, role, isKey, button };
}

// Тон фона → стиль подложки/рамки плашки.
function toneStyle(tone: BannerTone): React.CSSProperties {
  switch (tone) {
    case 'wait':
      return { background: C.infoBg, border: '1px solid transparent' };
    case 'stop':
      return { background: C.dangerBg, border: '1px solid transparent' };
    case 'closed':
      return { background: C.bgInset, border: '1px solid transparent' };
    case 'run':
    default:
      return { background: C.bgCard, border: `1px solid ${C.borderLight}` };
  }
}

interface FeatureBannerProps {
  // Id текущего чата (для featureOf и «этот чат ключевой»).
  sessionId: string;
  // Проект чата — область запроса индекса (null — чат вне проектов).
  projectId: string | null;
  isMobile?: boolean;
}

// Плашка фичи в слоте баннеров ChatPanel. Возвращает null, когда плашки нет
// (флаги выключены или чат не принадлежит ни одной фиче).
export function FeatureBanner({ sessionId, projectId, isMobile = false }: FeatureBannerProps) {
  // Гейт: оба флага (режим «Исполнение фичи» + «Обзор фич»).
  const featureMode = useFeature(FLAGS.turnPlanFeatureMode);
  const featuresOverview = useFeature(FLAGS.featuresOverview);
  const enabled = featureMode && featuresOverview;
  // Индекс фич проекта (реактивная подписка; fetch идемпотентен, дедуплицируется).
  useFeaturesIndex(projectId, enabled);
  // Фичи, к которым принадлежит чат; первая — сильнейшая связь.
  const entry = featureOf(sessionId)[0] ?? null;
  if (!enabled || !entry) return null;

  const { feature, link } = entry;
  const model = featureBannerModel(feature, link, sessionId);

  return (
    <div
      role="region"
      aria-label="Фича этого чата"
      data-testid="feature-banner"
      data-banner-tone={model.tone}
      style={{
        ...toneStyle(model.tone),
        display: 'flex',
        alignItems: 'center',
        gap: SP.sm,
        flexWrap: 'wrap',
        borderRadius: R.lg,
        padding: isMobile ? SP.sm : `10px ${SP.lg}px`,
        margin: `${SP.sm}px ${isMobile ? 16 : 16}px 0`,
        boxSizing: 'border-box',
        fontFamily: FONT.sans,
      }}
    >
      {/* Глиф состояния фичи */}
      <FeatureGlyph kind={model.glyph} size={13} />
      {/* Название фичи: одна строка, обрезка многоточием */}
      <span
        title={feature.title}
        style={{
          fontSize: FS.md, fontWeight: 700, color: C.textHeading,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          minWidth: 40, maxWidth: '40%',
        }}
      >
        {feature.title}
      </span>
      {/* Мини-рельса (7 сегментов) */}
      <FeatureRail rail={feature.rail} closed={feature.bucket === 'closed'} />
      {/* Статус */}
      <span style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap' }}>
        {model.status}
      </span>
      {/* Счётчик шагов + возвраты */}
      <span style={{
        fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap',
        fontFamily: FONT.mono, flexShrink: 0,
      }}>
        {model.counter}
      </span>
      {/* Роль этого чата: «ключевой» или «· этот чат: Ш2 · бэк» */}
      <span style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap', flexShrink: 0 }}>
        {model.isKey ? model.role : `· этот чат: ${model.role}`}
      </span>
      {/* Кнопка: в ключевой чат или открыть фичу */}
      <Button
        variant="ghost"
        size="sm"
        style={{ marginLeft: 'auto', flexShrink: 0 }}
        onClick={() => {
          if (model.button.kind === 'key') {
            if (feature.keySessionId) void openChatById(feature.keySessionId);
          } else {
            openTaskInSection({ id: feature.featureId, projectId: feature.projectId ?? undefined });
          }
        }}
      >
        {model.button.label}
        <ArrowRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ marginLeft: 4 }} />
      </Button>
    </div>
  );
}
