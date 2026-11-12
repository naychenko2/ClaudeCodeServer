// Секция «Кто работал» вкладки «Разбор» (FS-FR4; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §2 «Кто работал», §5 «4. Кто работал», §7 FS-FP): три FeatureBreakdownTable подряд —
// персоны, модели, роли — без переключателя разреза и меры: время, токены и деньги в одной
// таблице, сортировка фиксированная — по времени (measure="time" + readOnlyMeasure).
// Группы берутся только из stats.breakdown[axis], своих агрегатов нет; пустая ось не
// рисуется. На 360 колонка токенов скрыта (hideTokens); горизонтального переполнения
// секции нет — таблица сама обёрнута в overflowX. Подписи и цвета — те же источники,
// что у «Статистики» (FeatureStatsTab.tsx:277–285): personaGroupLabel/modelGroupLabel/
// roleGroupLabel, цвет персоны — цвет аватара (AGENT_COLORS). FeatureBreakdownBlock
// с его SegmentedControl здесь не используется: у секции нет состояния.

import { SP, FONT } from '../../lib/design';
import { usePersonas } from '../../lib/personas';
import { useModels } from '../../lib/models';
import { AGENT_COLORS } from '../AgentSelector';
import { personaGroupLabel, modelGroupLabel, roleGroupLabel } from './FeatureStatsFormat';
import { FeatureBreakdownTable } from './FeatureBreakdownTable';
import type { Axis } from './FeatureBreakdownModel';
import type { FeatureStats } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureNavCtx } from '../../lib/featureNavMarks';

export interface FeatureReviewWhoProps {
  // Конвейер фичи: чипы «Модели и роли» и живые статусы — таблица читает run сама.
  run: FeatureRun;
  // Статистика фичи: разбивки берутся только из stats.breakdown.
  stats: FeatureStats;
  // Контекст возврата для переходов в чаты групп (проброс без изменений).
  nav?: FeatureNavCtx;
  // На 360 колонка токенов скрыта.
  isMobile?: boolean;
}

// Порядок осей — по записке §5 «4. Кто работал»: персоны → модели → роли.
const AXES: Axis[] = ['personas', 'models', 'roles'];

export function FeatureReviewWho({ run, stats, nav, isMobile = false }: FeatureReviewWhoProps) {
  // Хуки выше любого return: подписи персон нужны всем таблицам, хук не может быть условным.
  const personas = usePersonas();
  useModels(); // реактивность: догрузка каталога моделей меняет подписи (как в «Статистике»)
  const costPartial = stats.spend?.costPartial ?? false;

  return (
    <div data-testid="review-who" style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, fontFamily: FONT.sans }}>
      {AXES.map(axis => {
        const groups = stats.breakdown[axis] ?? [];
        // Пустая разбивка оси — таблицы этой оси нет.
        if (groups.length === 0) return null;
        return (
          <FeatureBreakdownTable
            key={axis}
            groups={groups}
            axis={axis}
            run={run}
            // Мера фиксирована: время; заголовки мер — текст без переключателя.
            measure="time"
            readOnlyMeasure
            hideTokens={isMobile}
            costPartial={costPartial}
            labelOf={key => (axis === 'personas' ? personaGroupLabel(key, personas) : axis === 'roles' ? roleGroupLabel(key) : modelGroupLabel(key))}
            // Чипы персон живут в разрезах моделей и ролей — их подписи всегда по оси персон.
            personaLabelOf={key => personaGroupLabel(key, personas)}
            colorOf={key => {
              const c = personas.find(x => x.id === key)?.avatar?.color;
              return c ? AGENT_COLORS[c] ?? '' : '';
            }}
            nav={nav}
            isMobile={isMobile}
          />
        );
      })}
    </div>
  );
}
