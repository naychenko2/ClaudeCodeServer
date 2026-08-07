import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { RotateCcw } from 'lucide-react';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { TIER_ORDER, TIER_TITLE } from '../../lib/modelTiers';
import type { TierKey } from '../../components/modelProvidersShared';
import { RoutePicker } from './RoutePicker';
import { EffectiveLine } from './EffectiveLine';
import { C, FONT, FS, R } from '../../lib/design';
import { usePersonas } from '../../lib/personas';
import { routeDisplayLabel, usePresets, usePreview, type ChainLabelContext } from '../../lib/presets';
import { modelLabel } from '../../lib/models';
import {
  ANY_SPECIALTY, effectiveSpecialtyRecord, specialtyLabel, withDefaultTier, withTierCell,
} from '../../lib/specialties';
import type { ModelOption } from '../../lib/models';
import type {
  ModelTierValue, Persona, SpecialtyCatalogEntry, SpecialtySettingsLayer,
  SpecialtyTemplate, SpecialtyTemplateSettings,
} from '../../types';

// Возможности персоны: полный набор ключей (null у шаблона/персоны = «все»)
const ALL_TOOL_KEYS = ['tasks', 'notes', 'web'];
const TOOL_CHIP: Record<string, string> = { tasks: 'Задачи', notes: 'Заметки', web: 'Веб' };
const ACCESS_LABEL: Record<string, string> = { full: 'Полный', readOnly: 'Только чтение', custom: 'Свой' };

// Подсказки под подписью исполнительских специальностей (подписи приходят из каталога)
const EXECUTOR_HINT: Record<string, string> = {
  executor: 'Берётся за любую работу с правками файлов',
  backendExecutor: 'Серверный код, данные, интеграции',
  frontendExecutor: 'Интерфейс, вёрстка, клиентская логика',
};

// «Новые» специальности волны — бейдж в списке (переименованный executor не считаем новым)
const NEW_KEYS = new Set(['backendExecutor', 'frontendExecutor']);

// Одноразовый баннер о переносе прежних правил (спека, блок 5): закрывается навсегда
const MIGRATION_BANNER_KEY = 'cc-specialties-migration-banner-dismissed';

function sameSet(a: string[] | null, b: string[] | null): boolean {
  const norm = (x: string[] | null) => JSON.stringify([...(x ?? ALL_TOOL_KEYS)].sort());
  return norm(a) === norm(b);
}

// Персона с этой специальностью ушла от шаблона вручную (права/инструменты отличаются)
function personaDeviates(p: Persona, t: SpecialtyTemplate | null): boolean {
  if (!t) return false;
  if ((p.access ?? 'full') !== t.access) return true;
  if (!sameSet(p.tools ?? null, t.tools)) return true;
  const pDis = (p.access ?? 'full') === 'custom' ? [...(p.disallowedTools ?? [])].sort() : [];
  const tDis = t.access === 'custom' ? [...(t.disallowedTools ?? [])].sort() : [];
  return JSON.stringify(pDis) !== JSON.stringify(tDis);
}

// Есть ли у записи заполненные поля итерации 2 (матрица или уровень по умолчанию)
function recordFilled(r: SpecialtyTemplateSettings | null | undefined): boolean {
  return !!(r && (r.tierStrong || r.tierMedium || r.tierWeak || r.defaultTier));
}

const sectionTitleStyle: CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.06em', margin: '10px 2px 2px',
};

const flabelStyle: CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.05em',
};

const selectStyle: CSSProperties = {
  font: 'inherit', fontSize: FS.xs, color: C.textPrimary,
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
  padding: '6px 8px', outline: 'none', width: '100%',
};

// Компактная ссылка-сброс под мини-карточкой значения (глобально/лично)
function ResetLink({ busy, title, onClick }: { busy: boolean; title: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      title={title}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4, alignSelf: 'flex-start',
        font: 'inherit', fontSize: 11.5, color: C.accent, background: 'transparent',
        border: 'none', padding: 0, cursor: busy ? 'default' : 'pointer',
        opacity: busy ? 0.5 : 1, textDecoration: 'underline',
      }}
    >
      <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      Сбросить
    </button>
  );
}

