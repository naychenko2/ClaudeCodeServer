import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { RotateCcw } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { routeLabel, type TierKey } from '../../components/modelProvidersShared';
import { RoutePicker } from './RoutePicker';
import { C, FONT, FS, R } from '../../lib/design';
import { usePersonas } from '../../lib/personas';
import {
  DEFAULT_GLOBAL_PRESET_ID, DEFAULT_OWNER_PRESET_ID,
  defaultRouteFor, specialtyLabel, withDefaultRoute,
} from '../../lib/specialties';
import type { ModelOption } from '../../lib/models';
import type { Persona, SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtyTemplate } from '../../types';

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

const sectionTitleStyle: CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.06em', margin: '10px 2px 2px',
};

const flabelStyle: CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.05em',
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
  const [openKeys, setOpenKeys] = useState<Set<string>>(() => new Set());

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

  const renderRow = (e: SpecialtyCatalogEntry) => {
    const open = openKeys.has(e.key);
    const globalRoute = defaultRouteFor(globalLayer, DEFAULT_GLOBAL_PRESET_ID, e.key) ?? '';
    const ownerRoute = defaultRouteFor(ownerLayer, DEFAULT_OWNER_PRESET_ID, e.key) ?? '';
    const template = e.template;
    const source = ownerLayer.specialties[e.key] ? 'модель: только для меня'
      : globalLayer.specialties[e.key] ? 'модель: для всех' : 'модель: по умолчанию';
    const manual = manualCount(e.key, template);

    const setRoute = (scope: 'global' | 'owner', route: string) => {
      const presetId = scope === 'global' ? DEFAULT_GLOBAL_PRESET_ID : DEFAULT_OWNER_PRESET_ID;
      const layer = scope === 'global' ? globalLayer : ownerLayer;
      onSaveLayer(scope, withDefaultRoute(layer, presetId, e.key, route));
    };

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
                  fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.full,
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
            {/* Модель по умолчанию: глобально + личное переопределение (по образцу слотов) */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6, paddingTop: 10 }}>
              <div style={flabelStyle}>Модель по умолчанию</div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
                  <RoutePicker
                    route={globalRoute}
                    label={routeLabel(globalRoute, ollamaModel, tierModels)}
                    models={models}
                    tierModels={tierModels}
                    ollamaModel={ollamaModel}
                    cardTitle="Для всех"
                    readOnly={!isAdmin}
                    busy={savingScope === 'global'}
                    placeholder="не задана — работает модель по умолчанию"
                    onChange={r => setRoute('global', r)}
                  />
                  {isAdmin && globalRoute && (
                    <ResetLink
                      busy={savingScope === 'global'}
                      title="Убрать модель, общую для всех"
                      onClick={() => setRoute('global', '')}
                    />
                  )}
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
                  <RoutePicker
                    route={ownerRoute}
                    label={ownerRoute ? routeLabel(ownerRoute, ollamaModel, tierModels) : ''}
                    models={models}
                    tierModels={tierModels}
                    ollamaModel={ollamaModel}
                    cardTitle="Только для меня"
                    busy={savingScope === 'owner'}
                    placeholder={`Как у всех${globalRoute ? ` · ${routeLabel(globalRoute, ollamaModel, tierModels)}` : ''}`}
                    onChange={r => setRoute('owner', r)}
                  />
                  {ownerRoute && (
                    <ResetLink
                      busy={savingScope === 'owner'}
                      title="Вернуть «как у всех»"
                      onClick={() => setRoute('owner', '')}
                    />
                  )}
                </div>
              </div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
                Персона без своей модели работает моделью специальности. Модель,
                выбранная в самой персоне, всегда сильнее.
              </div>
            </div>

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
                          fontSize: 11, padding: '3px 8px', borderRadius: R.full,
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
                        fontSize: 11, padding: '3px 8px', borderRadius: R.full,
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
                    fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.full,
                    background: C.warningBg, color: C.warningText,
                  }}>правили вручную: {manual}</span>
                ) : (
                  <span style={{
                    fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: R.full,
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

  // Ни одной специальности не задана своя модель — ни глобально, ни лично
  const noRoutes = !catalog.some(e =>
    defaultRouteFor(globalLayer, DEFAULT_GLOBAL_PRESET_ID, e.key)
    || defaultRouteFor(ownerLayer, DEFAULT_OWNER_PRESET_ID, e.key));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
        Специальность задаёт персоне модель по умолчанию и стартовый набор прав
        и инструментов — дальше персона правится как обычно.
      </div>

      {noRoutes && (
        <div style={{
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          padding: '12px 14px', fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5,
        }}>
          Пока ни у одной специальности нет своей модели — персоны работают моделью
          по умолчанию. Задайте модель специальности — и все её персоны пойдут ею разом.
        </div>
      )}

      <div style={sectionTitleStyle}>Исполнители</div>
      {executors.map(renderRow)}

      <div style={sectionTitleStyle}>Остальные ({others.length})</div>
      {others.map(renderRow)}
    </div>
  );
}
