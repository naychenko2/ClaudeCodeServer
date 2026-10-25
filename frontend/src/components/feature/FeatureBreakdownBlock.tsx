// «Кто и на чём работал» — оболочка блока (волна 5, ST-F13; макет
// docs/mockups/feature-stats-breakdown-5.html, вариант C + малое кольцо, записка
// §«Рекомендация»). Шапка: заголовок и два SegmentedControl — разрез
// (персоны/модели/роли) и мера (время/токены/деньги). Мера — состояние оболочки:
// выбранная задаёт центр кольца (десктоп) и сортировку карточек (мобила); если у
// неё нет данных ни у одной видимой группы, действующая мера — время (М1): оболочка
// решает это здесь один раз и передаёт её и кольцу, и карточкам — шапка, кольцо и
// карточки показывают одну и ту же меру; у таблицы
// сортировка своя — по заголовкам колонок (ST-F11). Группы фильтруются по
// hiddenKeys один раз здесь: кольцо, таблица и карточки видят один и тот же
// набор. Арифметика — только из FeatureBreakdownModel (ST-F8); компонент
// ничего не пересчитывает. Строка роли feature и чип «ждёт вас» здесь не
// решаются: hiddenKeys и liveChipText остаются на контрактах ST-F11/ST-F12.
// Детям hiddenKeys не прокидывается: набор групп отфильтрован один раз здесь. На 360
// кольцо 180 px стоит по центру над карточками (решение дизайна).

import { useState, type CSSProperties } from 'react';
import { C, FS, SP, R, FONT, SHADOW } from '../../lib/design';
import { SegmentedControl } from '../ui/Segmented';
import { useIsMobile } from '../../lib/breakpoints';
import { formatDuration } from './FeatureStatsBits';
import { type Axis, type Measure, fewData, measureValue, effectiveMeasure } from './FeatureBreakdownModel';
import { ShareRing } from './ShareRing';
import { FeatureBreakdownTable } from './FeatureBreakdownTable';
import { FeatureBreakdownCards } from './FeatureBreakdownCards';
import type { FeatureBreakdown } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

export interface FeatureBreakdownBlockProps {
  // Разбивка целиком: оболочка сама берёт разрез выбранной оси.
  breakdown: FeatureBreakdown;
  // Часть моделей без цены (spend.costPartial) — плашка рядом с кольцом.
  costPartial: boolean;
  // Run для чипов и живых статусов: дочерние читают его сами.
  run: FeatureRun;
  // Человекочитаемое имя группы — прокидывается в кольцо, таблицу и карточки.
  labelOf?: (key: string) => string;
  // Цвет группы — прокидывается так же.
  colorOf?: (key: string) => string;
  // Ключи, которые не рисуем нигде (роль feature — вопрос к виду).
  hiddenKeys?: string[];
}

const AXIS_WORD: Record<Axis, string> = { personas: 'персоны', models: 'модели', roles: 'роли' };
// Чего должно стать больше одного (пустое состояние зависит от оси, а не только от «исполнителей»).
const AXIS_MORE: Record<Axis, string> = {
  personas: 'исполнителей станет больше одного',
  models: 'моделей станет больше одной',
  roles: 'ролей станет больше одной',
};

// Карточка блока (макет .card): bgCard, borderLight, R.lg, SHADOW.card; на 360 — SP.md.
const CARD: CSSProperties = {
  background: C.bgCard,
  border: `1px solid ${C.borderLight}`,
  borderRadius: R.lg,
  boxShadow: SHADOW.card,
};

// Шапка (макет .bh): заголовок serif, справа переключатели; на узкой ширине — перенос.
const HEAD: CSSProperties = { display: 'flex', gap: SP.md, flexWrap: 'wrap', alignItems: 'center' };
const TITLE: CSSProperties = {
  fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading, flex: 1, minWidth: 0,
};

// Плашка предупреждения (макет .partial): warningBg/warningText.
const PLATE: CSSProperties = {
  background: C.warningBg, color: C.warningText,
  borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
  fontSize: FS.xs, fontFamily: FONT.sans,
};

// Пустой разрез (записка §«Состояния»): пунктирный блок, а не EmptyState.
const EMPTY: CSSProperties = {
  border: `1px dashed ${C.dashed}`, borderRadius: R.lg, padding: `${SP.md}px`,
};

