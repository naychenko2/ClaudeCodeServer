// Блок «Пресеты для роли» — секции промпта специальности (фича
// `specialty-prompt-sections`, волна 4 «Персонализация специальностей»,
// план «Секции промптов»). Рендерится в карточке роли (визитка или
// настройка), показывает секции промпта из SpecialtyPromptSectionsCatalog.
//
// Режимы:
//   • view — карточка роли (SpecialtyRoleView): только ВКЛЮЧЁННЫЕ секции,
//     без счётчика длины / тумблера / кнопок. Если ни одна не включена —
//     единственная строка «Выключено пресетов: N — их видно в настройке роли.».
//   • edit — настройка роли (SpecialtyEditView): все секции (включённые и
//     выключенные), полный контроль: toggle, textarea, счётчик 1024 с
//     порогами жёлтый 80% / красный 95%, кнопки «Задать свой текст» /
//     «Вернуть типовой», строка «Сейчас пойдёт: …», ссылка
//     «Показать выключенные (N)» в конце. Плюс панель наследования (макет
//     persona-inheritance.html, P56–P59): строка «Наследуют: … · своё у …: N»
//     из inheritors (GET /api/specialties/{key}, B09), подпись строки
//     «текст по умолчанию · код / свой текст · общее», подсветка hot и тег
//     «изменено только что» для секций последнего сохранения с эффектом
//     «у Лизы обновилось · у Веры своё — не тронуто», подсказка про лимит и
//     «выключено специальностью» в конце панели.
//
// Все элементы — inline-стили по токенам lib/design.ts, контролы из
// ui-кита. Без Tailwind, без CSS-модулей. Под мобильную ширину работает
// `useIsMobile` (gap уменьшается, padding меньше).
//
// Файлы SpecialtyRoleView и SpecialtyEditView правят другие исполнители
// волны — этот компонент принимает готовые слои и каталог снаружи.

import { useMemo, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Badge, Button, Toggle } from '../ui';
import { useIsMobile } from '../../lib/breakpoints';
import { plural } from '../../lib/plural';
import { genitiveName } from '../../lib/russianName';
import {
  effectivePromptSection, getPromptSectionsCatalog, loadPromptSectionsCatalog,
  sectionsOf, withPromptSection, withoutPromptSection,
  type EffectivePromptSection, type PromptSectionSource,
} from '../../lib/specialties';
import type { LayerReducer } from '../../lib/presets';
import type {
  SpecialtyInheritor, SpecialtyPromptSectionMeta, SpecialtyPromptSectionsCatalog,
  SpecialtySettingsLayer,
} from '../../types';

// Потолок длины секции — общий с бэком (SpecialtyPromptPresets.SectionTextLimit).
// Дубликат на фронте: счётчик UI тоже сверяется с этой константой и подсвечивает
// жёлтый/красный, бэкенд при приёме применит финальный кламп.
const TEXT_LIMIT = 1024;

// === Бейдж источника (слои: code / global / user / owner) ===
// После перехода на единый общий слой (ADR-012) «owner» больше не значит «личное»:
// в edit-режиме это ПРАВИМЫЙ слой, а он теперь общий. Поэтому подписи owner и global
// совпадают — личной пометки в однослойной модели быть не должно.
// 'persona' — слой самой персоны: этот экран его не рендерит (он резолвит specialty-слои),
// запись нужна для полноты Record общего union (P2a, GET /api/personas/{id}/prompt-sections).
const SRC_LABEL: Record<PromptSectionSource, string> = {
  code:    'Из кода',
  global:  'Общее',
  user:    'Пользователя',
  owner:   'Общее',
  persona: 'Своей персоны',
};

// Подпись «Сейчас пойдёт» под текстом секции: чьё значение применится в промпте.
const SRC_NOTE: Record<PromptSectionSource, string> = {
  code:    'Сейчас пойдёт: текст из кода (дефолт)',
  global:  'Сейчас пойдёт: текст из общего слоя (настройки администратора)',
  user:    'Сейчас пойдёт: текст из слоя пользователя (выбранного администратором)',
  owner:   'Сейчас пойдёт: текст из общего слоя (настройки администратора)',
  persona: 'Сейчас пойдёт: свой текст персоны',
};

// Цвет счётчика 1024: нейтральный → жёлтый (80%) → красный (95%). Те же пороги,
// что в mockups/personas-specialties/index.html (index.html:938-939) и в
// SpecialtyPromptSectionsPanel.tsx (перенесены в общее место).
function lengthColor(len: number, limit: number): string {
  if (len > limit * 0.95) return C.dangerText;
  if (len >= limit * 0.8) return C.warningText;
  return C.textMuted;
}