// Личная ячейка уровня специальности. Вынесена в компонент: плейсхолдер пустой ячейки
// берётся из preview-резолва (хук — нельзя в renderMatrix, он вызывается в цикле).
function OwnerTierCell({ specKey, tier: t, value, presets, labelCtx, models, tierModels,
  ollamaModel, busy, fallbackPlaceholder, onChange, onReset }: {
  specKey: string;
  tier: TierKey;
  value: string;
  presets: ReturnType<typeof usePresets>;
  labelCtx: ChainLabelContext;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  busy: boolean;
  fallbackPlaceholder: string;
  onChange: (v: string) => void;
  onReset: () => void;
}) {
  // «Любая специальность» — не ключ каталога, превью её не резолвит (specialtyKey
  // пуст → запроса нет): плейсхолдер остаётся локальной оценкой из слоёв
  const d = usePreview(specKey === ANY_SPECIALTY
    ? { kind: 'specialty', specialtyKey: undefined, tier: t }
    : { kind: 'specialty', specialtyKey: specKey, tier: t });
  // Плейсхолдер по спеке: «Как у всех · {модель}» — фактическое значение из резолва;
  // до ответа (или при его сбое) — локальная оценка из слоёв
  const placeholder = d?.model
    ? `Как у всех · ${modelLabel(d.model)}`
    : fallbackPlaceholder;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
      <RoutePicker
        route={value}
        label={value ? routeDisplayLabel(value, presets, labelCtx) : ''}
        models={models}
        tierModels={tierModels}
        ollamaModel={ollamaModel}
        cardTitle={`${TIER_TITLE[t]} · только для меня`}
        busy={busy}
        placeholder={placeholder}
        showTiers={false}
        showPresets
        onChange={onChange}
      />
      {value && (
        <ResetLink
          busy={busy}
          title="Вернуть «как у всех»"
          onClick={onReset}
        />
      )}
    </div>
  );
}

