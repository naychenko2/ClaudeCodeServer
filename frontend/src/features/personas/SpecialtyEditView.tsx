// Экран «Настройка роли» (адресуется как #/personas/specialties/{roleKey}/edit).
// Доступен ТОЛЬКО админу (прямой хеш режется на уровне PersonasPage; кнопка
// «Редактировать» рисуется только админу на визитке).
//
// Полноценная форма правки по образцу PersonaForm:
//   • шапка-тулбар с акцентной полосой во всю ширину центра (как у персоны),
//     под ними центрированное полотно maxWidth 680;
//   • два раздела с переключателем в тулбаре (симметрично визитке роли):
//     «Общая информация» — доступ, инструменты, свой список запретов, секции
//     промпта, модели по уровням, уровень по умолчанию; «Умения» — типовой
//     профиль привязок через RoleBindingsBlock (mode='edit');
//   • плоские секции, разделённые тонкой линией (паттерн TaskEditForm/PersonaForm);
//   • кнопки «Сохранить»/«Отмена» и dirty-индикатор в шапке-тулбаре — ОБЩИЕ для
//     всей формы (обоих разделов): черновик один, dirty считается по всему
//     черновику; отмена с несохранённым — через ConfirmDialog (учитывает dirty
//     всей формы, а не активного раздела);
//   • раздел «Умения» — черновик без своих запросов к API: правки через
//     onChange → patch({ defaultBindings }), сохраняет общая кнопка формы;
//   • все поля через общие примитивы Field/TextField/FieldLabel (UI-кит);
//   • неизменяемые данные роли (имя, ключ, цвет) несёт тулбар — отдельного
//     hero нет, он дублировал шапку;
//   • запись через LayerReducer в глобальный слой (см. lib/presets.ts);
//   • мобильная раскладка: одна колонка, поля во всю ширину (нижний ориентир 360 CSS).

import { useEffect, useMemo, useRef, useState } from 'react';
import { Book, ChevronLeft, Info } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import {
  ConfirmDialog, Field, PillSwitch, TextField,
} from '../../components/ui';
import { Toolbar, ToolbarIconButton, tbBtnGhost, tbBtnPrimary } from '../../components/Toolbar';
import { useIsMobile } from '../../lib/breakpoints';
import type { LayerReducer } from '../../lib/presets';
import { useModels } from '../../lib/models';
import { TIER_TITLE, type ModelTierKey } from '../../lib/modelTiers';
import { RoutePicker } from '../../components/RoutePicker';
import { RoleBindingsBlock } from '../../components/specialties/RoleBindingsBlock';
import { RolePresetsBlock } from '../../components/specialties/RolePresetsBlock';
import { RoleAvatar } from '../../components/specialties/RoleAvatar';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { SectionLabel } from '../tasks/bits';
import {
  getPromptSectionsCatalog, loadPromptSectionsCatalog,
  roleColorKey,
} from '../../lib/specialties';
import { usePresets, usePreview, formatEffectiveLine, routeDisplayLabel } from '../../lib/presets';
import type {
  ModelTierValue, PersonaAccess,
  SpecialtyCatalogEntry, SpecialtyDefaultBinding, SpecialtyPromptSection,
  SpecialtyPromptSectionsCatalog, SpecialtySettingsLayer, SpecialtyTemplateSettings,
} from '../../types';

// === Опции и подписи ===

const ACCESS_OPTIONS: { value: PersonaAccess; label: string }[] = [
  { value: 'full', label: 'Полный' },
  { value: 'readOnly', label: 'Только чтение' },
  { value: 'custom', label: 'Свой список' },
];

const TOOL_KEYS = ['tasks', 'notes', 'web'] as const;
type ToolKey = typeof TOOL_KEYS[number];
const TOOL_LABEL: Record<ToolKey, string> = {
  tasks: 'Задачи', notes: 'Заметки', web: 'Веб',
};

