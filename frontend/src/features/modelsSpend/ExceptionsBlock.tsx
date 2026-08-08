import { useMemo, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { TIER_ORDER, type TierKey } from '../../lib/modelProvidersShared';
import { RoutePicker } from '../../components/RoutePicker';
import { routeDisplayLabel, usePresets } from '../../lib/presets';
import {
  ANY_SPECIALTY, mergePresetIntoCell, specialtyLabel, useSpecialtyCatalog, withTierCell,
} from '../../lib/specialties';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { C, FS, R, SP } from '../../lib/design';
import type { ModelOption } from '../../lib/models';
import type {
  ResetResult, SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtySettingsResponse, SpecialtyTemplateSettings,
} from '../../types';
import { ResetConfirmDialog } from './ResetConfirmDialog';

// Свёрнутый блок «Исключения» внизу вкладки «Модели по умолчанию» (макет models-spend-v3.html §2):
// бывшая вкладка «Специальности», ужатая в раскрываемый блок. Свёрнуто — бейдж с числом
// настроенных специальностей и подсказка; пусто — «Исключений нет · настроить». Раскрытие —
// матрица специальность × уровень с фильтром «С настройками» и сегментом «Для всех / Только для меня».
//
// Сброс исключений — серверный (план model-settings-reset.md, шаг 3): возврат наследования — это
// удаление записи слоя, а не обнуление ячеек, поэтому и массовый, и точечный жест идут через
// onReset (POST .../reset/{scope}[?key=]), а не через withTierCell/setCell.

interface Props {
  settings: SpecialtySettingsResponse | null;
  isAdmin: boolean;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => Promise<void>;
  // Перечитать настройки с сервера (getSettings + updateSpecialtySettings) — вызывается
  // после сброса, чтобы матрица и подписи пресетов отражали реальное состояние слоя.
  onReloadSettings: () => Promise<void>;
  // Слой, для которого сейчас идёт reset+reload (держит busy до конца перечитывания,
  // не только до ответа POST) — тот же класс защиты от гонки, что и savingScope.
  resettingScope: 'global' | 'owner' | null;
  // Серверный сброс: без key — весь слой scope, с key — одна специальность (или "any").
  onReset: (scope: 'global' | 'owner', key?: string) => Promise<ResetResult>;
}

export function ExceptionsBlock({
  settings, isAdmin, models, tierModels, ollamaModel, savingScope, onSaveLayer,
  onReloadSettings, resettingScope, onReset,
}: Props) {
  const catalog = useSpecialtyCatalog();
  const presets = usePresets();
  const [open, setOpen] = useState(false);
  // «Для всех» правит только админ; обычный пользователь — только «Только для меня»
  const [scope, setScope] = useState<'global' | 'owner'>(isAdmin ? 'global' : 'owner');
  const [filter, setFilter] = useState<'all' | 'configured'>('all');

  // Диалог массового сброса: preview загружен ДО открытия — числа в тексте настоящие
  const [bulkPreview, setBulkPreview] = useState<ResetResult | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [confirmBusy, setConfirmBusy] = useState(false);
  const [opError, setOpError] = useState<string | null>(null);

  const labelCtx = { tierModels, ollamaModel };
  const opBusy = savingScope !== null || resettingScope !== null;

  // Каталог без «нет специальности» + «Любая» первой
  const rows: SpecialtyCatalogEntry[] = useMemo(() => {
    if (!catalog) return [];
    return catalog.filter(e => e.key !== 'none');
  }, [catalog]);

  // Заполнена ли запись (есть хотя бы одна ячейка уровня)
  const recFilled = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
    !!(rec && (rec.tierStrong || rec.tierMedium || rec.tierWeak));
  // Запись есть, но ни одна ячейка не заполнена — «затеняющая» запись, оставшаяся ради
  // прав (см. `shadowed` в ответе серверного reset). Считается ИЗ ДАННЫХ слоя, а не из
  // ответа reset, иначе маркер жил бы один рендер и исчезал при переоткрытии модалки.
  const recShadowed = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
    rec != null && !recFilled(rec);
  // Есть запись в слое вовсе (заполненная или затеняющая) — по такой можно сбросить
  const recConfigured = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
    rec != null;

  // Слой, который правим в выбранном scope
  const layer = settings ? (scope === 'global' ? settings.global : settings.owner) : null;
  const canEdit = scope === 'owner' || isAdmin;

  // Все счёты — по слою ТЕКУЩЕГО scope, а не кросс-слойно (иначе, например, в «Для всех»
  // пустая личная запись поверх заполненной общей ложно получила бы маркер «без уровней»,
  // хотя показанные ячейки непусты). Ветка «Любая специальность» — тем же предикатом.
  const scopeAnyRec = layer?.defaultSpecialty;
  const scopeConfiguredCount = useMemo(() => {
    if (!layer) return 0;
    let n = recConfigured(scopeAnyRec) ? 1 : 0;
    for (const e of rows) {
      if (recConfigured(layer.specialties[e.key])) n++;
    }
    return n;
  }, [layer, rows, scopeAnyRec]);

  const total = rows.length + 1; // + «Любая»

  const cellOf = (rec: SpecialtyTemplateSettings | null | undefined, t: TierKey): string =>
    (t === 'strong' ? rec?.tierStrong : t === 'medium' ? rec?.tierMedium : rec?.tierWeak) ?? '';

  const setCell = (key: string, t: TierKey, value: string) => {
    if (!layer || !settings) return;
    const template = rows.find(e => e.key === key)?.template ?? null;
    const next = withTierCell(layer, key, t, value, template);
    // catch пустой намеренно: отказ уже показан баннером в ModelsSpendModal, здесь он нужен
    // только чтобы отклонённый промис не всплыл как «Uncaught (in promise)»
    void onSaveLayer(scope, next).catch(() => {});
  };

  // inline-сборка цепочки в ячейке матрицы: PresetOptions отдаёт СВЕЖИЙ слой (клон +
  // новый пресет, ещё не сохранён) — дописываем ячейку на ТОМ ЖЕ объекте и сохраняем
  // ОДНИМ onSaveLayer. Раздельные PUT (создать пресет, затем отдельно — ячейку) гонятся
  // по одному слою: второй ответ побеждает первый и стирает только что созданный пресет
  // (CRITICAL 1, ревью 65d8df66 — «Исключения» теряли цепочку на каждый клик «Сохранить»)
  const onPresetCreated = (key: string, t: TierKey, presetId: string,
    presetScope: 'global' | 'owner', freshLayer: SpecialtySettingsLayer) => {
    const template = rows.find(e => e.key === key)?.template ?? null;
    void onSaveLayer(presetScope, mergePresetIntoCell(freshLayer, key, t, presetId, template))
      .catch(() => {});
  };

  // Точечный жест «Вернуть наследование» в строке — тот же серверный маршрут, что и
  // массовый сброс, с key. НЕ withTierCell: recordOf при отсутствии записи её создаёт,
  // копируя права из эффективного шаблона, а бэкенд при наличии owner-записи на глобальную
  // не смотрит вовсе — фронтовый путь убил бы действующее общее исключение.
  const resetRow = async (key: string) => {
    setOpError(null);
    try {
      const res = await onReset(scope, key);
      const stillShadowed = res.shadowed.includes(key);
      showToast('Исключения', stillShadowed
        ? 'Уровни сняли, но у специальности остались свои права — она по-прежнему перекрывает общие уровни.'
        : 'Вернули к настройке по умолчанию');
      await onReloadSettings();
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось сбросить специальность');
    }
  };

  // Массовый сброс: preview — ДО открытия диалога (числа настоящие), reset — по подтверждению
  const openBulkReset = async () => {
    if (scopeConfiguredCount === 0 || previewLoading || opBusy) return;
    setOpError(null);
    setPreviewLoading(true);
    try {
      const preview = await api.specialties.resetPreview(scope);
      setBulkPreview(preview);
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось получить предпросмотр сброса');
    } finally {
      setPreviewLoading(false);
    }
  };

  const confirmBulkReset = async () => {
    setConfirmBusy(true);
    try {
      const res = await onReset(scope);
      const base = `Вернули к наследованию: ${res.specialties} специальностей, ${res.personas} персон`;
      const body = res.shadowed.length === 0
        ? base
        : `${base}. Свои права сохранили: ${res.shadowed.map(k => specialtyLabel(catalog, k)).join(', ')} — они по-прежнему перекрывают общие уровни.`;
      showToast('Исключения', body);
      setBulkPreview(null);
      await onReloadSettings();
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось сбросить исключения');
      setBulkPreview(null);
    } finally {
      setConfirmBusy(false);
    }
  };

  // Тексты диалога — по текущему scope, числа/имена из preview (TOCTOU с фактическим reset
  // возможен — тост считает по ответу reset, не по preview)
  const dialogText = useMemo(() => {
    if (!bulkPreview) return null;
    if (scope === 'owner') {
      const names = bulkPreview.personaNames.slice(0, 3);
      const rest = bulkPreview.personas - names.length;
      const namesPart = names.length > 0 ? `: ${names.join(', ')}${rest > 0 ? `, и ещё ${rest}` : ''}` : '';
      return {
        title: 'Сбросить свои исключения?',
        body: `Свои модели потеряют ${bulkPreview.specialties} специальностей и ${bulkPreview.personas} персон${namesPart}. Все они снова пойдут моделями по умолчанию.`,
      };
    }
    return {
      title: 'Сбросить общие исключения?',
      body: `Свои модели потеряют ${bulkPreview.specialties} специальностей. Личные настройки персон это не затронет.`,
    };
  }, [bulkPreview, scope]);

  // Какие строки показывать по фильтру — по слою текущего scope
  const visibleRows = useMemo(() => {
    if (!layer) return [] as SpecialtyCatalogEntry[];
    if (filter === 'all') return rows;
    return rows.filter(e => recConfigured(layer.specialties[e.key]));
  }, [rows, filter, layer]);

  const showAny = filter === 'all' || recConfigured(scopeAnyRec);
  const matrixEmpty = !showAny && visibleRows.length === 0;

  // Подпись пустой шапки (свёрнуто) — считается по слою текущего scope
  const headSub = scopeConfiguredCount === 0
    ? 'Ни у одной специальности и персоны нет своих моделей — все работают моделями по умолчанию.'
    : `настроено: ${scopeConfiguredCount} из ${total}`;

  return (
    <div style={{ marginTop: SP.md }}>
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden',
      }}>
        {/* Шапка-переключатель — раскрывается всегда, даже если в слое нет заполненных
            ячеек: затеняющая запись (см. shadowed) иначе была бы недостижима из интерфейса */}
        <div
          onClick={() => setOpen(o => !o)}
          style={{
            display: 'flex', alignItems: 'center', gap: 10, padding: '10px 13px', cursor: 'pointer',
          }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Исключения</span>
            {scopeConfiguredCount > 0 && (
              <span style={{
                marginLeft: 6, fontSize: 10.5, fontWeight: 700, padding: '2px 7px', borderRadius: R.max,
                background: C.bgSelected, color: C.textSecondary,
              }}>{scopeConfiguredCount}</span>
            )}
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 3 }}>{headSub}</div>
          </div>
          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
            style={{ color: C.textMuted, flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
        </div>

        {/* Матрица */}
        {open && (
          <div style={{ padding: '0 13px 13px', borderTop: `1px solid ${C.borderLight}` }}>
            {/* Фильтр + кнопка сброса слоя + сегмент scope */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', margin: '8px 0' }}>
              <FilterChip active={filter === 'all'} onClick={() => setFilter('all')}>Все · {total}</FilterChip>
              <FilterChip active={filter === 'configured'} onClick={() => setFilter('configured')}>
                С настройками · <span style={{ color: C.accent }}>{scopeConfiguredCount}</span>
              </FilterChip>
              <span style={{ flex: 1 }} />
              <Button
                size="sm" variant="ghost" loading={previewLoading}
                disabled={scopeConfiguredCount === 0 || previewLoading || opBusy}
                title={scopeConfiguredCount === 0 ? 'Исключений нет — сбрасывать нечего.' : undefined}
                onClick={openBulkReset}
              >
                Сбросить все исключения
              </Button>
              <ScopeSeg scope={scope} isAdmin={isAdmin} onPick={setScope} />
            </div>

            {opError && (
              <div style={{ fontSize: FS.xs, color: C.dangerText, padding: '2px 0 8px' }}>{opError}</div>
            )}

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
                        filled={recFilled(scopeAnyRec)}
                        shadowed={recShadowed(scopeAnyRec)}
                        canReset={canEdit && recConfigured(scopeAnyRec)}
                        resetBusy={opBusy}
                        onResetRow={() => resetRow(ANY_SPECIALTY)}
                        layer={layer} anyKey scope={scope} canEdit={canEdit} savingScope={savingScope} resettingScope={resettingScope}
                        models={models} tierModels={tierModels} ollamaModel={ollamaModel}
                        cellOf={cellOf} presets={presets} labelCtx={labelCtx}
                        settings={settings} onSaveLayer={onSaveLayer}
                        onCell={(t, v) => setCell(ANY_SPECIALTY, t, v)}
                        onPresetCreated={(t, id, s, l) => onPresetCreated(ANY_SPECIALTY, t, id, s, l)}
                      />
                    )}
                    {visibleRows.map(e => {
                      const rec = layer?.specialties[e.key];
                      const filled = recFilled(rec);
                      const shadowed = recShadowed(rec);
                      return (
                        <MatrixRow
                          key={e.key}
                          name={specialtyLabel(catalog, e.key)}
                          hint={shadowed ? `запись без уровней · ${scope === 'owner' ? 'личная' : 'общая'}`
                            : filled ? (scope === 'owner' ? 'личная' : 'общая') + ' настройка' : undefined}
                          filled={filled}
                          shadowed={shadowed}
                          canReset={canEdit && recConfigured(rec)}
                          resetBusy={opBusy}
                          onResetRow={() => resetRow(e.key)}
                          layer={layer} specKey={e.key} scope={scope} canEdit={canEdit} savingScope={savingScope} resettingScope={resettingScope}
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

      <ResetConfirmDialog
        open={dialogText !== null}
        title={dialogText?.title ?? ''}
        body={dialogText?.body ?? ''}
        confirmLabel="Сбросить"
        busy={confirmBusy}
        onCancel={() => setBulkPreview(null)}
        onConfirm={confirmBulkReset}
      />
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

function MatrixRow({ name, hint, filled, shadowed, canReset, resetBusy, onResetRow, layer, anyKey, specKey,
  scope, canEdit, savingScope, resettingScope, models, tierModels, ollamaModel, cellOf, presets, labelCtx,
  settings, onSaveLayer, onCell, onPresetCreated }: {
  name: string;
  hint?: string;
  filled: boolean;
  shadowed: boolean;
  canReset: boolean;
  resetBusy: boolean;
  onResetRow: () => void;
  layer: SpecialtySettingsLayer | null;
  anyKey?: boolean;
  specKey?: string;
  scope: 'global' | 'owner';
  canEdit: boolean;
  savingScope: 'global' | 'owner' | null;
  resettingScope: 'global' | 'owner' | null;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  cellOf: (rec: SpecialtyTemplateSettings | null | undefined, t: TierKey) => string;
  presets: ReturnType<typeof usePresets>;
  labelCtx: { tierModels: Record<TierKey, string>; ollamaModel?: string };
  settings: SpecialtySettingsResponse;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => Promise<void>;
  onCell: (t: TierKey, v: string) => void;
  onPresetCreated: (t: TierKey, presetId: string, presetScope: 'global' | 'owner', layer: SpecialtySettingsLayer) => void;
}) {
  const rec = anyKey ? layer?.defaultSpecialty : (specKey ? layer?.specialties[specKey] : null);
  const cellBusy = savingScope === scope || resettingScope === scope;
  return (
    <tr>
      <td style={{ padding: '6px 10px', borderBottom: `1px solid ${C.borderLight}`, verticalAlign: 'middle' }}>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>
          {name} {(filled || shadowed) && <span style={{ color: filled ? C.accent : C.textMuted }}>●</span>}
        </div>
        {hint && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</div>}
        {canReset && (
          <button
            type="button"
            onClick={onResetRow}
            disabled={resetBusy}
            title="Вернуть наследование"
            style={{
              display: 'block', marginTop: 2, font: 'inherit', fontSize: 11.5, color: C.accent,
              background: 'transparent', border: 'none', padding: 0,
              cursor: resetBusy ? 'default' : 'pointer', textDecoration: 'underline', opacity: resetBusy ? 0.5 : 1,
            }}
          >
            Вернуть наследование
          </button>
        )}
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
              // Блокируем и во время сохранения ячейки, и во время reset+reload этого слоя —
              // иначе клик в этом окне отправит PUT целого старого слоя и воскресит только
              // что удалённые записи, пока тост рапортует об успехе (класс 65d8df66)
              busy={cellBusy}
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