function SourceBadge({ src }: { src: PromptSectionSource }): React.ReactElement {
  const isCode = src === 'code';
  return (
    <span style={{
      fontFamily: FONT.sans, fontSize: FS.xs,
      fontWeight: isCode ? 400 : 600,
      padding: '2px 8px', borderRadius: R.max,
      background: C.bgSelected,
      color: isCode ? C.textMuted : C.textSecondary,
      fontStyle: isCode ? 'italic' : 'normal',
      whiteSpace: 'nowrap',
    }}>{SRC_LABEL[src]}</span>
  );
}

// Подзаголовок секции «Пресеты для роли» — единый для обоих режимов.
// Скрывается, если родитель сам рисует SectionLabel снаружи блока (визитка роли).
function SectionTitle({ children }: { children: React.ReactNode }): React.ReactElement {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
      color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.07em',
    }}>{children}</div>
    );
}

// Имя в родительном падеже для связных фраз («своё у Веры»); несклоняемое имя
// остаётся в именительном — фраза хуже грамматически, но не теряет смысла.
const genOf = (name: string): string => genitiveName(name) ?? name;

// Строка «Наследуют: Вера, Лиза · своё у Веры: 1 секция» (P56, макет .heirs).
// Наследники — из GET /api/specialties/{key} (B09); перечисляются только те,
// у кого есть своё. Пустой список наследников — строки нет (сказать нечего).
function InheritorsLine({ inheritors }: { inheritors: SpecialtyInheritor[] }): React.ReactElement {
  const ownParts = inheritors
    .filter(i => i.ownSections > 0)
    .map(i => `· своё у ${genOf(i.name)}: ${i.ownSections} ${plural(i.ownSections, 'секция', 'секции', 'секций')}`);
  return (
    <div style={{
      fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5,
    }}>
      <span>
        Наследуют: {inheritors.map(i => i.name).join(', ')}{' '}
        {ownParts.length > 0 && (
          <span style={{ color: C.textMuted }}>{ownParts.join(' ')}</span>
        )}
      </span>
    </div>
  );
}

// Эффект только что сохранённой правки для наследников (P58, «у Лизы обновилось ·
// у Веры своё — не тронуто»): у кого своих подмен нет — правка дошла; у кого есть —
// их своё не тронуто. Фронт не знает, какая именно секция у персоны своя (B09 даёт
// только число), поэтому про конкретную секцию здесь не утверждается — только про
// факт своего. Пустой список наследников — подписи нет.
function hotEffectText(inheritors: SpecialtyInheritor[] | null | undefined): string | null {
  if (!inheritors || inheritors.length === 0) return null;
  const noOwn = inheritors.filter(i => i.ownSections === 0).map(i => genOf(i.name));
  const own = inheritors.filter(i => i.ownSections > 0).map(i => genOf(i.name));
  const parts: string[] = [];
  if (noOwn.length) parts.push(`у ${noOwn.join(', ')} обновилось`);
  if (own.length) parts.push(`у ${own.join(', ')} своё — не тронуто`);
  return parts.length ? parts.join(' · ') : null;
}

// === Карточка секции в режиме просмотра ===
// Признака «своё/типовое» здесь нет: визитку роли видит любой пользователь, а слой
// теперь один общий — «Свой текст» врал бы про чужие настройки. Всё нужное несёт
// бейдж источника: «Из кода» либо «Общее».
function PresetCardView({ meta, eff }: {
  meta: SpecialtyPromptSectionMeta;
  eff: EffectivePromptSection;
}): React.ReactElement {
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      padding: '10px 14px', display: 'flex', flexDirection: 'column', gap: 6,
    }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
      }}>
        <span style={{
          flex: 1, minWidth: 0,
          fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 700,
          color: C.textHeading,
        }}>{meta.label}</span>
        <SourceBadge src={eff.enabledSource} />
      </div>
      <div style={{
        fontSize: FS.sm, color: C.textPrimary, lineHeight: 1.5,
        whiteSpace: 'pre-wrap', wordBreak: 'break-word',
      }}>{eff.text || '—'}</div>
      <div style={{
        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: 2,
      }}>{SRC_NOTE[eff.textSource]}</div>
    </div>
  );
}