// Состав возможностей роли. tools === null означает «все» — полный набор (по контракту
// SpecialtyTemplate). В UI режим «Все» отображается как включённые все три чипа.
const ALL_TOOLS: ToolKey[] = ['tasks', 'notes', 'web'];

// === Разделы формы: «Общая информация» | «Умения» ===
//
// Сегмент в тулбаре — симметрично визитке роли (SpecialtyRoleView): те же
// подписи и иконки. Выбор — ЛОКАЛЬНОЕ состояние; черновик и кнопки
// «Сохранить»/«Отмена» общие для обоих разделов, dirty считается по всей форме.
type RoleTab = 'general' | 'skills';

const TAB_OPTIONS: { value: RoleTab; label: string; icon: React.ReactElement }[] = [
  { value: 'general', label: 'Общая информация', icon: <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
  { value: 'skills', label: 'Умения', icon: <Book size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
];

const DEFAULT_TIER_OPTIONS: { value: ModelTierValue | ''; label: string }[] = [
  { value: '', label: 'Не задано' },
  { value: 'strong', label: TIER_TITLE.strong },
  { value: 'medium', label: TIER_TITLE.medium },
  { value: 'weak', label: TIER_TITLE.weak },
];

// Разбор списка запретов «через запятую» — пустые/повторяющиеся куски выбрасываются
function parseDisallowed(s: string): string[] {
  return Array.from(new Set(s.split(',').map(t => t.trim()).filter(Boolean)));
}

// Проверка: запись специальности пуста по всем редактируемым полям (кроме наследуемых).
// Такую запись смысла держать в слое нет — она дублировала бы дефолты каталога.
// Сигнатура совпадает с приватным isRecordEmpty из lib/specialties.ts (повторяем
// логику здесь, чтобы не тянуть приватные функции из чужого модуля).
function isRecordEmpty(rec: SpecialtyTemplateSettings): boolean {
  const noTiers = !rec.tierStrong && !rec.tierMedium && !rec.tierWeak && !rec.defaultTier;
  const noSections = !rec.promptSections || rec.promptSections.length === 0;
  const noBindings = !rec.defaultBindings || rec.defaultBindings.length === 0;
  return noTiers && noSections && noBindings;
}

// === Основной экран ===

export interface SpecialtyEditViewProps {
  roleKey: string;
  catalog: SpecialtyCatalogEntry[];
  layerSettings: SpecialtySettingsLayer | null;
  // Каталог секций промптов: если передан — берём его, иначе тянем сами
  // (модульный кэш, лишний запрос не уходит). Опциональный, чтобы сохранить
  // совместимость с родительским вызовом (PersonasSpecialties правит другой исполнитель).
  promptSectionsCatalog?: SpecialtyPromptSectionsCatalog | null;
  onBack: () => void;
  onSave: (reducer: LayerReducer) => Promise<void>;
  // Императивный API для тулбара-родителя: сохранить (через ref) и статус формы
  // (через onStatus). На этом экране тулбар рисуется внутри, но сигнатура
  // симметрична PersonaForm — если в будущем тулбар поднимут наружу, контракт готов.
  onStatus?: (status: { canSave: boolean; saving: boolean; dirty: boolean }) => void;
}

export function SpecialtyEditView({
  roleKey, catalog, layerSettings, promptSectionsCatalog: promptSectionsCatalogProp,
  onBack, onSave, onStatus,
}: SpecialtyEditViewProps): React.ReactElement {
  const isMobile = useIsMobile();

  // Активный раздел формы — локальное состояние (как у визитки роли). При смене
  // roleKey (родитель не перемонтирует экран) возвращаемся на «Общую информацию».
  const [tab, setTab] = useState<RoleTab>('general');
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс локального UI при смене роли
    setTab('general');
  }, [roleKey]);

  // Резолв роли из каталога (системные подписи — не редактируются)
  const role = useMemo(() => catalog.find(r => r.key === roleKey) ?? null, [catalog, roleKey]);
  const template = role?.template ?? null;

  // === Каталог секций промптов ===
  // Используем проп, если он пришёл; иначе тянем из модульного кэша или сети.
  const [promptSectionsCatalog, setPromptSectionsCatalog] =
    useState<SpecialtyPromptSectionsCatalog | null>(promptSectionsCatalogProp ?? getPromptSectionsCatalog());
  useEffect(() => {
    if (promptSectionsCatalogProp) {
      setPromptSectionsCatalog(promptSectionsCatalogProp);
      return;
    }
    if (getPromptSectionsCatalog()) {
      setPromptSectionsCatalog(getPromptSectionsCatalog());
      return;
    }
    let cancelled = false;
    void loadPromptSectionsCatalog().then(c => { if (!cancelled && c) setPromptSectionsCatalog(c); });
    return () => { cancelled = true; };
  }, [promptSectionsCatalogProp]);

  // Запись специальности из слоя (если есть) — инициализация черновика формы
  const initialRec: SpecialtyTemplateSettings = useMemo(() => {
    const fromLayer = layerSettings?.specialties?.[roleKey] ?? null;
    if (fromLayer) return fromLayer;
    // Записи нет — стартуем от шаблона прав из каталога (effective-дефолт админа).
    return {
      access: template?.access ?? 'full',
      tools: template?.tools ?? null,
      disallowedTools: template?.disallowedTools ?? null,
    };
  }, [layerSettings, roleKey, template]);

  // === Состояние формы ===
  // Черновик записи целиком: одно поле SpecialtyTemplateSettings. Простые поля
  // (access/tools/disallowed/tier*) правятся напрямую, promptSections и defaultBindings
  // — массивами. На onSave собираем итоговую rec и пушим в слой.
  const [recDraft, setRecDraft] = useState<SpecialtyTemplateSettings>(initialRec);
  const [saving, setSaving] = useState(false);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Сброс черновика при смене roleKey (родитель не пересоздаёт компонент).
  // Зависим от initialRec (а не от layerSettings/roleKey) — иначе на каждой
  // перерисовке будем терять несохранённые правки.
  const lastInitRef = useRef(initialRec);
  useEffect(() => {
    if (lastInitRef.current !== initialRec) {
      lastInitRef.current = initialRec;
      setRecDraft(initialRec);
      setError(null);
    }
  }, [initialRec]);

  // Подсчёт dirty: snapshot текущего черновика vs snapshot инициализатора.
  // JSON.stringify устойчив к порядку ключей только если ключи добавляются в одном
  // порядке — собираем оба объекта одним ключом.
  const buildSnapshot = (rec: SpecialtyTemplateSettings): string => JSON.stringify({
    access: rec.access ?? 'full',
    tools: rec.tools === undefined ? null : rec.tools,
    disallowed: parseDisallowed((rec.disallowedTools ?? []).join(', ')).sort(),
    tierStrong: rec.tierStrong ?? null,
    tierMedium: rec.tierMedium ?? null,
    tierWeak: rec.tierWeak ?? null,
    defaultTier: rec.defaultTier ?? null,
    promptSections: (rec.promptSections ?? []).map(s => ({ id: s.id, enabled: s.enabled, text: s.text ?? null })),
    defaultBindings: (rec.defaultBindings ?? []).map(b => ({
      type: b.type, mode: b.mode, condition: b.condition, skillName: b.skillName ?? null,
    })),
  });
  const initialSnapshot = useMemo(() => buildSnapshot(initialRec), [initialRec]);
  const dirty = buildSnapshot(recDraft) !== initialSnapshot;
  const canSave = dirty && !saving;

  // === Хелпер правки promptSection через RolePresetsBlock ===
  // Блок ждёт LayerReducer и editLayer; применяем reducer к локальному «editLayer»
  // и подменяем только promptSections в черновике (остальные поля не трогает).
  // ВАЖНО: хук стоит выше любых ранних возвратов — иначе первый рендер без роли
  // (каталог ещё грузится) не вызовет его, а следующий вызовет, и React упадёт
  // с «Rendered more hooks than during the previous render».
  const editLayerForBlock: SpecialtySettingsLayer = useMemo(() => ({
    specialties: { [roleKey]: recDraft },
    presets: layerSettings?.presets ?? [],
    defaultSpecialty: layerSettings?.defaultSpecialty ?? null,
  }), [recDraft, layerSettings, roleKey]);

  // === Статус наверх (для будущей интеграции с внешним тулбаром) ===
  useEffect(() => {
    onStatus?.({ canSave, saving, dirty });
  }, [canSave, saving, dirty, onStatus]);

  // === Хелпер правки черновика (мутабельный спред — паттерн PersonaForm) ===
  const patch = (patch: Partial<SpecialtyTemplateSettings>) => {
    setRecDraft(prev => ({ ...prev, ...patch }));
  };

  // === Запись черновика в слой через LayerReducer ===
  // Сборка итоговой rec: пустые массивы сворачиваем в null (наследование вниз);
  // полностью пустую запись удаляем из слоя, чтобы не дублировать дефолты каталога.
  const handleSave = async () => {
    if (!dirty || saving) return;
    setSaving(true);
    setError(null);
    const draft = recDraft;
    const disallowed = draft.access === 'custom' ? parseDisallowed((draft.disallowedTools ?? []).join(', ')) : null;
    const promptSections: SpecialtyPromptSection[] | null = ((draft.promptSections ?? []).length > 0)
      ? (draft.promptSections ?? []).slice()
      : null;
    const defaultBindings: SpecialtyDefaultBinding[] | null = ((draft.defaultBindings ?? []).length > 0)
      ? (draft.defaultBindings ?? []).slice()
      : null;
    const finalRec: SpecialtyTemplateSettings = {
      access: draft.access,
      tools: draft.tools ?? null,
      disallowedTools: disallowed,
      tierStrong: draft.tierStrong ?? null,
      tierMedium: draft.tierMedium ?? null,
      tierWeak: draft.tierWeak ?? null,
      defaultTier: draft.defaultTier ?? null,
      promptSections,
      defaultBindings,
    };
    const reducer: LayerReducer = (cur) => {
      const specialties = { ...cur.specialties };
      if (isRecordEmpty(finalRec)) {
        delete specialties[roleKey];
      } else {
        specialties[roleKey] = finalRec;
      }
      return {
        ...cur,
        specialties,
        // Пресеты и defaultSpecialty не трогаем — у формы нет на них прав
      };
    };
    try {
      await onSave(reducer);
      lastInitRef.current = finalRec;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить роль');
    } finally {
      setSaving(false);
    }
  };

  // === Отмена: если есть несохранённое — ConfirmDialog ===
  const handleCancel = () => {
    if (dirty) { setConfirmCancel(true); return; }
    onBack();
  };

  // === Роль не найдена — короткое состояние ===
  if (!role) {
    return (
      <div>
        <div style={{
          maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
          padding: isMobile ? '18px 16px 32px' : '22px 32px 40px',
        }}>
          <BackRow onBack={onBack} />
          <div style={{
            border: `1px dashed ${C.dashed}`, borderRadius: R.xl,
            padding: '22px 18px', textAlign: 'center',
            color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.55,
          }}>Роль не найдена в каталоге.</div>
        </div>
      </div>
    );
  }

  const accent = AGENT_COLORS[roleColorKey(role, roleKey)] ?? C.accent;

  // Колбэк для RolePresetsBlock: он передаёт редьюсер, мы применяем его к нашему
  // editLayer и подменяем только promptSections в черновике формы.
  const applySectionReducer = (reducer: LayerReducer): Promise<void> => {
    const next = reducer(editLayerForBlock);
    const nextRec = next.specialties[roleKey] ?? null;
    setRecDraft(prev => ({
      ...prev,
      promptSections: nextRec?.promptSections ?? prev.promptSections ?? [],
    }));
    return Promise.resolve();
  };

  return (
    // Прокрутка — у родителя (PersonasSpecialties): двойные скроллеры съедали
    // место на 360 CSS и резали PillSwitch доступа. Шапка и акцентная полоса идут
    // во всю ширину центра (как у персоны), полотно формы центрируется под ними.
    <div>
      {/* Шапка-тулбар роли: стрелка «Назад», название, кнопки действий */}
      <ToolbarRow
        accent={accent}
        roleKey={roleKey}
        roleLabel={role.label}
        roleAvatar={<RoleAvatar catalog={role} roleKey={roleKey} size={isMobile ? 32 : 40} />}
        isMobile={isMobile}
        tab={tab}
        onTab={setTab}
        canSave={canSave}
        saving={saving}
        dirty={dirty}
        onBack={onBack}
        onCancel={handleCancel}
        onSave={handleSave}
      />
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />

      <div style={{
        maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '18px 16px 32px' : '22px 32px 40px',
        display: 'flex', flexDirection: 'column', gap: 28,
      }}>
        {/* Описание роли: на десктопе его несёт подпись тулбара, на мобиле
            тулбар текста не рисует — там показываем строку описания здесь.
            Отдельного hero (аватар + название) нет: он дублировал тулбар. */}
        {isMobile && role.description?.trim() && (
          <div style={{
            fontSize: FS.base, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5,
          }}>{role.description}</div>
        )}

        {tab === 'general' ? (
          <>
            {/* Доступ */}
            <Section>
              <SectionLabel style={{ marginBottom: 10 }}>Доступ</SectionLabel>
              <PillSwitch<PersonaAccess>
                fill
                value={recDraft.access}
                onChange={(v) => patch({ access: v })}
                options={ACCESS_OPTIONS}
              />
              <Hint>
                {recDraft.access === 'full' && 'Персоны этой роли имеют полный доступ ко всем функциям.'}
                {recDraft.access === 'readOnly' && 'Персоны этой роли могут только читать — никаких команд, заметок или веб-инструмента.'}
                {recDraft.access === 'custom' && 'Задайте свой список запретов ниже — он ограничит возможности персон этой роли.'}
              </Hint>
            </Section>

            {/* Инструменты */}
            <Section>
              <SectionLabel style={{ marginBottom: 10 }}>Инструменты</SectionLabel>
              <ToolsRow
                value={recDraft.tools === undefined ? null : recDraft.tools}
                onChange={(v) => patch({ tools: v })}
              />
            </Section>

            {/* Свой список запретов — только при custom */}
            {recDraft.access === 'custom' && (
              <Section>
                <Field label="Свой список запретов" hint="Через запятую — какие возможности недоступны персоне этой роли">
                  <TextField
                    value={(recDraft.disallowedTools ?? []).join(', ')}
                    onChange={(v) => patch({ disallowedTools: v.split(',').map(s => s.trim()).filter(Boolean) })}
                    placeholder="tasks, notes"
                  />
                </Field>
              </Section>
            )}

            {/* Секции промпта — через готовый блок RolePresetsBlock (mode='edit') */}
            <Section>
              <SectionLabel style={{ marginBottom: 10 }}>Секции промпта</SectionLabel>
              <RolePresetsBlock
                roleKey={roleKey}
                catalog={promptSectionsCatalog}
                editLayer={editLayerForBlock}
                globalLayer={layerSettings}
                userLayer={null}
                mode="edit"
                onSave={applySectionReducer}
                showTitle={false}
              />
            </Section>

            {/* Модели по уровням — три RoutePicker */}
            <Section>
              <TierModelsSection
                roleKey={roleKey}
                strong={recDraft.tierStrong ?? ''}
                medium={recDraft.tierMedium ?? ''}
                weak={recDraft.tierWeak ?? ''}
                onStrong={(v) => patch({ tierStrong: v.trim() || null })}
                onMedium={(v) => patch({ tierMedium: v.trim() || null })}
                onWeak={(v) => patch({ tierWeak: v.trim() || null })}
              />
            </Section>

            {/* Уровень по умолчанию */}
            <Section>
              <SectionLabel style={{ marginBottom: 10 }}>Уровень по умолчанию</SectionLabel>
              <Field
                label=""
                hint="Каким уровнем работает персона этой роли, если у неё не задана своя ячейка уровня"
              >
                <PillSwitch<ModelTierValue | ''>
                  // На 360 CSS 4 опции в fill-режиме вылезают за поле формы (~21 px).
                  // Снимаем fill на мобиле — опции занимают естественную ширину и
                  // помещаются (тот же приём, что у «Доступ», но там 3 опции).
                  fill={!isMobile}
                  value={recDraft.defaultTier ?? ''}
                  onChange={(v) => patch({ defaultTier: v || null })}
                  options={DEFAULT_TIER_OPTIONS}
                />
              </Field>
            </Section>

            {/* Подсказка под формой — общий инвариант раздела */}
            <div style={{
              fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5, padding: '0 2px',
            }}>
              Поле персоны сильнее правила специальности; специальность без правила
              наследует «Любая специальность» → «Модели по умолчанию».
            </div>
          </>
        ) : (
          <>
            {/* Умения — типовой профиль привязок роли (RoleBindingsBlock, mode='edit'):
                карточки с инлайн-правкой, степпер добавления и «Найти навык» из реестра.
                Правки уходят в черновик формы (onChange → patch), своих запросов к API
                раздел не делает — сохраняет общая кнопка «Сохранить» в шапке. */}
            <Section>
              <RoleBindingsBlock
                roleKey={roleKey}
                bindings={recDraft.defaultBindings ?? []}
                mode="edit"
                accent={accent}
                onChange={(v) => patch({ defaultBindings: v })}
              />
            </Section>
          </>
        )}

        {error && (
          <div style={{
            fontSize: FS.xs, color: C.dangerText, fontFamily: FONT.sans,
            background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
            borderRadius: R.md, padding: '8px 12px', lineHeight: 1.5,
          }}>{error}</div>
        )}
      </div>

      {confirmCancel && (
        <ConfirmDialog
          title="Несохранённые изменения"
          subtitle="В настройках роли есть правки, которые не записаны в слой. Выйти без сохранения?"
          confirmLabel="Выйти"
          cancelLabel="Остаться"
          confirmVariant="danger"
          onConfirm={() => { setConfirmCancel(false); onBack(); }}
          onCancel={() => setConfirmCancel(false)}
        />
      )}
    </div>
  );
}

