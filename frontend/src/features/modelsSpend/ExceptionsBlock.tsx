import { useMemo, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { Button, Dot, InlineSegmented } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { TIERS, TIER_ORDER, routeTier, type TierKey } from '../../lib/modelProvidersShared';
import { RoutePicker } from '../../components/RoutePicker';
import { cellPresetLabel, presetIdOf, routeDisplayLabel, usePresets } from '../../lib/presets';
import {
  ANY_SPECIALTY, effectiveDefaultTier, mergePresetIntoCell, specialtyLabel,
  useSpecialtyCatalog, withDefaultTier, withTierCell,
} from '../../lib/specialties';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { plural } from '../../lib/spend';
import { useIsMobile } from '../../lib/breakpoints';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import type { CSSProperties } from 'react';
import type { ModelOption } from '../../lib/models';
import type {
  ModelTierValue, ResetResult, SpecialtyCatalogEntry, SpecialtySettingsLayer,
  SpecialtySettingsResponse, SpecialtyTemplateSettings,
} from '../../types';
import { ResetConfirmDialog } from './ResetConfirmDialog';

// Склонение счётчиков в текстах сброса — тот же `plural`, что уже режет счётчики в
// KpiRibbon/QuotasTab этой же вкладки, отдельного хелпера не заводим
const specWord = (n: number) => plural(n, 'специальность', 'специальности', 'специальностей');
const personaWord = (n: number) => plural(n, 'персона', 'персоны', 'персон');

// Блок «Особые правила для специальностей» на вкладке «Модели по умолчанию»: бывшая
// вкладка «Специальности», ужатая в раскрываемый блок (по умолчанию раскрыт, D2).
// Свёрнуто — бейдж с числом настроенных специальностей и подсказка; пусто —
// «Особых правил нет · настроить».
//
// Раскрыто — СТРОКИ-АККОРДЕОНЫ (одна специальность = одна строка), а не матрица
// специальность × уровень. Матрица не помещалась в тело модалки: подпись значения в
// RoutePicker идёт nowrap, min-content колонки требовал ~300px вместо расчётных 107, и
// на мобиле «Специальность» уезжала за экран. В строке уровни лежат в гриде
// repeat(3, minmax(0, 1fr)) — minmax(0,…) обязателен, иначе колонка снова запросит
// min-content и болезнь вернётся. Горизонтального скролла в блоке нет нигде.
//
// Сброс исключений — серверный (план model-settings-reset.md, шаг 3): возврат наследования
// — это удаление записи слоя, а не обнуление ячеек, поэтому и массовый, и точечный жест
// идут через onReset (POST .../reset/{scope}[?key=]), а не через withTierCell/setCell.

// Геометрия строки: тач-цель 44 на мобиле / плотные 40 на десктопе. Та же высота у
// скелет-строк загрузки — блок не прыгает, когда данные доедут.
const ROW_H_MOBILE = 44;
const ROW_H_DESKTOP = 40;
// Вертикальный скролл списка — только на десктопе: на мобиле шторка Modal скроллится
// сама, а вложенный скроллер воровал бы жест.
const LIST_MAX_H = 420;

// Hover / нажатие пальцем / focus-visible строки: псевдоклассы inline-стилями не
// выразить (тот же приём, что в IconButton и ROW_CLASS раздела). Фон в inline-стиле
// кнопки НЕ задаём — он победил бы правило по специфичности.
const ROW_CLASS = 'cc-exc-row';
if (typeof document !== 'undefined' && !document.getElementById('cc-exc-row-style')) {
  const el = document.createElement('style');
  el.id = 'cc-exc-row-style';
  el.textContent = `.${ROW_CLASS}:hover,.${ROW_CLASS}:active{background:${C.bgSelected};}`
    + `.${ROW_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};border-color:${C.accent};}`;
  document.head.appendChild(el);
}

// Заполнена ли запись (есть хотя бы одна ячейка уровня или задан уровень по умолчанию).
// Семантика зеркалит бэкенд SpecialtySettingsStore.CarriesTier: носителем настройки
// считается и defaultTier, — иначе запись «только с уровнем» ложно помечалась бы
// затеняющей (запись без уровней) и исчезала из счётчика исключений.
const recFilled = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
  !!(rec && (rec.tierStrong || rec.tierMedium || rec.tierWeak || rec.defaultTier));
// Запись есть, но ни одна ячейка не заполнена — «затеняющая» запись, оставшаяся ради
// прав (см. `shadowed` в ответе серверного reset). Считается ИЗ ДАННЫХ слоя, а не из
// ответа reset, иначе маркер жил бы один рендер и исчезал при переоткрытии модалки.
const recShadowed = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
  rec != null && !recFilled(rec);
// Есть запись в слое вовсе (заполненная или затеняющая) — по такой можно сбросить
const recConfigured = (rec: SpecialtyTemplateSettings | null | undefined): boolean =>
  rec != null;

const cellOf = (rec: SpecialtyTemplateSettings | null | undefined, t: TierKey): string =>
  (t === 'strong' ? rec?.tierStrong : t === 'medium' ? rec?.tierMedium : rec?.tierWeak) ?? '';

// Значение сегмента «Уровень по умолчанию»: 'inherit' — наследование (пустое значение
// на проводе). InlineSegmented принимает только строковые значения, поэтому пустая
// строка живёт под именем.
type TierChoice = ModelTierValue | 'inherit';

interface Props {
  settings: SpecialtySettingsResponse | null;
  isAdmin: boolean;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => Promise<void>;
  // Перечитать настройки с сервера (getSettings + updateSpecialtySettings) — вызывается
  // после сброса, чтобы строки и подписи пресетов отражали реальное состояние слоя.
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
  const isMobile = useIsMobile();
  const catalog = useSpecialtyCatalog();
  const presets = usePresets();
  const [open, setOpen] = useState(true);
  // «Для всех» правит только админ; обычный пользователь — только «Только для меня»
  const [scope, setScope] = useState<'global' | 'owner'>(isAdmin ? 'global' : 'owner');
  const [filter, setFilter] = useState<'all' | 'configured'>('all');
  // Раскрыта не больше одной строки. Внутри RoutePicker живёт несохранённый черновик
  // цепочки (presetEditing) — при закрытии строки он умрёт молча, поэтому закрытие
  // возможно ТОЛЬКО явным нажатием на шапку самой строки. При смене scope/фильтра
  // раскрытие сбрасываем: иначе строка показала бы значения другого слоя.
  const [expandedKey, setExpandedKey] = useState<string | null>(null);

  // Диалог массового сброса: preview загружен ДО открытия — числа в тексте настоящие
  const [bulkPreview, setBulkPreview] = useState<ResetResult | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [confirmBusy, setConfirmBusy] = useState(false);
  const [opError, setOpError] = useState<string | null>(null);

  const labelCtx = { tierModels, ollamaModel };
  const opBusy = savingScope !== null || resettingScope !== null;
  // Слой правится или перечитывается — блокируем ВСЕ контролы строк (пикеры, сегмент
  // уровня, «Вернуть наследование», сами шапки строк): клик в окне reset+reload
  // отправил бы PUT целого старого слоя и воскресил только что удалённые записи,
  // пока тост рапортует об успехе (класс 65d8df66)
  const rowBusy = savingScope === scope || resettingScope === scope;

  // Каталог без «нет специальности» + «Любая» первой
  const rows: SpecialtyCatalogEntry[] = useMemo(() => {
    if (!catalog) return [];
    return catalog.filter(e => e.key !== 'none');
  }, [catalog]);

  // Слой, который правим в выбранном scope
  const layer = settings ? (scope === 'global' ? settings.global : settings.owner) : null;
  const canEdit = scope === 'owner' || isAdmin;

  // Все счёты — по слою ТЕКУЩЕГО scope, а не кросс-слойно (иначе, например, в «Для всех»
  // пустая личная запись поверх заполненной общей ложно получила бы маркер «без уровней»,
  // хотя показанные значения непусты). Ветка «Любая специальность» — тем же предикатом.
  const scopeAnyRec = layer?.defaultSpecialty;
  const configuredKeys = useMemo(() => {
    if (!layer) return [] as string[];
    const keys: string[] = [];
    if (recConfigured(scopeAnyRec)) keys.push(ANY_SPECIALTY);
    for (const e of rows) {
      if (recConfigured(layer.specialties[e.key])) keys.push(e.key);
    }
    return keys;
  }, [layer, rows, scopeAnyRec]);
  const scopeConfiguredCount = configuredKeys.length;

  const total = rows.length + 1; // + «Любая»

  const setCell = (key: string, t: TierKey, value: string) => {
    if (!layer || !settings) return;
    const template = rows.find(e => e.key === key)?.template ?? null;
    const next = withTierCell(layer, key, t, value, template);
    // catch пустой намеренно: отказ уже показан баннером в ModelsSpendModal, здесь он нужен
    // только чтобы отклонённый промис не всплыл как «Uncaught (in promise)»
    void onSaveLayer(scope, next).catch(() => {});
  };

  // Установить/снять уровень по умолчанию специальности (через withDefaultTier):
  // '' — снять до наследования. Запись создаётся с правами из шаблона специальности,
  // если её в слое ещё не было (recordOf копирует эффективный шаблон, иначе «пустая»
  // owner-запись сбросила бы права специальности к дефолту кода).
  const setDefaultTier = (key: string, value: ModelTierValue | '') => {
    if (!layer || !settings) return;
    const template = rows.find(e => e.key === key)?.template ?? null;
    const next = withDefaultTier(layer, key, value, template);
    void onSaveLayer(scope, next).catch(() => {});
  };

  // inline-сборка цепочки в поле уровня: PresetOptions отдаёт СВЕЖИЙ слой (клон +
  // новый пресет, ещё не сохранён) — дописываем значение на ТОМ ЖЕ объекте и сохраняем
  // ОДНИМ onSaveLayer. Раздельные PUT (создать пресет, затем отдельно — значение) гонятся
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
      showToast('Особые правила', stillShadowed
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
      const personaPart = res.personas > 0 ? `, ${res.personas} ${personaWord(res.personas)}` : '';
      const base = `Вернули к наследованию: ${res.specialties} ${specWord(res.specialties)}${personaPart}`;
      const body = res.shadowed.length === 0
        ? base
        : `${base}. Свои права сохранили: ${res.shadowed.map(k => specialtyLabel(catalog, k)).join(', ')} — они по-прежнему перекрывают общие уровни.`;
      showToast('Особые правила', body);
      setBulkPreview(null);
      setExpandedKey(null);
      await onReloadSettings();
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось сбросить особые правила');
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
      // Хвост про персоны — только при M > 0, иначе «и 0 персон» читается как сбой
      const personasPart = bulkPreview.personas > 0
        ? ` и ${bulkPreview.personas} ${personaWord(bulkPreview.personas)}${namesPart}`
        : '';
      return {
        title: 'Сбросить свои особые правила?',
        body: `Свои модели потеряют ${bulkPreview.specialties} ${specWord(bulkPreview.specialties)}${personasPart}. Все они снова пойдут моделями по умолчанию.`,
      };
    }
    return {
      title: 'Сбросить общие особые правила?',
      body: `Свои модели потеряют ${bulkPreview.specialties} ${specWord(bulkPreview.specialties)}. Личные настройки персон это не затронет.`,
    };
  }, [bulkPreview, scope]);

  // Строки списка по фильтру: «Любая специальность» всегда первой
  const listRows = useMemo(() => {
    if (!layer || !catalog) return [] as { key: string; name: string; hint?: string;
      rec: SpecialtyTemplateSettings | null }[];
    const items: { key: string; name: string; hint?: string; rec: SpecialtyTemplateSettings | null }[] = [];
    if (filter === 'all' || recConfigured(scopeAnyRec)) {
      items.push({
        key: ANY_SPECIALTY, name: specialtyLabel(catalog, ANY_SPECIALTY),
        hint: 'когда специальность не задана', rec: scopeAnyRec ?? null,
      });
    }
    for (const e of rows) {
      const rec = layer.specialties[e.key] ?? null;
      if (filter === 'configured' && !recConfigured(rec)) continue;
      items.push({ key: e.key, name: specialtyLabel(catalog, e.key), rec });
    }
    return items;
  }, [layer, catalog, rows, filter, scopeAnyRec]);

  // Однострочная сводка строки: «общая настройка · Сильная — Opus 5 · уровень: Средняя».
  // Перечисляем только заданные уровни — иначе строка превращается в три «— по умолчанию —».
  const rowSummary = (rec: SpecialtyTemplateSettings | null, hint?: string): string => {
    const parts: string[] = hint ? [hint] : [];
    const scopeWord = scope === 'owner' ? 'личная' : 'общая';
    if (!recConfigured(rec)) {
      parts.push('как у всех');
    } else if (recShadowed(rec)) {
      parts.push(`запись без уровней · ${scopeWord}`);
    } else {
      parts.push(`${scopeWord} настройка`);
      for (const t of TIER_ORDER) {
        const v = cellOf(rec, t);
        if (v) parts.push(`${TIERS[t].title} — ${routeDisplayLabel(v, presets, labelCtx)}`);
      }
      if (rec?.defaultTier) parts.push(`уровень: ${TIERS[rec.defaultTier].title}`);
    }
    return parts.join(' · ');
  };

  const pickScope = (s: 'global' | 'owner') => {
    if (s === scope) return;
    setScope(s);
    setExpandedKey(null);
  };

  const pickFilter = (f: 'all' | 'configured') => {
    if (f === filter) return;
    setFilter(f);
    // Компенсация обзорности: если под фильтром «С настройками» осталась одна строка —
    // раскрываем её сразу, иначе список из одного пункта не отвечает на вопрос «что там»
    setExpandedKey(f === 'configured' && configuredKeys.length === 1 ? configuredKeys[0] : null);
  };

  // Подпись шапки (свёрнуто) — считается по слою текущего scope
  const headSub = scopeConfiguredCount === 0
    ? 'Особых правил нет · настроить'
    : isMobile
      ? `${scopeConfiguredCount} из ${total}`
      : `свои модели у ${scopeConfiguredCount} ${specWord(scopeConfiguredCount)} из ${total}`;

  return (
    <div style={{ marginTop: SP.md }}>
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden',
      }}>
        {/* Шапка-переключатель — раскрывается всегда, даже если в слое нет заполненных
            записей: затеняющая запись (см. shadowed) иначе была бы недостижима из интерфейса */}
        <div
          onClick={() => setOpen(o => !o)}
          style={{
            display: 'flex', alignItems: 'center', gap: SP.sm,
            padding: `${SP.sm}px ${SP.md}px`, cursor: 'pointer',
          }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Особые правила для специальностей</span>
            {scopeConfiguredCount > 0 && (
              <span style={{
                marginLeft: 6, fontSize: FS.xs, fontWeight: 700, padding: '2px 7px', borderRadius: R.max,
                background: C.bgSelected, color: C.textSecondary,
              }}>{scopeConfiguredCount}</span>
            )}
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 3 }}>{headSub}</div>
          </div>
          <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
            style={{ color: C.textMuted, flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
        </div>

        {/* Список строк-аккордеонов */}
        {open && (
          <div style={{ padding: `0 ${SP.md}px ${SP.md}px`, borderTop: `1px solid ${C.borderLight}` }}>
            {/* Вводная — НАВЕРХУ: объяснение нужно ДО действий, а не после списка */}
            <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, paddingTop: SP.sm }}>
              {scopeConfiguredCount === 0 && (
                <div>Ни у одной специальности и персоны нет своих моделей — все работают моделями по умолчанию.</div>
              )}
              <div>Особое правило задаёт свои модели одной специальности. Пустое поле — «как у всех».</div>
            </div>

            {/* Тулбар: слой → фильтр → сброс. На мобиле — три строки во всю ширину */}
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
              margin: `${SP.sm}px 0`,
            }}>
              <ScopeSeg scope={scope} isAdmin={isAdmin} isMobile={isMobile} onPick={pickScope} />
              <div style={{
                display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
                flex: isMobile ? '1 1 100%' : undefined,
              }}>
                <FilterChip active={filter === 'all'} onClick={() => pickFilter('all')}>Все · {total}</FilterChip>
                <FilterChip active={filter === 'configured'} onClick={() => pickFilter('configured')}>
                  С настройками · <span style={{ color: C.accent }}>{scopeConfiguredCount}</span>
                </FilterChip>
              </div>
              {!isMobile && <span style={{ flex: 1 }} />}
              <Button
                size="sm" variant="ghost" loading={previewLoading}
                fullWidth={isMobile}
                disabled={scopeConfiguredCount === 0 || previewLoading || opBusy}
                title={scopeConfiguredCount === 0 ? 'Особых правил нет — сбрасывать нечего.' : undefined}
                onClick={openBulkReset}
              >
                {isMobile ? 'Сбросить все' : 'Сбросить все особые правила'}
              </Button>
            </div>

            {opError && (
              <div style={{ fontSize: FS.xs, color: C.dangerText, padding: `2px 0 ${SP.sm}px` }}>{opError}</div>
            )}

            {catalog === null || settings === null ? (
              // Скелет вместо «Загрузка…»: блок занимает ту же высоту, что и готовый список
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.sm}px 0` }}>
                {[0, 1, 2].map(i => (
                  <div key={i} style={{ height: ROW_H_MOBILE, borderRadius: R.md, background: C.bgSelected }} />
                ))}
              </div>
            ) : listRows.length === 0 ? (
              <div style={{
                display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', padding: `${SP.sm}px 0`,
              }}>
                <span style={{ fontSize: FS.sm, color: C.textMuted }}>
                  Ни у одной специальности нет своих моделей в этом слое.
                </span>
                <Button size="sm" variant="ghost" onClick={() => pickFilter('all')}>Показать все</Button>
              </div>
            ) : (
              <div style={{
                border: `1px solid ${C.border}`, borderRadius: R.xl, background: C.bgWhite,
                // overflow hidden держит скругление и запрещает горизонтальный скролл;
                // вертикальный — только на десктопе (см. LIST_MAX_H)
                overflow: 'hidden',
                maxHeight: isMobile ? undefined : LIST_MAX_H,
                overflowY: isMobile ? undefined : 'auto',
              }}>
                {listRows.map((it, i) => (
                  <ExceptionRow
                    key={it.key}
                    name={it.name}
                    summary={rowSummary(it.rec, it.hint)}
                    filled={recFilled(it.rec)}
                    shadowed={recShadowed(it.rec)}
                    rec={it.rec}
                    expanded={expandedKey === it.key}
                    onToggle={() => setExpandedKey(k => (k === it.key ? null : it.key))}
                    // «Любая специальность» отбита более заметной границей — она про
                    // отсутствие специальности, а не про одну из них
                    separator={i === listRows.length - 1 ? null
                      : it.key === ANY_SPECIALTY ? C.border : C.borderLight}
                    isMobile={isMobile}
                    busy={rowBusy}
                    canEdit={canEdit}
                    canReset={canEdit && recConfigured(it.rec)}
                    onResetRow={() => resetRow(it.key)}
                    scope={scope}
                    models={models} tierModels={tierModels} ollamaModel={ollamaModel}
                    presets={presets} labelCtx={labelCtx}
                    settings={settings} savingScope={savingScope} onSaveLayer={onSaveLayer}
                    onCell={(t, v) => setCell(it.key, t, v)}
                    onPresetCreated={(t, id, s, l) => onPresetCreated(it.key, t, id, s, l)}
                    onDefaultTier={v => setDefaultTier(it.key, v)}
                    inherited={effectiveDefaultTier(settings.global, settings.owner, it.key)}
                  />
                ))}
              </div>
            )}
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

// Строка-аккордеон: шапка-кнопка (имя + маркер + сводка + шеврон) и раскрываемое тело
// с тремя полями уровней, сегментом «Уровень по умолчанию» и точечным сбросом.
function ExceptionRow({ name, summary, filled, shadowed, rec, expanded, onToggle, separator, isMobile,
  busy, canEdit, canReset, onResetRow, scope, models, tierModels, ollamaModel, presets, labelCtx,
  settings, savingScope, onSaveLayer, onCell, onPresetCreated, onDefaultTier, inherited }: {
  name: string;
  summary: string;
  filled: boolean;
  shadowed: boolean;
  rec: SpecialtyTemplateSettings | null;
  expanded: boolean;
  onToggle: () => void;
  separator: string | null;
  isMobile: boolean;
  // Слой сохраняется или перечитывается: накрывает и шапку строки, и все контролы тела
  busy: boolean;
  canEdit: boolean;
  canReset: boolean;
  onResetRow: () => void;
  scope: 'global' | 'owner';
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  presets: ReturnType<typeof usePresets>;
  labelCtx: { tierModels: Record<TierKey, string>; ollamaModel?: string };
  settings: SpecialtySettingsResponse;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => Promise<void>;
  onCell: (t: TierKey, v: string) => void;
  onPresetCreated: (t: TierKey, presetId: string, presetScope: 'global' | 'owner', layer: SpecialtySettingsLayer) => void;
  onDefaultTier: (v: ModelTierValue | '') => void;
  // Готовое значение effectiveDefaultTier (resolve по обоим слоям — зеркало бэкенд
  // SpecialtySettingsStore.SpecialtyDefaultTier). Если уровень приходит из ДРУГОГО
  // scope — под сегментом рисуем «сейчас наследуется: Сильная · общая».
  inherited: { tier: ModelTierValue; source: 'owner' | 'global' } | null;
}) {
  // Сводка: одна строка с многоточием на десктопе, две строки на мобиле; полный текст — в title
  const summaryStyle: CSSProperties = {
    fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
    ...(isMobile
      ? { display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }
      : { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }),
  };
  const showInherit = inherited && (inherited.source !== scope || inherited.tier !== rec?.defaultTier);
  // «как у всех» — не настройка, а её отсутствие: активный сегмент нейтральный,
  // акцент загорается только когда уровень действительно задан (accent-дисциплина)
  const tierOptions: { value: TierChoice; label: string; tone?: { bg: string; fg: string } }[] = [
    { value: 'inherit', label: 'как у всех', tone: { bg: C.bgWhite, fg: C.textSecondary } },
    { value: 'strong', label: TIERS.strong.title },
    { value: 'medium', label: TIERS.medium.title },
    { value: 'weak', label: TIERS.weak.title },
  ];

  return (
    <div style={{
      background: expanded ? C.bgSelected : undefined,
      borderBottom: separator ? `1px solid ${separator}` : undefined,
    }}>
      <button
        type="button"
        className={ROW_CLASS}
        onClick={onToggle}
        disabled={busy}
        aria-expanded={expanded}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: SP.sm, textAlign: 'left',
          padding: `${SP.sm}px ${SP.md}px`, minHeight: isMobile ? ROW_H_MOBILE : ROW_H_DESKTOP,
          fontFamily: FONT.sans, border: '1px solid transparent', borderRadius: R.md,
          cursor: busy ? 'wait' : 'pointer', outline: 'none',
        }}
      >
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
          {/* Имя не усекаем: подписи приходят с бэкенда, «Исполнитель (универсальный)»
              на мобиле честно ложится в две строки */}
          <span style={{
            display: 'flex', alignItems: 'center', gap: SP.xs,
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
          }}>
            <span style={{ minWidth: 0 }}>{name}</span>
            {(filled || shadowed) && <Dot color={filled ? C.accent : C.textMuted} size={7} />}
          </span>
          <span title={summary} style={summaryStyle}>{summary}</span>
        </span>
        {/* Шеврон — второй признак раскрытия: в тёмной теме разница фонов слабее */}
        <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{
          color: C.textMuted, flexShrink: 0,
          transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
        }} />
      </button>

      {expanded && (
        <div style={{
          padding: `0 ${SP.md}px ${SP.md}px`, display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          {/* minmax(0, 1fr) обязателен: с обычным 1fr колонка требует min-content своей
              подписи (nowrap) и грид распирает блок за ширину модалки */}
          <div style={{
            display: 'grid', gap: SP.sm,
            gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, minmax(0, 1fr))',
          }}>
            {TIER_ORDER.map(t => {
              const value = cellOf(rec, t);
              // В узком поле имя пресета «Цепочка 2 · 4 шага» не информативно: вместо него
              // показываем голову цепочки («glm-5.2 → sonnet · +2»), а полный состав и имя
              // пресета — в title (тултип). Для непресетов возвращается обычная подпись
              // маршрута без title, чтобы не дублировать строку в тултипе.
              const { label, title } = cellPresetLabel(value, presets, labelCtx);
              // Самоcсылка: цепочка пресета содержит шаг, указывающий на этот же уровень —
              // резолвер пропустит его как петлю, цепочка фактически короче (тот же класс,
              // что и в слотах, B8 — здесь, чтобы было видно прямо в карточке специальности).
              const cellPresetId = presetIdOf(value);
              const cellPreset = cellPresetId
                ? presets.find(p => p.id.toLowerCase() === cellPresetId.toLowerCase()) ?? null
                : null;
              const selfRef = !!cellPreset && cellPreset.steps.some(s => routeTier(s) === t);
              return (
                <div key={t} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                  <RoutePicker
                    route={value}
                    label={value ? label : ''}
                    title={value ? title : undefined}
                    models={models}
                    tierModels={tierModels}
                    ollamaModel={ollamaModel}
                    showTiers={false}
                    showPresets
                    // 'global' ("Для всех") — созданный пресет должен быть виден и валиден
                    // ВСЕМ, поэтому и список, и inline-создание ограничены общим слоем; 'owner'
                    // не передаём — личное значение резолвится и от общих пресетов тоже (MAJOR 3)
                    presetScope={scope === 'global' ? 'global' : undefined}
                    presetCreation={{
                      settings, savingScope, onSaveLayer,
                      onCreated: (id, s, l) => onPresetCreated(t, id, s, l),
                    }}
                    readOnly={!canEdit}
                    busy={busy}
                    // Слой назван сегментом сверху — в заголовке поля он был бы третьим повтором
                    cardTitle={TIERS[t].title}
                    placeholder="— по умолчанию —"
                    onChange={v => onCell(t, v)}
                  />
                  {selfRef && (
                    <div style={{ fontSize: FS.xs, color: C.warningText, lineHeight: 1.45, padding: '0 2px' }}>
                      Шаг «{TIERS[t].title}» внутри цепочки указывает обратно на эту же ячейку — он будет пропущен.
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          <div>
            <div style={{
              fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
              textTransform: 'uppercase', letterSpacing: '0.06em', marginBottom: SP.xs,
            }}>
              Уровень по умолчанию
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
              <InlineSegmented<TierChoice>
                value={rec?.defaultTier ?? 'inherit'}
                options={tierOptions}
                disabled={!canEdit || busy}
                isMobile={isMobile}
                onChange={v => onDefaultTier(v === 'inherit' ? '' : v)}
              />
              <span style={{ flex: 1 }} />
              {canReset && (
                <Button size="sm" variant="ghost" disabled={busy} onClick={onResetRow}
                  title="Вернуть наследование">
                  Вернуть наследование
                </Button>
              )}
            </div>
            <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: SP.xs }}>
              Каким уровнем работают персоны этой специальности, если у них не задан свой.
            </div>
            {showInherit && inherited && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: SP.xxs }}>
                сейчас наследуется: {TIERS[inherited.tier].title}
                {' · '}{inherited.source === 'owner' ? 'своя' : 'общая'}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
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

function ScopeSeg({ scope, isAdmin, isMobile, onPick }: {
  scope: 'global' | 'owner';
  isAdmin: boolean;
  isMobile: boolean;
  onPick: (s: 'global' | 'owner') => void;
}) {
  return (
    <div style={{
      display: 'flex', gap: 2, background: C.bgSelected, borderRadius: R.lg, padding: 2,
      width: isMobile ? '100%' : undefined,
    }}>
      {isAdmin && (
        <SegBtn active={scope === 'global'} grow={isMobile} onClick={() => onPick('global')}>Для всех</SegBtn>
      )}
      <SegBtn active={scope === 'owner'} grow={isMobile} onClick={() => onPick('owner')}>Только для меня</SegBtn>
    </div>
  );
}

function SegBtn({ active, grow, onClick, children }: {
  active: boolean; grow?: boolean; onClick: () => void; children: React.ReactNode;
}) {
  return (
    <button type="button" onClick={onClick} style={{
      font: 'inherit', fontSize: FS.xs, fontWeight: 600, cursor: 'pointer', border: 'none', borderRadius: R.md,
      padding: '5px 11px', background: active ? C.bgWhite : 'transparent', flex: grow ? 1 : undefined,
      color: active ? C.textHeading : C.textSecondary, boxShadow: active ? 'var(--shadow-card)' : 'none',
    }}>{children}</button>
  );
}