// === Карточка секции в режиме настройки ===
// hot — секция изменена последним сохранением (P58): рамка C.info с кольцом C.infoBg
// (тот же приём, что у .spsec.hot в макете) и тег «изменено только что».
// caption — подпись строки (P57): «текст по умолчанию · код» / «свой текст · общее»
// (в макете «свой текст · владелец»; в однослойной модели ADR-012 это общий слой —
// формулировка совпадает с существующими подписями этого файла), для hot — вместо
// неё эффект правки («у Лизы обновилось · у Веры своё — не тронуто»).
function PresetCardEdit({ meta, eff, canEdit, hot, caption, onEnabled, onText, onReset, onOverride }: {
  meta: SpecialtyPromptSectionMeta;
  eff: EffectivePromptSection;
  canEdit: boolean;
  hot: boolean;
  caption: string | null;
  onEnabled: (sectionId: string, enabled: boolean) => void;
  onText: (sectionId: string, text: string) => void;
  onReset: (sectionId: string) => void;
  onOverride: (sectionId: string) => void;
}): React.ReactElement {
  // isOverride — есть ли своё переопределение в текущем слое (owner в нашем случае,
  // но резолв даёт owner/user/global). Для UI «свой текст» достаточно source === 'owner':
  // переопределение на user/global отображается бейджем источника отдельно.
  const hasOwnOverride = eff.enabledSource === 'owner' || eff.textSource === 'owner';
  const editable = canEdit && hasOwnOverride;

  const text = eff.text ?? '';
  const len = text.length;
  const cntCls = lengthColor(len, TEXT_LIMIT);

  return (
    <div style={{
      background: C.bgWhite, borderRadius: R.xl,
      border: hot ? `1px solid ${C.info}` : `1px solid ${C.border}`,
      boxShadow: hot ? `0 0 0 3px ${C.infoBg}` : undefined,
      padding: '10px 14px', display: 'flex', flexDirection: 'column', gap: 8,
    }}>
      {/* Шапка: тумблер + название + тег «изменено только что» (hot) */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
      }}>
        <Toggle
          checked={eff.enabled}
          onChange={v => onEnabled(meta.id, v)}
          disabled={!canEdit}
          ariaLabel={`Секция «${meta.label}»`}
        />
        <span style={{
          flex: 1, minWidth: 0,
          fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 700,
          color: C.textHeading,
        }}>{meta.label}</span>
        {hot && <Badge tone="info" size="xs">изменено только что</Badge>}
      </div>
      {caption && (
        <div style={{
          fontSize: FS.xs, color: hot ? C.textSecondary : C.textMuted, lineHeight: 1.45,
        }}>{caption}</div>
      )}
      <div style={{
        fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45,
      }}>{meta.description}</div>

      {/* Панель управления: кнопка переопределения/сброса + счётчик */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
      }}>
        {editable ? (
          <Button variant="ghost" size="sm" onClick={() => onReset(meta.id)}>
            Вернуть типовой
          </Button>
        ) : (
          canEdit && (
            <Button variant="ghost" size="sm" onClick={() => onOverride(meta.id)}>
              Задать свой текст
            </Button>
          )
        )}
        <span style={{
          marginLeft: 'auto',
          fontFamily: FONT.mono, fontSize: FS.xs,
          color: cntCls,
          fontWeight: len >= TEXT_LIMIT * 0.8 ? 700 : 400,
        }}>{len} / {TEXT_LIMIT}</span>
      </div>

      {/* Textarea: нативный (TextArea из ui-кита не принимает maxLength в одной из ранних
          версий). На read-only — disabled и серый фон. На override — редактируемый. */}
      <textarea
        value={text}
        onChange={e => onText(meta.id, e.target.value)}
        maxLength={TEXT_LIMIT}
        disabled={!editable}
        placeholder={eff.text ? '' : 'Текст секции…'}
        rows={4}
        style={{
          width: '100%', fontFamily: FONT.sans, fontSize: FS.sm,
          color: editable ? C.textHeading : C.textSecondary,
          background: editable ? C.bgWhite : C.bgSelected,
          borderRadius: R.xl, border: `1px solid ${C.border}`,
          padding: '8px 10px', resize: 'vertical',
          minHeight: 60, maxHeight: 220,
          outline: 'none', lineHeight: 1.5, boxSizing: 'border-box',
          cursor: editable ? 'text' : 'not-allowed',
        }}
      />

      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
        {SRC_NOTE[hasOwnOverride ? 'owner' : eff.textSource]}
      </div>
    </div>
  );
}