// === Секция: плоская, с верхним разделителем ===
function Section({ children }: { children: React.ReactNode }): React.ReactElement {
  return <div style={{ borderTop: `1px solid ${C.borderLight}`, paddingTop: 22 }}>{children}</div>;
}

// === Подпись под полем (мелкий текст C.textMuted) ===
function Hint({ children }: { children: React.ReactNode }): React.ReactElement | null {
  if (!children) return null;
  return (
    <span style={{
      fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans,
      lineHeight: 1.5, display: 'block', marginTop: 8,
    }}>{children}</span>
  );
}

// === Шапка-тулбар роли ===
function ToolbarRow({ accent, roleKey, roleLabel, roleAvatar, isMobile, tab, onTab, canSave, saving, dirty, onBack, onCancel, onSave }: {
  accent: string;
  roleKey: string;
  roleLabel: string;
  roleAvatar: React.ReactNode;
  isMobile: boolean;
  tab: RoleTab;
  onTab: (t: RoleTab) => void;
  canSave: boolean;
  saving: boolean;
  dirty: boolean;
  onBack: () => void;
  onCancel: () => void;
  onSave: () => void;
}): React.ReactElement {
  const saveLabel = saving ? 'Сохраняю…' : 'Сохранить';
  return (
    <Toolbar
      isMobile={isMobile}
      noBorder
      bg="transparent"
      style={{ borderLeft: `3px solid ${accent}` }}
    >
      {/* Стрелка «Назад» — возврат к визитке роли */}
      <ToolbarIconButton onClick={onBack} title="Назад" isMobile={isMobile}>
        <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
      </ToolbarIconButton>
      {roleAvatar}
      {/* На мобиле имя роли уже несёт шапка экрана (PersonasPage). В тулбаре
          его рисовать не надо — текст сжимается и дублирует шапку. */}
      {!isMobile ? (
        <div style={{
          flex: 1, minWidth: 140, display: 'flex', flexDirection: 'column', gap: 1,
        }}>
          <div style={{
            fontFamily: FONT.serif, fontSize: FS.h1, fontWeight: 600,
            color: accent, letterSpacing: '-0.01em', lineHeight: 1.25,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{roleLabel}</div>
          {/* Ключ роли — единственное, что было только в hero; здесь он остаётся
              на месте подписи (у визитки на этой строке живёт описание). */}
          <div style={{
            fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>ключ: {roleKey}</div>
        </div>
      ) : (
        <div style={{ flex: 1, minWidth: 0 }} />
      )}
      {/* Сегмент разделов «Общая информация | Умения» — симметрично визитке роли:
          на десктопе живёт в строке тулбара между именем и кнопками, на мобиле
          уходит своей строкой во всю ширину (Toolbar переносит детей). */}
      <div style={{
        display: 'flex', minWidth: 0,
        flex: isMobile ? '1 0 100%' : '0 0 auto',
        overflowX: 'auto',
      }}>
        <PillSwitch<RoleTab>
          value={tab}
          onChange={onTab}
          options={TAB_OPTIONS}
          compact={isMobile}
          isMobile={isMobile}
        />
      </div>
      {/* Кнопки действий — как у PersonaToolbar: точка dirty + primary «Сохранить»,
          слева ghost «Отмена». Общие для обоих разделов: черновик формы один. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
        <button type="button" onClick={onCancel} style={tbBtnGhost}>Отмена</button>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
          {dirty && !saving && (
            <span
              title="Есть несохранённые изменения"
              style={{ width: 7, height: 7, borderRadius: R.full, background: accent, flexShrink: 0 }}
            />
          )}
          <button
            type="button"
            onClick={onSave}
            disabled={!canSave}
            style={{
              ...tbBtnPrimary,
              opacity: !canSave ? 0.55 : 1,
              cursor: !canSave ? 'default' : 'pointer',
            }}
          >{saveLabel}</button>
        </div>
      </div>
    </Toolbar>
  );
}

// === Инструменты: чипы + опция «Все» ===
function ToolsRow({ value, onChange }: {
  value: string[] | null;
  onChange: (v: string[] | null) => void;
}): React.ReactElement {
  // tools === null — «Все возможности». Тогда все чипы подсвечены как включённые.
  const active = value === null ? ALL_TOOLS.slice() : (value as ToolKey[]);
  const toggle = (key: ToolKey) => {
    if (value === null) {
      // Было «все», сейчас щёлкнули по одному — оставляем только остальные
      const next = ALL_TOOLS.filter(k => k !== key);
      onChange(next.length === ALL_TOOLS.length ? null : next);
    } else if (active.includes(key)) {
      const next = active.filter(k => k !== key);
      onChange(next.length === 0 ? [] : next);
    } else {
      const next = [...active, key];
      onChange(next.length === ALL_TOOLS.length ? null : next);
    }
  };
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
      {TOOL_KEYS.map(k => {
        const on = active.includes(k);
        return (
          <button
            key={k}
            type="button"
            onClick={() => toggle(k)}
            className="cc-tools-row-btn"
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 6,
              padding: '6px 12px', borderRadius: R.pill, cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
              border: `1px solid ${on ? C.accent : C.border}`,
              background: on ? C.accentLight : C.bgWhite,
              color: on ? C.accent : C.textSecondary,
              transition: 'background 0.12s, border-color 0.12s',
              outline: 'none',
            }}
          >
            {on && <span aria-hidden style={{
              width: 6, height: 6, borderRadius: R.full, background: C.accent,
            }} />}
            {TOOL_LABEL[k]}
          </button>
        );
      })}
    </div>
  );
}

// === Модели по уровням — три RoutePicker ===
function TierModelsSection({ roleKey, strong, medium, weak, onStrong, onMedium, onWeak }: {
  roleKey: string;
  strong: string;
  medium: string;
  weak: string;
  onStrong: (v: string) => void;
  onMedium: (v: string) => void;
  onWeak: (v: string) => void;
}): React.ReactElement {
  const isMobile = useIsMobile();
  const models = useModels();
  const presets = usePresets();
  // Превью «Сейчас пойдёт» под каждой ячейкой — резолв места chat-persona с уровнем.
  const previews = {
    strong: usePreview({ kind: 'specialty', specialtyKey: roleKey, tier: 'strong' }),
    medium: usePreview({ kind: 'specialty', specialtyKey: roleKey, tier: 'medium' }),
    weak: usePreview({ kind: 'specialty', specialtyKey: roleKey, tier: 'weak' }),
  };
  const chainCtx = { tierModels: {} as Record<ModelTierKey, string>, ollamaModel: undefined };

  const cells: Array<{ tier: ModelTierKey; label: string; value: string; onChange: (v: string) => void }> = [
    { tier: 'strong', label: TIER_TITLE.strong, value: strong, onChange: onStrong },
    { tier: 'medium', label: TIER_TITLE.medium, value: medium, onChange: onMedium },
    { tier: 'weak', label: TIER_TITLE.weak, value: weak, onChange: onWeak },
  ];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <SectionLabel>Модели по уровням</SectionLabel>
      <Hint>Пустая ячейка наследуется: правило роли → «Модели по умолчанию».</Hint>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {cells.map((c) => (
          <div key={c.tier} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{
                width: 64, flexShrink: 0, fontSize: 12.5, color: C.textSecondary,
              }}>{c.label}</span>
              <div style={{ flex: 1, minWidth: 0, display: 'flex' }}>
                <RoutePicker
                  route={c.value}
                  label={c.value ? routeDisplayLabel(c.value, presets, chainCtx) : ''}
                  models={models}
                  tierModels={chainCtx.tierModels}
                  placeholder={isMobile ? 'не задано' : 'Как «Модели по умолчанию»'}
                  showTiers={false}
                  showPresets
                  onChange={c.onChange}
                  fullWidth
                  dashed={!c.value}
                />
              </div>
            </div>
            <TierPreviewRow
              tier={c.tier}
              preview={previews[c.tier]}
              hasValue={!!c.value}
            />
          </div>
        ))}
      </div>
    </div>
  );
}

// «Сейчас пойдёт» под ячейкой уровня: резолв места chat-persona с уровнем.
// Без значения ячейки показываем превью (что реально пойдёт сейчас), чтобы
// админ видел, что унаследуется. Префикс передаём в formatEffectiveLine,
// иначе строка начинается с «Сейчас пойдёт: » и при конкатенации получался дубль.
function TierPreviewRow({ tier, preview, hasValue }: {
  tier: ModelTierKey;
  preview: ReturnType<typeof usePreview>;
  hasValue: boolean;
}): React.ReactElement {
  const text = preview
    ? formatEffectiveLine(preview, {
        tierText: `уровень «${TIER_TITLE[tier]}»`,
        prefix: hasValue ? 'Сейчас пойдёт: ' : 'Наследуется: ',
      })
    : null;
  if (!text) {
    return (
      <div style={{ paddingLeft: 72, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>
        Резолв подгружается…
      </div>
    );
  }
  return (
    <div style={{ paddingLeft: 72 }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
        padding: '4px 9px', borderRadius: R.md,
        background: C.bgPanel, border: `1px solid ${C.borderLight}`,
        display: 'inline-block', maxWidth: '100%',
      }}>{text}</div>
    </div>
  );
}

// === Стрелка «Назад» в пустом состоянии (роль не найдена) ===
function BackRow({ onBack }: { onBack: () => void }): React.ReactElement {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      <button type="button" onClick={onBack} style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
        color: C.textHeading, background: 'none', border: 'none',
        padding: 0, cursor: 'pointer',
      }}>
        <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        <span>Специальности</span>
      </button>
    </div>
  );
}