export function SpecialtiesTab({ catalog, globalLayer, ownerLayer, isAdmin, models,
  tierModels, ollamaModel, savingScope, onSaveLayer }: {
  catalog: SpecialtyCatalogEntry[];
  globalLayer: SpecialtySettingsLayer;
  ownerLayer: SpecialtySettingsLayer;
  isAdmin: boolean;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
}) {
  const personas = usePersonas();
  const presets = usePresets();
  const [openKeys, setOpenKeys] = useState<Set<string>>(() => new Set());
  const [bannerDismissed, setBannerDismissed] = useState(
    () => localStorage.getItem(MIGRATION_BANNER_KEY) === '1');

  const labelCtx: ChainLabelContext = { tierModels, ollamaModel };

  const executors = useMemo(() => catalog.filter(e => e.executorFamily), [catalog]);
  const others = useMemo(() => catalog.filter(e => !e.executorFamily && e.key !== 'none'), [catalog]);

  const toggle = (key: string) => setOpenKeys(prev => {
    const next = new Set(prev);
    if (next.has(key)) next.delete(key); else next.add(key);
    return next;
  });

  // Число персон специальности, ушедших от шаблона вручную — для бейджа в карточке
  const manualCount = (key: string, template: SpecialtyTemplate | null): number =>
    personas.filter(p => p.specialty === key && personaDeviates(p, template)).length;

  // Ячейка уровня в записи специальности
  const cellOf = (rec: SpecialtyTemplateSettings | null | undefined, t: TierKey): string =>
    (t === 'strong' ? rec?.tierStrong : t === 'medium' ? rec?.tierMedium : rec?.tierWeak) ?? '';

  // Записать ячейку / уровень по умолчанию в слой (key 'any' — «Любая специальность»)
  const setCell = (scope: 'global' | 'owner', key: string, t: TierKey, value: string,
    template: SpecialtyTemplate | null) => {
    const layer = scope === 'global' ? globalLayer : ownerLayer;
    onSaveLayer(scope, withTierCell(layer, key, t, value, template));
  };
  const setDefTier = (scope: 'global' | 'owner', key: string, value: ModelTierValue | '',
    template: SpecialtyTemplate | null) => {
    const layer = scope === 'global' ? globalLayer : ownerLayer;
    onSaveLayer(scope, withDefaultTier(layer, key, value, template));
  };

  // Подпись пустой личной ячейки: ближайшее фактическое значение ниже по цепочке
  // (owner-запись, если есть, заменяет глобальную ЦЕЛИКОМ — глобальную ячейку предлагаем
  // только когда личной записи нет вовсе; дальше «Любая специальность», затем слот)
  const ownerPlaceholder = (key: string, t: TierKey): string => {
    const ownerRec = key === ANY_SPECIALTY ? ownerLayer.defaultSpecialty : ownerLayer.specialties[key];
    const globalRec = key === ANY_SPECIALTY ? globalLayer.defaultSpecialty : globalLayer.specialties[key];
    const defRec = ownerLayer.defaultSpecialty ?? globalLayer.defaultSpecialty;
    const next = (!ownerRec ? cellOf(globalRec, t) : '') || cellOf(defRec, t) || tierModels[t];
    if (!next) return 'Как у всех';
    return `Как у всех · ${routeDisplayLabel(next, presets, labelCtx)}`;
  };

  // Редактор матрицы + уровня по умолчанию (общий для специальности и «Любой»)
  const renderMatrix = (key: string, template: SpecialtyTemplate | null) => {
    const globalRec = key === ANY_SPECIALTY ? globalLayer.defaultSpecialty : globalLayer.specialties[key];
    const ownerRec = key === ANY_SPECIALTY ? ownerLayer.defaultSpecialty : ownerLayer.specialties[key];
    return (
      <>
        {/* Уровень по умолчанию: каким уровнем работают персоны специальности без своего */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, paddingTop: 10 }}>
          <div style={flabelStyle}>Уровень по умолчанию</div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
              <span style={{ fontSize: 10.5, color: C.textMuted }}>Для всех</span>
              <select
                value={globalRec?.defaultTier ?? ''}
                disabled={!isAdmin || savingScope === 'global'}
                onChange={e => setDefTier('global', key, e.target.value as ModelTierValue | '', template)}
                style={selectStyle}
                aria-label="Уровень по умолчанию для всех"
              >
                <option value="">Не задан</option>
                {TIER_ORDER.map(t => <option key={t} value={t}>{TIER_TITLE[t]}</option>)}
              </select>
            </label>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
              <span style={{ fontSize: 10.5, color: C.textMuted }}>Только для меня</span>
              <select
                value={ownerRec?.defaultTier ?? ''}
                disabled={savingScope === 'owner'}
                onChange={e => setDefTier('owner', key, e.target.value as ModelTierValue | '', template)}
                style={selectStyle}
                aria-label="Уровень по умолчанию только для меня"
              >
                <option value="">
                  {globalRec?.defaultTier ? `Как у всех · ${TIER_TITLE[globalRec.defaultTier]}` : 'Как у всех'}
                </option>
                {TIER_ORDER.map(t => <option key={t} value={t}>{TIER_TITLE[t]}</option>)}
              </select>
            </label>
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
            Каким уровнем работают персоны этой специальности, если у них не задан свой.
          </div>
        </div>

        {/* Модели по уровням: три ячейки, в каждой — модель или пресет */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={flabelStyle}>Модели по уровням</div>
          {TIER_ORDER.map(t => {
            const globalCell = cellOf(globalRec, t);
            const ownerCell = cellOf(ownerRec, t);
            return (
              <div key={t} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
                    <RoutePicker
                      route={globalCell}
                      label={globalCell ? routeDisplayLabel(globalCell, presets, labelCtx) : ''}
                      models={models}
                      tierModels={tierModels}
                      ollamaModel={ollamaModel}
                      cardTitle={`${TIER_TITLE[t]} · для всех`}
                      readOnly={!isAdmin}
                      busy={savingScope === 'global'}
                      placeholder="Не задана — решает место применения"
                      showTiers={false}
                      showPresets
                      onChange={v => setCell('global', key, t, v, template)}
                    />
                    {isAdmin && globalCell && (
                      <ResetLink
                        busy={savingScope === 'global'}
                        title="Убрать модель, общую для всех"
                        onClick={() => setCell('global', key, t, '', template)}
                      />
                    )}
                  </div>
                  <OwnerTierCell
                    specKey={key}
                    tier={t}
                    value={ownerCell}
                    presets={presets}
                    labelCtx={labelCtx}
                    models={models}
                    tierModels={tierModels}
                    ollamaModel={ollamaModel}
                    busy={savingScope === 'owner'}
                    fallbackPlaceholder={ownerPlaceholder(key, t)}
                    onChange={v => setCell('owner', key, t, v, template)}
                    onReset={() => setCell('owner', key, t, '', template)}
                  />
                </div>
                {/* «Любая специальность» не ключ каталога — превью-эндпоинт её не
                    резолвит, строку не показываем */}
                {key !== ANY_SPECIALTY && (
                  <EffectiveLine ctx={{ kind: 'specialty', specialtyKey: key, tier: t }} />
                )}
              </div>
            );
          })}
        </div>
      </>
    );
  };

  const renderRow = (e: SpecialtyCatalogEntry) => {
    const open = openKeys.has(e.key);
    const template = e.template;
    const source = ownerLayer.specialties[e.key] ? 'только для меня'
      : globalLayer.specialties[e.key] ? 'для всех' : 'по умолчанию';
    const manual = manualCount(e.key, template);

    return (
      <div key={e.key} style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        overflow: 'hidden',
      }}>
        <div
          onClick={() => toggle(e.key)}
          style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '11px 13px', cursor: 'pointer' }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 7,
              fontSize: FS.base, fontWeight: 600, color: C.textHeading,
            }}>
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {specialtyLabel(catalog, e.key)}
              </span>
              {NEW_KEYS.has(e.key) && (
                <span style={{
                  fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.max,
                  background: C.infoBg, color: C.info, whiteSpace: 'nowrap', flexShrink: 0,
                }}>новая</span>
              )}
            </div>
            {EXECUTOR_HINT[e.key] && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 1 }}>{EXECUTOR_HINT[e.key]}</div>
            )}
          </div>
          <span style={{
            color: C.textMuted, fontSize: 12, flexShrink: 0,
            transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
          }}>▾</span>
        </div>

        {open && (
          <div style={{
            padding: '0 13px 13px', borderTop: `1px solid ${C.borderLight}`,
            display: 'flex', flexDirection: 'column', gap: 12,
          }}>
            {renderMatrix(e.key, template)}

            {/* Шаблон прав и инструментов: эффективное значение, источник, ручные правки персон */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <div style={flabelStyle}>Шаблон прав и инструментов</div>
              {template ? (
                <>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                    <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
                      Доступ: {ACCESS_LABEL[template.access] ?? template.access}
                    </span>
                    <span style={{ fontSize: FS.xs, color: C.textMuted }}>· {source}</span>
                  </div>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
                    {ALL_TOOL_KEYS.map(k => {
                      const on = template.tools === null || template.tools.includes(k);
                      return (
                        <span key={k} style={{
                          fontSize: 11, padding: '3px 8px', borderRadius: R.max,
                          background: C.bgPanel, border: `1px solid ${on ? C.border : C.dashed}`,
                          color: on ? C.textSecondary : C.textMuted,
                          textDecoration: on ? 'none' : 'line-through',
                        }}>
                          {TOOL_CHIP[k]}
                        </span>
                      );
                    })}
                    {template.access === 'custom' && (template.disallowedTools ?? []).map(d => (
                      <span key={d} style={{
                        fontSize: 11, padding: '3px 8px', borderRadius: R.max,
                        background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, color: C.dangerText,
                      }}>
                        − {d}
                      </span>
                    ))}
                  </div>
                </>
              ) : (
                <div style={{ fontSize: FS.sm, color: C.textMuted }}>
                  Шаблон не задан — права и инструменты выбираются прямо в персоне.
                </div>
              )}
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                {manual > 0 ? (
                  <span style={{
                    fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.max,
                    background: C.warningBg, color: C.warningText,
                  }}>правили вручную: {manual}</span>
                ) : (
                  <span style={{
                    fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.max,
                    background: C.bgSelected, color: C.textSecondary,
                  }}>без ручных правок</span>
                )}
              </div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
                Шаблон подставляется один раз — в момент выбора специальности. Дальше права
                и инструменты живут в персоне: ваши правки не затираются, а изменения шаблона
                на уже созданных персон не переносятся.
              </div>
            </div>
          </div>
        )}
      </div>
    );
  };

  // «Любая специальность» — запись defaultSpecialty слоёв (наследник правила "any" из v1):
  // срабатывает для специальности без своей записи. Только матрица и уровень — права
  // и инструменты к ней не применяются (их бэкенд читает только из записи специальности).
  const anyRecord = effectiveSpecialtyRecord(globalLayer, ownerLayer, ANY_SPECIALTY);
  const anyFilled = recordFilled(anyRecord);
  const anyOpen = openKeys.has(ANY_SPECIALTY);

  // Ни у одной специальности не заданы свои модели — ни глобально, ни лично
  const noMatrices = !recordFilled(globalLayer.defaultSpecialty) && !recordFilled(ownerLayer.defaultSpecialty)
    && !Object.values(globalLayer.specialties).some(recordFilled)
    && !Object.values(ownerLayer.specialties).some(recordFilled);

  // Баннер о переносе прежних правил — только если поля реально заполнены (миграция v1→v2)
  const showMigrationBanner = !bannerDismissed && !noMatrices;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
        У специальности свои сильная, средняя и слабая модели: задаёте один раз —
        работают все её персоны. Персона может поставить своё.
      </div>

      {showMigrationBanner && (
        <div style={{
          background: C.infoBg, border: `1px solid ${C.border}`, borderRadius: R.xl,
          padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 8,
          fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5,
        }}>
          <span>
            Ваши прежние правила перенесены в модели специальностей. Проверьте строку
            «Сейчас пойдёт» — персона с уровнем теперь берёт модель у своей специальности,
            а не общую. Сами пресеты пришлось пересобрать: раньше это были наборы правил,
            теперь — цепочки моделей.
          </span>
          <Button
            variant="ghost" size="sm"
            style={{ alignSelf: 'flex-start' }}
            onClick={() => {
              localStorage.setItem(MIGRATION_BANNER_KEY, '1');
              setBannerDismissed(true);
            }}
          >
            Понятно
          </Button>
        </div>
      )}

      {noMatrices && (
        <div style={{
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          padding: '12px 14px', fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5,
        }}>
          Ни у одной специальности нет своих моделей — все персоны работают моделями
          по умолчанию. Задайте специальности модели по уровням, и все её персоны
          пойдут ими разом.
        </div>
      )}

      {/* «Любая специальность» — применяется, когда у конкретной записи нет */}
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        overflow: 'hidden',
      }}>
        <div
          onClick={() => toggle(ANY_SPECIALTY)}
          style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '11px 13px', cursor: 'pointer' }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>
              Любая специальность
            </div>
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 1 }}>
              {anyFilled
                ? 'Сработает для специальности без своих настроек'
                : 'Срабатывает для специальности без своих настроек'}
            </div>
          </div>
          <span style={{
            color: C.textMuted, fontSize: 12, flexShrink: 0,
            transform: anyOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
          }}>▾</span>
        </div>
        {anyOpen && (
          <div style={{
            padding: '0 13px 13px', borderTop: `1px solid ${C.borderLight}`,
            display: 'flex', flexDirection: 'column', gap: 12,
          }}>
            {renderMatrix(ANY_SPECIALTY, null)}
          </div>
        )}
      </div>

      <div style={sectionTitleStyle}>Исполнители</div>
      {executors.map(renderRow)}

      <div style={sectionTitleStyle}>Остальные ({others.length})</div>
      {others.map(renderRow)}
    </div>
  );
}