export function FeatureBreakdownBlock({
  breakdown, costPartial, run, labelOf, colorOf, hiddenKeys,
}: FeatureBreakdownBlockProps) {
  // Хуки выше любого return: разрез и мера — состояние оболочки.
  const [axis, setAxis] = useState<Axis>('personas');
  const [measure, setMeasure] = useState<Measure>('time');
  const isMobile = useIsMobile();

  // ОДИН набор групп: hiddenKeys отсекаются здесь один раз — дальше кольцо,
  // таблица и карточки работают с одним и тем же списком.
  const hidden = new Set(hiddenKeys ?? []);
  const visible = (breakdown[axis] ?? []).filter(g => !hidden.has(g.key));

  // «Без расхода»: ни токенов, ни денег ни у одной группы — опции меры
  // приглушены, плашка объясняет причину (состояние записки «без расхода»).
  const tokensAvailable = visible.some(g => measureValue(g, 'tokens') != null);
  const costAvailable = visible.some(g => measureValue(g, 'cost') != null);
  const noCost = !tokensAvailable && !costAvailable;

  // Действующая мера (М1): если у выбранной нет данных ни у одной видимой группы —
  // фолбэк на время. Решается здесь один раз: шапка, кольцо и карточки видят одну
  // меру (раньше кольцо молча фолбэчил внутри себя, а карточки и шапка оставались
  // на выбранной — доли в карточках исчезали).
  const effMeasure: Measure = effectiveMeasure(visible, measure);

  const head = (
    <div data-testid="breakdown-head" style={HEAD}>
      <span data-testid="breakdown-title" style={TITLE}>Кто и на чём работал</span>
      <SegmentedControl
        value={axis}
        onChange={setAxis}
        touch={isMobile}
        options={[
          { value: 'personas', label: 'персоны' },
          { value: 'models', label: 'модели' },
          { value: 'roles', label: 'роли' },
        ]}
      />
      <SegmentedControl
        // Показываем действующую меру, а не сырой выбор. Недоступная мера —
        // disabled (решение дизайна): клик по ней ничего не сбрасывает.
        value={effMeasure}
        onChange={setMeasure}
        touch={isMobile}
        options={[
          { value: 'time', label: 'время' },
          { value: 'tokens', label: 'токены', disabled: !tokensAvailable, title: !tokensAvailable ? 'токены недоступны без учёта расхода' : undefined },
          { value: 'cost', label: 'деньги', disabled: !costAvailable, title: !costAvailable ? 'деньги недоступны без учёта расхода' : undefined },
        ]}
      />
    </div>
  );

  return (
    <div data-testid="feature-breakdown-block" style={{ ...CARD, padding: `${(isMobile ? SP.md : SP.lg)}px` }}>
      {head}
      {fewData(visible) ? (
        // Меньше двух групп: сравнивать нечего — пунктир с объяснением, а не таблица.
        <div data-testid="breakdown-empty" style={{ ...EMPTY, marginTop: SP.md }}>
          <b style={{ display: 'block', fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: SP.xs }}>
            Пока нечего разрезать
          </b>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>
            {visible.length === 1
              ? `Разрезы появятся, когда ${AXIS_MORE[axis]}. Сейчас виден только ${labelOf ? labelOf(visible[0].key) : visible[0].key} — ${formatDuration(visible[0].minutes)}.`
              : `Разрезы появятся, когда ${AXIS_MORE[axis]}.`}
          </span>
        </div>
      ) : isMobile ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.md }}>
          {noCost && <div data-testid="breakdown-no-cost" style={PLATE}>Токены и деньги недоступны: у этой фичи нет учёта расхода. Доли — по времени</div>}
          {costPartial && <div data-testid="breakdown-cost-partial" style={PLATE}>цена по части моделей недоступна</div>}
          {/* Кольцо 180 по центру над карточками (решение дизайна, записка §360). */}
          <div style={{ display: 'flex', justifyContent: 'center' }}>
            <ShareRing
              groups={visible} measure={effMeasure} size={180}
              labelOf={labelOf} colorOf={colorOf} axisLabel={AXIS_WORD[axis]}
            />
          </div>
          <FeatureBreakdownCards
            groups={visible} axis={axis} measure={effMeasure} run={run}
            labelOf={labelOf} colorOf={colorOf}
          />
        </div>
      ) : (
        <div style={{ display: 'flex', gap: SP.lg, alignItems: 'flex-start', marginTop: SP.md }}>
          {/* Колонца кольца: плашки «без расхода» стоят рядом с кольцом (пункт
             задачи). Ширина фиксирована — размер кольца (М2): без неё длинный текст
             плашки растягивал колонку до max-content (~430 px) и ломал раскладку 1280;
             теперь текст переносится внутри колонки. */}
          <div data-testid="breakdown-ring-col" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, width: 140 }}>
            {noCost && <div data-testid="breakdown-no-cost" style={PLATE}>Токены и деньги недоступны: у этой фичи нет учёта расхода. Доли — по времени</div>}
            {costPartial && <div data-testid="breakdown-cost-partial" style={PLATE}>цена по части моделей недоступна</div>}
            <ShareRing
              groups={visible} measure={effMeasure} size={140}
              labelOf={labelOf} colorOf={colorOf} axisLabel={AXIS_WORD[axis]}
            />
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <FeatureBreakdownTable
              groups={visible} axis={axis} run={run}
              labelOf={labelOf} colorOf={colorOf}
            />
          </div>
        </div>
      )}
    </div>
  );
}