export interface RolePresetsBlockProps {
  roleKey: string;
  catalog: SpecialtyPromptSectionsCatalog | null;
  // Текущий редактируемый слой (owner / global / user — что выбрано в LayerSwitch).
  editLayer: SpecialtySettingsLayer | null;
  // globalLayer нужен для effectivePromptSection; обычно это settings.global,
  // чтобы резолв шёл поверх дефолта каталога, даже если правим user-слой.
  globalLayer: SpecialtySettingsLayer | null;
  // userLayer — только для admin на user-слое; иначе null.
  userLayer: SpecialtySettingsLayer | null;
  mode: 'view' | 'edit';
  // Только для edit: запись слоя через редьюсер. Тип совпадает с контрактом
  // saveLayer в lib/presets.ts (см. SpecialtyEditView.ModelsSection).
  onSave?: (reducer: LayerReducer) => Promise<void>;
  // Не рисовать собственный SectionTitle — родитель сам вешает SectionLabel
  // снаружи блока (визитка роли, плоские секции без белых коробок).
  showTitle?: boolean;
  // Наследники секций специальности — из GET /api/specialties/{key} (B09). Только
  // для edit: строка «Наследуют: …» и эффект правки на hot-секциях. null/undefined
  // (не загружено) — строки и эффекта нет, остальной блок работает как раньше.
  inheritors?: SpecialtyInheritor[] | null;
  // Id секций, изменённых последним сохранением родителя (P58): подсветка hot и
  // тег «изменено только что». Пустой/undefined — ничего не подсвечено.
  justChanged?: ReadonlySet<string> | null;
}

