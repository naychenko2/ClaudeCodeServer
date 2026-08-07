import { useMemo, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { TIER_ORDER, type TierKey } from '../../lib/modelProvidersShared';
import { RoutePicker } from '../../components/RoutePicker';
import { presetRoute, routeDisplayLabel, usePresets } from '../../lib/presets';
import {
  ANY_SPECIALTY, effectiveSpecialtyRecord, specialtyLabel, useSpecialtyCatalog, withTierCell,
} from '../../lib/specialties';
import { C, FS, R, SP } from '../../lib/design';
import type { ModelOption } from '../../lib/models';
import type { SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtySettingsResponse, SpecialtyTemplateSettings } from '../../types';

// Свёрнутый блок «Исключения» внизу вкладки «Модели по умолчанию» (макет models-spend-v3.html §2):
// бывшая вкладка «Специальности», ужатая в раскрываемый блок. Свёрнуто — бейдж с числом
// настроенных специальностей и подсказка; пусто — «Исключений нет · настроить». Раскрытие —
// матрица специальность × уровень с фильтром «С настройками» и сегментом «Для всех / Только для меня».

interface Props {
  settings: SpecialtySettingsResponse | null;
  isAdmin: boolean;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
}

export function ExceptionsBlock({ settings, isAdmin, models, tierModels, ollamaModel, savingScope, onSaveLayer }: Props) {
  const catalog = useSpecialtyCatalog();
  const presets = usePresets();
  const [open, setOpen] = useState(false);
  // «Для всех» правит только админ; обычный пользователь — только «Только для меня»
  const [scope, setScope] = useState<'global' | 'owner'>(isAdmin ? 'global' : 'owner');
  const [filter, setFilter] = useState<'all' | 'configured'>('all');

  const labelCtx = { tierModels, ollamaModel };

  // Каталог без «нет специальности» + «Любая» первой
  const rows: SpecialtyCatalogEntry[] = useMemo(() => {
    if (!catalog) return [];
    return catalog.filter(e => e.key !== 'none');
  }, [catalog]);

  // Заполнена ли запись (есть хотя бы одна ячейка уровня)
  const recFilled = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
    !!(rec && (rec.tierStrong || rec.tierMedium || rec.tierWeak));

  // Число исключений: специальности с заполненными ячейками в любом слое + «Любая»
  const configuredCount = useMemo(() => {
    if (!settings) return 0;
    let n = 0;
    if (recFilled(settings.global.defaultSpecialty) || recFilled(settings.owner.defaultSpecialty)) n++;
    for (const e of rows) {
      if (recFilled(settings.global.specialties[e.key]) || recFilled(settings.owner.specialties[e.key])) n++;
    }
    return n;
  }, [settings, rows]);

  const total = rows.length + 1; // + «Любая»

  // Слой, который правим в выбранном scope
  const layer = settings ? (scope === 'global' ? settings.global : settings.owner) : null;
  const canEdit = scope === 'owner' || isAdmin;

  const cellOf = (rec: SpecialtyTemplateSettings | null | undefined, t: TierKey): string =>
    (t === 'strong' ? rec?.tierStrong : t === 'medium' ? rec?.tierMedium : rec?.tierWeak) ?? '';

  const setCell = (key: string, t: TierKey, value: string) => {
    if (!layer || !settings) return;
    const template = rows.find(e => e.key === key)?.template ?? null;
    const next = withTierCell(layer, key, t, value, template);
    onSaveLayer(scope, next);
  };

  // inline-сборка цепочки в ячейке матрицы: PresetOptions отдаёт СВЕЖИЙ слой (клон +
  // новый пресет, ещё не сохранён) — дописываем ячейку на ТОМ ЖЕ объекте и сохраняем
  // ОДНИМ onSaveLayer. Раздельные PUT (создать пресет, затем отдельно — ячейку) гонятся
  // по одному слою: второй ответ побеждает первый и стирает только что созданный пресет
  // (CRITICAL 1, ревью 65d8df66 — «Исключения» теряли цепочку на каждый клик «Сохранить»)
  const onPresetCreated = (key: string, t: TierKey, presetId: string,
    presetScope: 'global' | 'owner', freshLayer: SpecialtySettingsLayer) => {
    const template = rows.find(e => e.key === key)?.template ?? null;
    onSaveLayer(presetScope, withTierCell(freshLayer, key, t, presetRoute(presetId), template));
  };

  // Какие строки показывать по фильтру
  const visibleRows = useMemo(() => {
    if (!settings) return [] as SpecialtyCatalogEntry[];
    if (filter === 'all') return rows;
    return rows.filter(e => {
      const rec = effectiveSpecialtyRecord(settings.global, settings.owner, e.key);
      return recFilled(rec);
    });
  }, [rows, filter, settings]);

  const showAny = filter === 'all' || (settings && recFilled(effectiveSpecialtyRecord(settings.global, settings.owner, ANY_SPECIALTY)));
  const matrixEmpty = !showAny && visibleRows.length === 0;

  // Подпись пустой шапки (свёрнуто)
  const headSub = configuredCount === 0
    ? 'Исключений нет · все специальности живут на маршрутах по умолчанию'
    : `настроено: ${configuredCount} из ${total}`;

  return (
    <div style={{ marginTop: SP.md }}>
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden',
      }}>
        {/* Шапка-переключатель */}
        <div
          onClick={() => configuredCount > 0 && setOpen(o => !o)}
          style={{
            display: 'flex', alignItems: 'center', gap: 10, padding: '10px 13px',
            cursor: configuredCount > 0 ? 'pointer' : 'default',
          }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Исключения</span>
            {configuredCount > 0 && (
              <span style={{
                marginLeft: 6, fontSize: 10.5, fontWeight: 700, padding: '2px 7px', borderRadius: R.max,
                background: C.bgSelected, color: C.textSecondary,
              }}>{configuredCount}</span>
            )}
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 3 }}>{headSub}</div>
          </div>
          {configuredCount === 0 ? (
            <Button size="sm" variant="ghost" onClick={(e) => { e.stopPropagation(); setOpen(true); }}>Настроить</Button>
          ) : (
            <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
              style={{ color: C.textMuted, flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
          )}
        </div>

        {/* Матрица */}
        {open && (
          <div style={{ padding: '0 13px 13px', borderTop: `1px solid ${C.borderLight}` }}>
            {/* Фильтр + сегмент scope */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', margin: '8px 0' }}>
              <FilterChip active={filter === 'all'} onClick={() => setFilter('all')}>Все · {total}</FilterChip>
              <FilterChip active={filter === 'configured'} onClick={() => setFilter('configured')}>
                С настройками · <span style={{ color: C.accent }}>{configuredCount}</span>
              </FilterChip>
              <span style={{ flex: 1 }} />
              <ScopeSeg scope={scope} isAdmin={isAdmin} onPick={setScope} />
            </div>

            {catalog === null || settings === null ? (
              <div style={{ fontSize: FS.sm, color: C.textMuted, padding: '8px 0' }}>Загрузка…</div>
            ) : matrixEmpty ? (
              <div style={{ fontSize: FS.sm, color: C.textMuted, padding: '8px 0' }}>
                Нет специальностей с настройками в выбранном фильтре.
              </div>
            ) : (
              <div style={{ border: `1px solid ${C.border}`, borderRadius: R.xl, overflowX: 'auto', background: C.bgWhite }}>
                <table style={{ width: '100%', borderCollapse: 'separate', borderSpacing: 0, minWidth: 480 }}>
                  <thead>
                    <tr>
                      <Th style={{ width: '30%' }}>Специальность</Th>
                      <Th>Сильная</Th>
                      <Th>Средняя</Th>
                      <Th>Слабая</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {showAny && (
                      <MatrixRow
                        name={specialtyLabel(catalog, ANY_SPECIALTY)}
                        hint="нет специальности"
                        layer={layer} anyKey scope={scope} canEdit={canEdit} savingScope={savingScope}
                        models={models} tierModels={tierModels} ollamaModel={ollamaModel}
                        cellOf={cellOf} presets={presets} labelCtx={labelCtx}
                        settings={settings} onSaveLayer={onSaveLayer}
                        onCell={(t, v) => setCell(ANY_SPECIALTY, t, v)}
                        onPresetCreated={(t, id, s, l) => onPresetCreated(ANY_SPECIALTY, t, id, s, l)}
                      />
                    )}
                    {visibleRows.map(e => {
                      const rec = effectiveSpecialtyRecord(settings.global, settings.owner, e.key);
                      const filled = recFilled(rec);
                      return (
                        <MatrixRow
                          key={e.key}
                          name={specialtyLabel(catalog, e.key)}
                          hint={filled ? (scope === 'owner' ? 'личная' : 'общая') + ' настройка' : undefined}
                          mark={filled}
                          layer={layer} specKey={e.key} scope={scope} canEdit={canEdit} savingScope={savingScope}
                          models={models} tierModels={tierModels} ollamaModel={ollamaModel}
                          cellOf={cellOf} presets={presets} labelCtx={labelCtx}
                          settings={settings} onSaveLayer={onSaveLayer}
                          onCell={(t, v) => setCell(e.key, t, v)}
                          onPresetCreated={(t, id, s, l) => onPresetCreated(e.key, t, id, s, l)}
                        />
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
            <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: 8 }}>
              Исключение перекрывает маршрут для одной специальности. Пустая ячейка — «как у всех».
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function Th({ children, style }: { children: React.ReactNode; style?: React.CSSProperties }) {
  return (
    <th style={{
      fontSize: FS.xs, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.05em',
      textAlign: 'left', padding: '8px 10px', background: C.bgInset, borderBottom: `1px solid ${C.border}`,
      position: 'sticky', top: 0, zIndex: 2, ...style,
    }}>{children}</th>
  );
}

function MatrixRow({ name, hint, mark, layer, anyKey, specKey, scope, canEdit, savingScope, models,
  tierModels, ollamaModel, cellOf, presets, labelCtx, settings, onSaveLayer, onCell, onPresetCreated }: {
  name: string;
  hint?: string;
  mark?: boolean;
  layer: SpecialtySettingsLayer | null;
  anyKey?: boolean;
  specKey?: string;
  scope: 'global' | 'owner';
  canEdit: boolean;
  savingScope: 'global' | 'owner' | null;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  cellOf: (rec: SpecialtyTemplateSettings | null | undefined, t: TierKey) => string;
  presets: ReturnType<typeof usePresets>;
  labelCtx: { tierModels: Record<TierKey, string>; ollamaModel?: string };
  settings: SpecialtySettingsResponse;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
  onCell: (t: TierKey, v: string) => void;
  onPresetCreated: (t: TierKey, presetId: string, presetScope: 'global' | 'owner', layer: SpecialtySettingsLayer) => void;
}) {
  const rec = anyKey ? layer?.defaultSpecialty : (specKey ? layer?.specialties[specKey] : null);
  return (
    <tr>
      <td style={{ padding: '6px 10px', borderBottom: `1px solid ${C.borderLight}`, verticalAlign: 'middle' }}>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>
          {name} {mark && <span style={{ color: C.accent }}>●</span>}
        </div>
        {hint && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</div>}
      </td>
      {TIER_ORDER.map(t => {
        const value = cellOf(rec, t);
        return (
          <td key={t} style={{ padding: '6px 10px', borderBottom: `1px solid ${C.borderLight}`, verticalAlign: 'middle' }}>
            <RoutePicker
              route={value}
              label={value ? routeDisplayLabel(value, presets, labelCtx) : ''}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              showTiers={false}
              showPresets
              // 'global' ("Для всех") — созданный пресет должен быть виден и валиден
              // ВСЕМ, поэтому и список, и inline-создание ограничены общим слоем; 'owner'
              // не передаём — личная ячейка резолвится и от общих пресетов тоже (MAJOR 3)
              presetScope={scope === 'global' ? 'global' : undefined}
              presetCreation={{
                settings, savingScope, onSaveLayer,
                onCreated: (id, s, l) => onPresetCreated(t, id, s, l),
              }}
              readOnly={!canEdit}
              busy={savingScope === scope}
              cardTitle={`${['Сильная', 'Средняя', 'Слабая'][TIER_ORDER.indexOf(t)]} · ${scope === 'owner' ? 'только для меня' : 'для всех'}`}
              placeholder="— по умолчанию —"
              onChange={v => onCell(t, v)}
            />
          </td>
        );
      })}
    </tr>
  );
}

function FilterChip({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" onClick={onClick} style={{
      font: 'inherit', fontSize: FS.xs, fontWeight: 600, cursor: 'pointer', padding: '4px 10px',
      borderRadius: R.max, border: `1px solid ${active ? C.accentMuted : C.border}`,
      background: active ? C.accentLight : C.bgWhite, color: active ? C.textHeading : C.textSecondary,
    }}>{children}</button>
  );
}

function ScopeSeg({ scope, isAdmin, onPick }: { scope: 'global' | 'owner'; isAdmin: boolean; onPick: (s: 'global' | 'owner') => void }) {
  return (
    <div style={{ display: 'inline-flex', gap: 2, background: C.bgSelected, borderRadius: R.lg, padding: 2 }}>
      {isAdmin && <SegBtn active={scope === 'global'} onClick={() => onPick('global')}>Для всех</SegBtn>}
      <SegBtn active={scope === 'owner'} onClick={() => onPick('owner')}>Только для меня</SegBtn>
    </div>
  );
}

function SegBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" onClick={onClick} style={{
      font: 'inherit', fontSize: FS.xs, fontWeight: 600, cursor: 'pointer', border: 'none', borderRadius: R.md,
      padding: '5px 11px', background: active ? C.bgWhite : 'transparent',
      color: active ? C.textHeading : C.textSecondary, boxShadow: active ? 'var(--shadow-card)' : 'none',
    }}>{children}</button>
  );
}