export function RolePresetsBlock({
  roleKey, catalog, editLayer, globalLayer, userLayer, mode, onSave, showTitle = true,
  inheritors, justChanged,
}: RolePresetsBlockProps): React.ReactElement {
  const isMobile = useIsMobile();
  const isView = mode === 'view';
  const canEdit = !isView && typeof onSave === 'function';

  // Локальный флаг «Показать выключенные» в edit-режиме — единый для роли.
  const [showDisabled, setShowDisabled] = useState(false);

  // Список метаданных секций из каталога. Если каталог не передан (загрузка) —
  // SpecialtyPromptSectionsPanel обычно уже подгрузил его через loadPromptSectionsCatalog
  // и каталог в стейте; здесь компонент работает с тем, что пришёл.
  const meta: SpecialtyPromptSectionMeta[] = useMemo(
    () => (catalog ? sectionsOf(catalog) : []),
    [catalog],
  );

  // Эффективные значения (enabled/text/источники) для каждой секции.
  const effById = useMemo(() => {
    const out = new Map<string, EffectivePromptSection>();
    for (const m of meta) {
      out.set(m.id, effectivePromptSection(
        catalog, editLayer, userLayer, globalLayer, roleKey, m.id,
      ));
    }
    return out;
  }, [meta, catalog, editLayer, userLayer, globalLayer, roleKey]);

  // Делим на enabled/disabled для фильтрации в обоих режимах.
  const enabledMeta = useMemo(
    () => meta.filter(m => effById.get(m.id)?.enabled),
    [meta, effById],
  );
  const disabledMeta = useMemo(
    () => meta.filter(m => !effById.get(m.id)?.enabled),
    [meta, effById],
  );
  const enabledCount = enabledMeta.length;
  const disabledCount = disabledMeta.length;

  // === Обработчики edit-режима ===
  const handleEnabled = (sectionId: string, enabled: boolean) => {
    if (!editLayer || !onSave) return;
    void onSave((cur) => withPromptSection(cur, roleKey, sectionId, { enabled }));
  };
  const handleText = (sectionId: string, text: string) => {
    if (!editLayer || !onSave) return;
    void onSave((cur) => withPromptSection(cur, roleKey, sectionId, { text }));
  };
  const handleReset = (sectionId: string) => {
    if (!editLayer || !onSave) return;
    void onSave((cur) => withoutPromptSection(cur, roleKey, sectionId));
  };
  const handleOverride = (sectionId: string) => {
    if (!editLayer || !onSave) return;
    const eff = effById.get(sectionId);
    if (!eff) return;
    // Создаём override в текущем слое: пишем текущий эффективный текст/enabled
    void onSave((cur) => withPromptSection(cur, roleKey, sectionId, {
      enabled: eff.enabled,
      text: eff.text ? eff.text : null,
    }));
  };

  // === VIEW: только включённые секции, без кнопок ===
  if (isView) {
    if (enabledCount === 0) {
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {showTitle && <SectionTitle>Пресеты для роли</SectionTitle>}
          <div style={{
            fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5,
          }}>
            Выключено пресетов: {disabledCount} — их видно в настройке роли.
          </div>
        </div>
      );
    }
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {showTitle && <SectionTitle>Пресеты для роли</SectionTitle>}
        <div style={{
          display: 'flex', flexDirection: 'column',
          gap: isMobile ? SP.xs : SP.sm,
        }}>
          {enabledMeta.map(m => (
            <PresetCardView
              key={m.id}
              meta={m}
              eff={effById.get(m.id)!}
            />
          ))}
        </div>
      </div>
    );
  }

  // === EDIT: все секции (по флагу showDisabled) ===
  const visibleMeta = showDisabled ? meta : enabledMeta;

  // Строка «Наследуют: …» (P56) — над списком в обоих edit-состояниях, если
  // наследники пришли. Не пришли (null) или их нет — строки нет.
  const heirsEl = inheritors && inheritors.length > 0
    ? <InheritorsLine inheritors={inheritors} />
    : null;

  // Подпись каждой строки: base — состояние текста и слой (P57); hot — эффект
  // правки (P58), если наследники известны; без них — обычная подпись. Для
  // выключенной строки подписи нет: «обновилось» про выключение говорит двусмысленно.
  const captionOf = (m: SpecialtyPromptSectionMeta, eff: EffectivePromptSection): string | null => {
    if (!eff.enabled) return null;
    const base = (eff.enabledSource === 'owner' || eff.textSource === 'owner')
      ? 'свой текст · общее' : 'текст по умолчанию · код';
    if (justChanged?.has(m.id)) return hotEffectText(inheritors) ?? base;
    return base;
  };

  // Подсказка конца панели (P59): общий лимит и смысл выключения для персон.
  const limitHintEl = (
    <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
      Счётчик и лимит те же: до {TEXT_LIMIT} символов на секцию. Выключение здесь
      персона видит подписью «выключено специальностью» — это не её выбор, и
      «Выключено» у неё не нажато.
    </div>
  );

  // Полностью пустое состояние: ничего не включено и пользователь ещё не раскрыл выключенные.
  if (visibleMeta.length === 0) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {showTitle && <SectionTitle>Пресеты для роли</SectionTitle>}
        {heirsEl}
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl,
          padding: isMobile ? '14px 12px' : '16px 14px',
          textAlign: 'center',
          fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5,
        }}>
          <div style={{
            fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: 4,
          }}>Пресеты пока не настроены</div>
          Включите пресеты и задайте текст — инструкции добавятся в промпт персон
          этой специальности.
          {disabledCount > 0 && (
            <div style={{ marginTop: SP.sm }}>
              <Button
                variant="ghost" size="sm"
                onClick={() => setShowDisabled(true)}
              >
                Показать выключенные ({disabledCount})
              </Button>
            </div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {showTitle && <SectionTitle>Пресеты для роли</SectionTitle>}
      {heirsEl}
      <div style={{
        display: 'flex', flexDirection: 'column',
        gap: isMobile ? SP.xs : SP.sm,
      }}>
        {visibleMeta.map(m => {
          const eff = effById.get(m.id)!;
          return (
            <PresetCardEdit
              key={m.id}
              meta={m}
              eff={eff}
              canEdit={canEdit}
              hot={justChanged?.has(m.id) ?? false}
              caption={captionOf(m, eff)}
              onEnabled={handleEnabled}
              onText={handleText}
              onReset={handleReset}
              onOverride={handleOverride}
            />
          );
        })}
      </div>
      {limitHintEl}
      {/* Ссылка «Показать/Скрыть выключенные (N)» — в edit-режиме видна, если есть что показать. */}
      {disabledCount > 0 && (
        <div>
          <button
            type="button"
            onClick={() => setShowDisabled(v => !v)}
            style={{
              font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs,
              fontWeight: 600,
              color: C.accent, background: 'none', border: 'none',
              padding: 0, cursor: 'pointer',
              textDecoration: 'underline', textUnderlineOffset: 2,
            }}
          >
            {showDisabled
              ? `Скрыть выключенные (${disabledCount})`
              : `Показать выключенные (${disabledCount})`}
          </button>
        </div>
      )}
    </div>
  );
}

// Ссылка на каталог секций для ленивой загрузки родителем — этот компонент
// сам не дёргает сеть (она асинхронная и может дублироваться с
// SpecialtyPromptSectionsPanel). Родитель либо передаёт готовый `catalog`,
// либо вызывает `loadPromptSectionsCatalog()` заранее и передаёт результат.
//
// Тип-safety: оставляем эти хелперы экспортированными, чтобы контейнер мог
// догрузить каталог единым запросом перед монтированием блока.
export { getPromptSectionsCatalog, loadPromptSectionsCatalog };