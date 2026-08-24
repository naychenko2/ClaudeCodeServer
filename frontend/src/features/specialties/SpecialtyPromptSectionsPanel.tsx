// Панель роли для раздела «Инструкции для роли» во вкладке «Правила» диалога
// «Модели и расход» (плана «Секции промптов», этап 4 — фронт).
//
// Компоновка под карточкой специальности (v4 макета): переключатель слоя один,
// на уровне вкладки; здесь — только индикатор слоя (read-only бейдж) и карточки
// пресетов + блок «Типовые умения». Автосохранение с debounce 350мс — паттерн
// SpecialRulesTab (без явной кнопки «Сохранить»). Read-only дефолты из нижнего
// слоя/кода: textarea показывает текст, но disabled — переопределение создаётся
// через явный override (кнопка «Задать свой текст»).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, Plus, Search, Sparkles, X } from 'lucide-react';
import type {
  PersonaBindingMode, PersonaBindingType, SpecialtyCatalogEntry, SpecialtyDefaultBinding,
  SpecialtyPromptSectionsCatalog, SpecialtySettingsLayer, SpecialtyTemplateSettings,
} from '../../types';
import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { showToast } from '../../lib/toast';
import { Button, Toggle, IconField, PillSwitch } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { SectionLabel } from '../tasks/bits';
import { Stepper, Crumb } from '../personas/stepperUi';
import {
  BINDING_TYPE_META, BINDING_TYPE_ORDER, BindingModeBadge, BindingTypeIcon,
} from '../personas/bindingMeta';
import {
  effectivePromptSection, ensureSpecialtyRecord, getPromptSectionsCatalog, loadPromptSectionsCatalog,
  newDefaultBinding, sectionsForSpecialty, sectionsOf, withDefaultBindings,
  withPromptSection, withoutPromptSection, type PromptSectionSource,
} from '../../lib/specialties';
import { useSpecialtySettings, useUserLayer, type LayerReducer } from '../../lib/presets';

// === Тексты и словарь ===

// Подпись бейджа источника значения (глобал/юзер/владелец/код). Курсивом — дефолт кода.
const SRC_BADGE: Record<PromptSectionSource, { label: string; cls: string; ital: boolean }> = {
  global: { label: 'Общее',        cls: C.textMuted,     ital: false },
  user:   { label: 'Пользователя', cls: C.textSecondary, ital: false },
  owner:  { label: 'Ваше',         cls: C.accent,        ital: false },
  code:   { label: 'Из кода',      cls: C.textMuted,     ital: true  },
};

// Подпись «Сейчас пойдёт» под текстом секции: объясняет, чьё значение применится в промпте.
const EFF_NOTE: Record<PromptSectionSource, string> = {
  global: 'Сейчас пойдёт: текст из общего слоя (настройки администратора)',
  user:   'Сейчас пойдёт: текст из слоя пользователя (выбранного администратором)',
  owner:  'Сейчас пойдёт: ваш текст',
  code:   'Сейчас пойдёт: текст из кода (дефолт)',
};

const LAYER_LABEL = { global: 'Для всех', owner: 'Только для меня', user: 'Пользователю…' } as const;
type ScopeKind = 'global' | 'owner' | 'user';

// Примеры условий для типового умения (паттерн PersonaBindingsPanel)
const CONDITION_EXAMPLES = [
  'когда спрашивают про релизы',
  'когда просят статус задач',
  'в каждом ответе про архитектуру',
];

const MODE_OPTIONS: { value: PersonaBindingMode; label: string }[] = [
  { value: 'auto',   label: 'Авто' },
  { value: 'always', label: 'Всегда' },
  { value: 'off',    label: 'Выкл' },
];

interface Props {
  isMobile: boolean;
  activeScope: ScopeKind;
  contextUserId: string | null;
  // Каталог специальностей (для имён и описаний ролей) — глобальный, не зависит от слоя
  catalog: SpecialtyCatalogEntry[] | null;
  // Занят ли сейчас saveLayer (для индикатора «Сохранение…»)
  saving: boolean;
  // Можно ли править (для disabled)
  canEdit: boolean;
  // Сохранение черновика слоя (атомарное, как SpecialRulesTab.onSaveLayer). В этой
  // панели всё считается вокруг editLayer (см. queueSave / resetDefault / overrideText),
  // поэтому редьюсер почти всегда игнорирует cur и возвращает готовый слой — отдельной
  // обработки user-scope не нужно, на уровне SpecialRulesTab.onSaveLayer это уже учтено.
  onSaveLayer: (reducer: LayerReducer) => Promise<void>;
}

// Состояние UI черновика: под каждое поле редактирования — пара «значение + что считать
// источником», чтобы видеть, чей текст/вкл сейчас активен, без ломки effectivePromptSection.
interface DraftState {
  // key = `${roleKey}:${sectionId}`
  text: Record<string, string>;
  enabled: Record<string, boolean>;
}

function draftKey(roleKey: string, sectionId: string): string { return `${roleKey}:${sectionId}`; }

export function SpecialtyPromptSectionsPanel({
  isMobile, activeScope, contextUserId,
  catalog, saving, canEdit, onSaveLayer,
}: Props) {
  // Слои читаем сами из стора — снаружи не получаем (структурный запрет этапа 1).
  const settings = useSpecialtySettings();
  const userLayerFromStore = useUserLayer(contextUserId);
  const globalLayer = settings?.global ?? null;
  const editLayer: SpecialtySettingsLayer | null = activeScope === 'global'
    ? settings?.global ?? null
    : activeScope === 'owner'
      ? settings?.owner ?? null
      : userLayerFromStore;
  // Загрузка каталога секций (один раз на сессию — модульный кэш в specialties.ts)
  const [sectionsCatalog, setSectionsCatalog] = useState<SpecialtyPromptSectionsCatalog | null>(
    () => getPromptSectionsCatalog(),
  );
  const [catalogError, setCatalogError] = useState(false);
  useEffect(() => {
    if (sectionsCatalog) return;
    let alive = true;
    loadPromptSectionsCatalog().then(c => {
      if (!alive) return;
      setSectionsCatalog(c);
      if (!c) setCatalogError(true);
    });
    return () => { alive = false; };
  }, [sectionsCatalog]);

  // Черновики редактирования (текст + enabled), debounce-сохранение в editLayer
  const [draft, setDraft] = useState<DraftState>({ text: {}, enabled: {} });
  // Открытая роль (одна) и открытая секция (одна) — UX макета v4
  const [openRoleKey, setOpenRoleKey] = useState<string | null>(null);
  // Показывать выключенные пресеты роли (кнопка из empty-state «Показать выключенные пресеты»)
  const [showDisabled, setShowDisabled] = useState<Record<string, boolean>>({});

  // Debounce сохранение: при изменении draft (text/enabled) — отложенный PUT слоя.
  // draftRef обновляется эффектом — иначе мутация ref во время рендера ломает апдейты
  // (react-hooks/refs). Сам queueSave смотрит на ref.current в момент срабатывания таймера.
  const saveTimer = useRef<number | null>(null);
  const draftRef = useRef<DraftState>({ text: {}, enabled: {} });
  useEffect(() => { draftRef.current = draft; });
  const queueSave = useCallback(() => {
    if (!editLayer || !canEdit) return;
    if (saveTimer.current) window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => {
      const layer = draftRef.current;
      let updated = editLayer;
      for (const [k, v] of Object.entries(layer.text)) {
        const [roleKey, sectionId] = k.split(':');
        const eff = effectivePromptSection(sectionsCatalog, editLayer, userLayerFromStore, globalLayer, roleKey, sectionId);
        // Не пишем, если текст совпадает с эффективным (наследование, нет override)
        if (v.trim() === eff.text) continue;
        updated = withPromptSection(updated, roleKey, sectionId, { text: v });
      }
      for (const [k, v] of Object.entries(layer.enabled)) {
        const [roleKey, sectionId] = k.split(':');
        const eff = effectivePromptSection(sectionsCatalog, editLayer, userLayerFromStore, globalLayer, roleKey, sectionId);
        if (v === eff.enabled) continue;
        updated = withPromptSection(updated, roleKey, sectionId, { enabled: v });
      }
      void onSaveLayer(() => updated);
    }, 350);
  }, [editLayer, canEdit, sectionsCatalog, userLayerFromStore, globalLayer, onSaveLayer]);
  useEffect(() => () => { if (saveTimer.current) window.clearTimeout(saveTimer.current); }, []);

  // При смене активного слоя — сбрасываем черновики (значения относятся к слою).
  // Сбросить через эффект — единственный способ отловить смену слоя ниже по дереву:
  // при ремоунте через key ломается ленивая загрузка каталога (хочется держать её между
  // переключениями слоёв), reset-функции setState не синхронизируют между сменой
  // activeScope и ручным переключением.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setDraft({ text: {}, enabled: {} });
    setOpenRoleKey(null);
  }, [activeScope, contextUserId]);

  // Список ролей для рендера — отбрасываем None
  const roles = useMemo(() => (catalog ?? []).filter(r => r.key !== 'none'), [catalog]);

  // === Обработчики секций ===
  const setText = (roleKey: string, sectionId: string, text: string) => {
    setDraft(d => ({ ...d, text: { ...d.text, [draftKey(roleKey, sectionId)]: text } }));
    queueSave();
  };
  const setEnabled = (roleKey: string, sectionId: string, enabled: boolean) => {
    setDraft(d => ({ ...d, enabled: { ...d.enabled, [draftKey(roleKey, sectionId)]: enabled } }));
    queueSave();
  };
  const resetDefault = (roleKey: string, sectionId: string) => {
    // Снимаем override в editLayer
    if (!editLayer) return;
    const next = withoutPromptSection(editLayer, roleKey, sectionId);
    void onSaveLayer(() => next);
    setDraft(d => {
      const t = { ...d.text }; delete t[draftKey(roleKey, sectionId)];
      const en = { ...d.enabled }; delete en[draftKey(roleKey, sectionId)];
      return { text: t, enabled: en };
    });
    showToast('Инструкции', 'Возвращён типовой текст');
  };
  const overrideText = (roleKey: string, sectionId: string) => {
    // Создаём override: пишем текущий эффективный текст в editLayer
    if (!editLayer) return;
    const eff = effectivePromptSection(sectionsCatalog, editLayer, userLayerFromStore, globalLayer, roleKey, sectionId);
    const next = withPromptSection(editLayer, roleKey, sectionId, { text: eff.text, enabled: eff.enabled });
    void onSaveLayer(() => next);
    setDraft(d => ({ ...d, text: { ...d.text, [draftKey(roleKey, sectionId)]: eff.text } }));
    showToast('Инструкции', 'Создано переопределение — текст можно править');
  };

  // === Обработчики типовых умений ===
  const addDefaultBinding = (roleKey: string, binding: SpecialtyDefaultBinding) => {
    if (!editLayer) return;
    const rec = ensureSpecialtyRecord(editLayer, roleKey);
    const list = (rec.defaultBindings ?? []).slice();
    list.push(binding);
    const next = withDefaultBindings(editLayer, roleKey, list);
    void onSaveLayer(() => next);
  };
  const removeDefaultBinding = (roleKey: string, idx: number) => {
    if (!editLayer) return;
    const rec = ensureSpecialtyRecord(editLayer, roleKey);
    const list = (rec.defaultBindings ?? []).slice();
    list.splice(idx, 1);
    const next = withDefaultBindings(editLayer, roleKey, list);
    void onSaveLayer(() => next);
  };
  const updateDefaultBinding = (roleKey: string, idx: number, patch: Partial<SpecialtyDefaultBinding>) => {
    if (!editLayer) return;
    const rec = ensureSpecialtyRecord(editLayer, roleKey);
    const list = (rec.defaultBindings ?? []).slice();
    list[idx] = { ...list[idx], ...patch };
    const next = withDefaultBindings(editLayer, roleKey, list);
    void onSaveLayer(() => next);
  };

  // Заголовок-разделитель (спека §1) — общий для всей панели
  return (
    <div style={{ marginTop: SP.lg }}>
      <SectionTitle>Инструкции для роли</SectionTitle>
      <div style={{
        fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5,
        margin: `${SP.xs}px 2px ${SP.sm}px`,
      }}>
        Текст инструкций добавляется в системный промпт всех персон этой специальности.
        Значения наследуются: владельцы видят и могут переопределить настройки администратора.
      </div>

      {catalogError && (
        <EmptyBox title="Не удалось загрузить секции" tone="danger">
          Проверьте соединение и попробуйте обновить страницу.
          <div style={{ marginTop: SP.sm }}>
            <Button variant="ghost" size="sm" onClick={() => {
              setCatalogError(false); setSectionsCatalog(null);
              loadPromptSectionsCatalog().then(c => setSectionsCatalog(c));
            }}>Повторить</Button>
          </div>
        </EmptyBox>
      )}

      {!catalogError && !sectionsCatalog && !catalog && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {[0, 1, 2].map(i => (
            <div key={i} style={{ height: 56, borderRadius: R.xl, background: C.bgSelected }} />
          ))}
        </div>
      )}

      {!catalogError && sectionsCatalog && catalog && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {roles.map(r => (
            <RoleCard
              key={r.key}
              role={r}
              activeScope={activeScope}
              open={openRoleKey === r.key}
              isMobile={isMobile}
              canEdit={canEdit}
              loading={!sectionsCatalog || !catalog}
              editLayer={editLayer}
              globalLayer={globalLayer}
              userLayer={userLayerFromStore}
              sectionsCatalog={sectionsCatalog}
              draft={draft}
              showDisabled={!!showDisabled[r.key]}
              saving={saving}
              onToggle={() => setOpenRoleKey(openRoleKey === r.key ? null : r.key)}
              onToggleShowDisabled={() => setShowDisabled(s => ({ ...s, [r.key]: !s[r.key] }))}
              onText={setText}
              onEnabled={setEnabled}
              onReset={resetDefault}
              onOverride={overrideText}
              onAddBinding={(b) => addDefaultBinding(r.key, b)}
              onRemoveBinding={(i) => removeDefaultBinding(r.key, i)}
              onUpdateBinding={(i, p) => updateDefaultBinding(r.key, i, p)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

// === Заголовок-разделитель (общий для панели) ===
function SectionTitle({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: FS.md, fontWeight: 700, color: C.textHeading,
      paddingBottom: SP.xs, borderBottom: `1px solid ${C.divider}`,
    }}>{children}</div>
  );
}

// === Empty-box (паттерн SpecialRulesTab.EmptyBox) ===
function EmptyBox({ title, children, tone }: {
  title: string; children: React.ReactNode; tone?: 'danger' | 'default';
}) {
  const border = tone === 'danger' ? C.dangerText : C.dashed;
  return (
    <div style={{
      border: `1px dashed ${border}`, borderRadius: R.xl,
      padding: '22px 18px', textAlign: 'center',
      color: tone === 'danger' ? C.dangerText : C.textSecondary,
      fontSize: FS.sm, lineHeight: 1.55,
    }}>
      <div style={{
        fontSize: FS.md, fontWeight: 700, marginBottom: SP.xs,
        color: tone === 'danger' ? C.dangerText : C.textHeading,
      }}>{title}</div>
      {children}
    </div>
  );
}

// === Иконка папки для шапки роли ===
function FolderIcon({ size = 16 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={ICON_STROKE} strokeLinecap="round" strokeLinejoin="round">
      <path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z" />
    </svg>
  );
}

// === Карточка роли ===
function RoleCard({
  role, activeScope, open, isMobile, canEdit, loading, editLayer, globalLayer, userLayer,
  sectionsCatalog, draft, showDisabled, saving, onToggle, onToggleShowDisabled,
  onText, onEnabled, onReset, onOverride, onAddBinding, onRemoveBinding, onUpdateBinding,
}: {
  role: SpecialtyCatalogEntry;
  activeScope: ScopeKind;
  open: boolean;
  isMobile: boolean;
  canEdit: boolean;
  loading: boolean;
  editLayer: SpecialtySettingsLayer | null;
  globalLayer: SpecialtySettingsLayer | null;
  userLayer: SpecialtySettingsLayer | null;
  sectionsCatalog: SpecialtyPromptSectionsCatalog;
  draft: DraftState;
  showDisabled: boolean;
  saving: boolean;
  onToggle: () => void;
  onToggleShowDisabled: () => void;
  onText: (roleKey: string, sectionId: string, text: string) => void;
  onEnabled: (roleKey: string, sectionId: string, enabled: boolean) => void;
  onReset: (roleKey: string, sectionId: string) => void;
  onOverride: (roleKey: string, sectionId: string) => void;
  onAddBinding: (b: SpecialtyDefaultBinding) => void;
  onRemoveBinding: (idx: number) => void;
  onUpdateBinding: (idx: number, patch: Partial<SpecialtyDefaultBinding>) => void;
}) {
  const sectionList = sectionsOf(sectionsCatalog);
  const catalogRole = sectionsForSpecialty(sectionsCatalog, role.key);
  // Подсчёт «включённых» по эффективным значениям
  const enabledCount = useMemo(() => sectionList.filter(s => {
    const eff = effectivePromptSection(sectionsCatalog, editLayer, userLayer, globalLayer, role.key, s.id);
    return eff.enabled;
  }).length, [sectionList, editLayer, userLayer, globalLayer, sectionsCatalog, role.key]);
  const editRecord: SpecialtyTemplateSettings | null = editLayer?.specialties[role.key] ?? null;
  const bindingCount = editRecord?.defaultBindings?.length
    ?? catalogRole?.defaultBindings.length ?? 0;

  // Пустое состояние для блока пресетов: все выключены
  const allOff = enabledCount === 0 && !showDisabled;

  return (
    <div style={{
      background: C.bgWhite,
      border: `1px solid ${open ? C.accent : C.border}`,
      borderRadius: R.xl,
      boxShadow: open ? SHADOW.card : 'none',
      transition: 'border-color 0.15s',
    }}>
      {/* Шапка */}
      <button
        type="button"
        onClick={onToggle}
        style={{
          display: 'flex', alignItems: 'center', gap: 12,
          width: '100%', textAlign: 'left',
          padding: isMobile ? '10px 12px' : '12px 14px',
          minHeight: 40, cursor: 'pointer',
          background: 'transparent', border: 'none',
          fontFamily: FONT.sans, color: 'inherit',
        }}
      >
        <span style={{
          width: 32, height: 32, borderRadius: R.full, flexShrink: 0,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          background: C.accentLight, color: C.accent,
        }}><FolderIcon size={16} /></span>
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{
            display: 'block', fontSize: FS.base, fontWeight: 700, color: C.textHeading,
          }}>{role.label}</span>
          <span style={{
            display: 'block', fontSize: FS.sm, color: C.textSecondary,
            marginTop: SP.xxs, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{role.description || ''}</span>
        </span>
        <span style={{
          fontSize: FS.xs, color: C.textMuted, flexShrink: 0,
        }}>{enabledCount} из {sectionList.length} пресетов · {bindingCount} умений</span>
        <span style={{
          display: 'flex', color: C.textMuted, flexShrink: 0,
          transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
        }}><ChevronDown size={16} strokeWidth={ICON_STROKE} /></span>
      </button>

      {open && !loading && (
        <div style={{
          background: C.bgCard, borderTop: `1px solid ${C.border}`,
          padding: isMobile ? '10px 10px 14px' : '12px 14px 16px',
        }}>
          {/* Индикатор слоя (read-only бейдж) */}
          <div style={{ marginBottom: SP.sm }}>
            <span style={{
              display: 'inline-flex', alignItems: 'center', gap: 6,
              fontSize: FS.xs, color: C.textSecondary,
              background: C.bgSelected, borderRadius: R.max, padding: '3px 10px',
            }}>
              <Sparkles size={12} strokeWidth={ICON_STROKE} />
              Редактируется слой: {LAYER_LABEL[activeScope]}
            </span>
          </div>

          {/* Пресеты для роли */}
          <SectionLabel style={{ marginBottom: SP.xs }}>Пресеты для роли</SectionLabel>
          {allOff ? (
            <EmptyBox title="Пресеты пока не настроены">
              Включите пресеты и задайте текст — инструкции добавятся в промпт персон этой специальности.
              <div style={{ marginTop: SP.sm }}>
                <Button variant="ghost" size="sm" onClick={onToggleShowDisabled}>
                  Показать выключенные пресеты
                </Button>
              </div>
            </EmptyBox>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, marginTop: SP.xs }}>
              {sectionList.map(s => (
                <PresetCard
                  key={s.id}
                  roleKey={role.key}
                  sectionMeta={s}
                  editLayer={editLayer}
                  globalLayer={globalLayer}
                  userLayer={userLayer}
                  sectionsCatalog={sectionsCatalog}
                  draftText={draft.text[draftKey(role.key, s.id)]}
                  draftEnabled={draft.enabled[draftKey(role.key, s.id)]}
                  canEdit={canEdit}
                  saving={saving}
                  isMobile={isMobile}
                  onText={onText}
                  onEnabled={onEnabled}
                  onReset={onReset}
                  onOverride={onOverride}
                />
              ))}
            </div>
          )}

          {/* Типовые умения */}
          <div style={{ marginTop: SP.md, paddingTop: SP.sm, borderTop: `1px solid ${C.borderLight}` }}>
            <SectionLabel style={{ marginBottom: SP.xs }}>Типовые умения</SectionLabel>
            <div style={{
              fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5, marginBottom: SP.sm,
            }}>
              При создании персоны этой роли умения добавляются автоматически.
              Существующим персонам — по кнопке «Применить типовые» на вкладке «Умения».
            </div>
            <DefaultBindingsList
              role={role}
              sectionsCatalog={sectionsCatalog}
              editLayer={editLayer}
              canEdit={canEdit}
              onAdd={onAddBinding}
              onRemove={onRemoveBinding}
              onUpdate={onUpdateBinding}
            />
          </div>
        </div>
      )}
    </div>
  );
}

// activeScope пробрасывается через пропс; шаблон не уходит в гигантский — выделено здесь.

// === Карточка секции (пресет) ===
function PresetCard({
  roleKey, sectionMeta, editLayer, globalLayer, userLayer, sectionsCatalog,
  draftText, draftEnabled, canEdit, saving, isMobile,
  onText, onEnabled, onReset, onOverride,
}: {
  roleKey: string;
  sectionMeta: { id: string; label: string; description: string };
  editLayer: SpecialtySettingsLayer | null;
  globalLayer: SpecialtySettingsLayer | null;
  userLayer: SpecialtySettingsLayer | null;
  sectionsCatalog: SpecialtyPromptSectionsCatalog;
  draftText: string | undefined;
  draftEnabled: boolean | undefined;
  canEdit: boolean;
  saving: boolean;
  isMobile: boolean;
  onText: (roleKey: string, sectionId: string, text: string) => void;
  onEnabled: (roleKey: string, sectionId: string, enabled: boolean) => void;
  onReset: (roleKey: string, sectionId: string) => void;
  onOverride: (roleKey: string, sectionId: string) => void;
}) {
  const eff = effectivePromptSection(sectionsCatalog, editLayer, userLayer, globalLayer, roleKey, sectionMeta.id);
  // Текст: черновик → эффективный (если draft нет). enabled: draft → эффективный.
  const text = draftText ?? eff.text;
  const enabled = draftEnabled !== undefined ? draftEnabled : eff.enabled;
  // Источник бейджа: enabled-источник (для тумблера) и text-источник (для подписи)
  const enabledSrc = eff.enabledSource;
  const textSrc = (draftText !== undefined && draftText !== eff.text) ? 'owner' : eff.textSource;
  // Редактируемый: если в нашем слое есть override (запись в enabled или text отличается от ниже)
  const isOverride = enabledSrc === 'owner' || textSrc === 'owner';
  const editable = canEdit && isOverride;

  const textLimit = sectionsCatalog.textLimit;
  const len = text.length;
  const cntCls = len > textLimit * 0.95 ? C.danger
    : len >= textLimit * 0.8 ? C.warning : C.textMuted;

  // Локально «раскрыт» если enabled или есть override
  const [expanded, setExpanded] = useState<boolean>(enabled || isOverride);

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      marginBottom: SP.xs, boxShadow: SHADOW.card, overflow: 'hidden',
    }}>
      {/* Шапка */}
      <div
        onClick={() => setExpanded(v => !v)}
        style={{
          display: 'flex', alignItems: 'center', gap: 10,
          padding: isMobile ? '8px 10px' : '10px 14px',
          cursor: 'pointer', flexWrap: 'wrap', minHeight: 40,
        }}
      >
        <div onClick={e => e.stopPropagation()}>
          <Toggle
            checked={enabled}
            onChange={v => onEnabled(roleKey, sectionMeta.id, v)}
            disabled={!canEdit}
          />
        </div>
        <span style={{
          flex: 1, minWidth: 120,
          fontSize: FS.base, fontWeight: 700, color: C.textHeading,
        }}>{sectionMeta.label}</span>
        <SourceBadge src={isOverride ? 'owner' : enabledSrc} />
        <span style={{
          display: 'flex', color: C.textMuted, flexShrink: 0,
          transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
        }}><ChevronDown size={15} strokeWidth={ICON_STROKE} /></span>
      </div>
      <div style={{
        fontSize: FS.sm, color: C.textSecondary,
        padding: isMobile ? '0 10px 8px 10px' : '0 14px 10px 14px',
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{sectionMeta.description}</div>

      {expanded && (
        <div style={{
          background: C.bgCard, borderTop: `1px dashed ${C.border}`,
          padding: isMobile ? '8px 10px 10px' : '10px 14px 12px',
        }}>
          {/* Панель: пресет / reset / счётчик */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
          }}>
            <span style={{
              fontSize: FS.sm, padding: '5px 9px',
              borderRadius: R.md, border: `1px solid ${C.border}`,
              background: C.bgWhite, color: C.textSecondary,
            }}>
              {isOverride ? 'Свой текст' : 'Типовой текст'}
            </span>
            {editable ? (
              <Button
                variant="ghost"
                size="sm"
                onClick={() => onReset(roleKey, sectionMeta.id)}
              >Вернуть типовой</Button>
            ) : (
              canEdit && (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => onOverride(roleKey, sectionMeta.id)}
                >Задать свой текст</Button>
              )
            )}
            <span style={{
              marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs, color: cntCls,
              fontWeight: len >= textLimit * 0.8 ? 700 : 400,
            }}>{len} / {textLimit}</span>
            {saving && (
              <span style={{
                fontSize: FS.xs, color: C.textMuted, fontStyle: 'italic',
              }}>Сохранение…</span>
            )}
          </div>

          {/* Textarea — нативный, чтобы работали maxLength/onFocus (TextArea их не принимает) */}
          <textarea
            value={text}
            onChange={e => onText(roleKey, sectionMeta.id, e.target.value)}
            maxLength={textLimit}
            disabled={!editable}
            placeholder={eff.text ? '' : 'Текст секции…'}
            onFocus={editable ? undefined : () => {
              if (!canEdit) return;
              showToast('Инструкции', 'Чтобы задать свой текст, включите пресет');
            }}
            style={{
              width: '100%', fontFamily: FONT.sans, fontSize: FS.sm,
              color: editable ? C.textHeading : C.textSecondary,
              background: editable ? C.bgWhite : C.bgSelected,
              borderRadius: R.xl, border: `1px solid ${C.border}`,
              padding: '8px 10px', resize: 'vertical',
              minHeight: isMobile ? 60 : 80, maxHeight: 220,
              outline: 'none', lineHeight: 1.5, boxSizing: 'border-box',
              cursor: editable ? 'text' : 'not-allowed',
              transition: 'border-color 0.15s, box-shadow 0.15s',
            }}
          />

          {/* Подпись «Сейчас пойдёт» */}
          <div style={{
            fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm,
          }}>{EFF_NOTE[textSrc]}</div>
        </div>
      )}
    </div>
  );
}

// === Бейдж источника ===
function SourceBadge({ src }: { src: PromptSectionSource }) {
  const m = SRC_BADGE[src];
  return (
    <span style={{
      fontSize: FS.xs, fontWeight: src === 'code' ? 400 : 600,
      padding: '2px 8px', borderRadius: R.max,
      background: C.bgSelected, color: m.cls, fontStyle: m.ital ? 'italic' : 'normal',
      flexShrink: 0, whiteSpace: 'nowrap',
    }}>{m.label}</span>
  );
}

// === Типовые умения: список + степпер добавления ===
function DefaultBindingsList({
  role, sectionsCatalog, editLayer, canEdit,
  onAdd, onRemove, onUpdate,
}: {
  role: SpecialtyCatalogEntry;
  sectionsCatalog: SpecialtyPromptSectionsCatalog;
  editLayer: SpecialtySettingsLayer | null;
  canEdit: boolean;
  onAdd: (b: SpecialtyDefaultBinding) => void;
  onRemove: (idx: number) => void;
  onUpdate: (idx: number, patch: Partial<SpecialtyDefaultBinding>) => void;
}) {
  const editRecord: SpecialtyTemplateSettings | null = editLayer?.specialties[role.key] ?? null;
  const catalogRole = sectionsForSpecialty(sectionsCatalog, role.key);
  // Эффективный список: override в слое → каталог кода
  const list: SpecialtyDefaultBinding[] = editRecord?.defaultBindings ?? catalogRole?.defaultBindings ?? [];

  const [addOpen, setAddOpen] = useState(false);
  const [confirmDelIdx, setConfirmDelIdx] = useState<number | null>(null);
  const [flashIdx, setFlashIdx] = useState<number | null>(null);

  const handleRemove = (idx: number) => {
    if (confirmDelIdx === idx) {
      onRemove(idx);
      setConfirmDelIdx(null);
      showToast('Инструкции', 'Типовое умение удалено');
      return;
    }
    setConfirmDelIdx(idx);
    window.setTimeout(() => setConfirmDelIdx(null), 3000);
  };
  const handleAdd = (b: SpecialtyDefaultBinding) => {
    onAdd(b);
    setAddOpen(false);
    setFlashIdx(list.length); // новая запись попадёт в конец
    window.setTimeout(() => setFlashIdx(null), 1200);
  };

  return (
    <>
      {list.length === 0 ? (
        <EmptyBox title="Типовых умений нет">
          Персона этой роли создаётся с пустыми умениями.
        </EmptyBox>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, marginBottom: SP.sm }}>
          {list.map((b, i) => (
            <DefaultBindingCard
              key={`${b.type}-${b.skillName ?? ''}-${i}`}
              binding={b}
              flashing={flashIdx === i}
              confirmDel={confirmDelIdx === i}
              canEdit={canEdit}
              onConditionChange={v => onUpdate(i, { condition: v })}
              onModeChange={m => onUpdate(i, { mode: m })}
              onSkillChange={name => onUpdate(i, { skillName: name })}
              onDelete={() => handleRemove(i)}
            />
          ))}
        </div>
      )}
      {!addOpen && canEdit && (
        <Button
          variant="dashed"
          size="sm"
          leftIcon={<Plus size={14} strokeWidth={ICON_STROKE} />}
          onClick={() => setAddOpen(true)}
        >
          Добавить типовое умение
        </Button>
      )}
      {addOpen && canEdit && (
        <AddBindingPanel onCancel={() => setAddOpen(false)} onCommit={handleAdd} />
      )}
    </>
  );
}

// === Карточка типового умения (паттерн PersonaBindingsPanel) ===
function DefaultBindingCard({
  binding, flashing, confirmDel, canEdit,
  onConditionChange, onModeChange, onSkillChange, onDelete,
}: {
  binding: SpecialtyDefaultBinding;
  flashing: boolean;
  confirmDel: boolean;
  canEdit: boolean;
  onConditionChange: (v: string) => void;
  onModeChange: (m: PersonaBindingMode) => void;
  onSkillChange: (name: string) => void;
  onDelete: () => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const dim = binding.mode === 'off' && !expanded;
  return (
    <div style={{
      background: flashing ? C.accentLight : C.bgWhite,
      border: `1px solid ${expanded ? C.accent : C.border}`,
      borderRadius: R.xl, padding: '10px 14px',
      transition: 'border-color 0.15s, background 0.6s',
      opacity: dim ? 0.7 : 1,
    }}>
      <div
        onClick={() => setExpanded(v => !v)}
        style={{ display: 'flex', alignItems: 'center', gap: 12, cursor: 'pointer' }}
      >
        <BindingTypeIcon type={binding.type} dim={dim} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
          }}>
            {BINDING_TYPE_META[binding.type].name}
            {binding.type === 'skill' && binding.skillName ? ` · «${binding.skillName}»` : ''}
          </div>
          <div style={{
            fontSize: FS.sm, color: binding.condition ? C.textSecondary : C.textMuted,
            marginTop: SP.xxs, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            fontStyle: binding.condition ? 'normal' : 'italic',
          }}>{binding.condition || 'Всегда под рукой — условие не задано'}</div>
        </div>
        <BindingModeBadge mode={binding.mode} />
      </div>

      {expanded && (
        <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 10, paddingTop: 12 }}>
          <SectionLabel style={{ marginBottom: SP.xs }}>Когда пользоваться</SectionLabel>
          <textarea
            value={binding.condition}
            onChange={e => onConditionChange(e.target.value)}
            maxLength={300}
            rows={2}
            placeholder="Например: когда спрашивают про релизы — читай CHANGELOG.md"
            disabled={!canEdit}
            style={{
              width: '100%', minHeight: 40, fontFamily: FONT.sans, fontSize: FS.sm,
              color: C.textHeading, background: C.bgWhite, borderRadius: R.xl,
              border: `1px solid ${C.border}`, padding: '8px 10px',
              resize: 'vertical', outline: 'none', lineHeight: 1.45, boxSizing: 'border-box',
            }}
          />
          <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap', marginTop: SP.sm }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>Примеры:</span>
            {CONDITION_EXAMPLES.map(e => (
              <Button
                key={e}
                variant="ghostFilled"
                size="xs"
                pill
                onClick={() => onConditionChange(e)}
              >{e}</Button>
            ))}
          </div>

          <SectionLabel style={{ marginTop: SP.sm, marginBottom: SP.xs }}>Режим</SectionLabel>
          <div style={{
            maxWidth: 360, opacity: canEdit ? 1 : 0.6,
            pointerEvents: canEdit ? 'auto' : 'none',
          }}>
            <PillSwitch<PersonaBindingMode>
              fill
              value={binding.mode}
              onChange={onModeChange}
              options={MODE_OPTIONS}
            />
          </div>

          {binding.type === 'skill' && canEdit && (
            <div style={{ marginTop: SP.sm }}>
              <SectionLabel style={{ marginBottom: SP.xs }}>Имя скилла</SectionLabel>
              <IconField
                value={binding.skillName ?? ''}
                onChange={onSkillChange}
                mono
                placeholder="frontend-design"
                height={38}
                radius={R.lg}
                icon={<Search size={15} strokeWidth={ICON_STROKE} />}
              />
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm }}>
                Навык привяжется к новой персоне при создании; отсутствующий в каталоге
                пропускается молча.
              </div>
            </div>
          )}

          <div style={{
            display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: SP.sm,
          }}>
            {canEdit ? (
              <button
                type="button"
                onClick={onDelete}
                style={{
                  background: 'none', border: 'none', padding: '4px 0',
                  fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                  color: confirmDel ? C.danger : C.dangerText,
                  cursor: 'pointer', textDecoration: 'underline',
                }}
              >{confirmDel ? 'Точно удалить?' : 'Удалить умение'}</button>
            ) : <span />}
            <Button variant="ghost" size="sm" onClick={() => setExpanded(false)}>
              Готово
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

// === Степпер добавления типового умения: ① Тип → ② Цель → ③ Правило ===
function AddBindingPanel({ onCancel, onCommit }: {
  onCancel: () => void;
  onCommit: (b: SpecialtyDefaultBinding) => void;
}) {
  // Шаг 1 — выбор типа
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [type, setType] = useState<PersonaBindingType | null>(null);
  const [skillName, setSkillName] = useState<string>('');
  const [skillQuery, setSkillQuery] = useState('');
  const [condition, setCondition] = useState('');
  const [mode, setMode] = useState<PersonaBindingMode>('auto');

  const reset = () => {
    setStep(1); setType(null); setSkillName(''); setSkillQuery(''); setCondition(''); setMode('auto');
  };

  return (
    <div style={{
      borderTop: `1px solid ${C.borderLight}`,
      marginTop: SP.xs, paddingTop: SP.sm,
    }}>
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        marginBottom: SP.xs,
      }}>
        <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>Добавить типовое умение</span>
        <button
          type="button"
          onClick={onCancel}
          aria-label="Закрыть"
          style={{
            width: 28, height: 28, border: 'none', background: 'transparent',
            borderRadius: R.md, color: C.textMuted, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
        ><X size={14} strokeWidth={ICON_STROKE} /></button>
      </div>

      <Stepper
        step={step}
        accent={C.accent}
        steps={[{ n: 1, label: 'Тип' }, { n: 2, label: 'Цель' }, { n: 3, label: 'Правило' }]}
        onStep={s => {
          if (s >= step) return;
          if (s === 1) { setStep(1); setType(null); setSkillName(''); setSkillQuery(''); }
          else if (s === 2) { setStep(2); setSkillName(''); }
        }}
      />

      {step === 1 && (
        <div style={{
          display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))',
          gap: 10, marginTop: SP.sm,
        }}>
          {BINDING_TYPE_ORDER.map(t => {
            const m = BINDING_TYPE_META[t];
            return (
              <button
                key={t}
                type="button"
                onClick={() => { setType(t); setStep(2); }}
                onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.background = C.bgCard; }}
                onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.background = C.bgWhite; }}
                style={{
                  textAlign: 'left', background: C.bgWhite, border: `1px solid ${C.border}`,
                  borderRadius: R.xl, padding: 12, cursor: 'pointer',
                  fontFamily: FONT.sans, transition: 'border-color 0.15s, background 0.15s',
                }}
              >
                <BindingTypeIcon type={t} />
                <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, marginTop: SP.sm }}>{m.name}</div>
                <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, marginTop: 3 }}>{m.hint}</div>
              </button>
            );
          })}
        </div>
      )}

      {step === 2 && type && (
        <>
          <Crumb onClick={() => { setStep(1); setType(null); setSkillName(''); setSkillQuery(''); }}>
            {BINDING_TYPE_META[type].name}
          </Crumb>
          {type === 'skill' ? (
            <>
              <div style={{ marginTop: SP.sm }}>
                <IconField
                  value={skillQuery}
                  onChange={setSkillQuery}
                  placeholder="Найти навык…"
                  height={38}
                  radius={R.lg}
                  icon={<Search size={15} strokeWidth={ICON_STROKE} />}
                />
              </div>
              <SkillPicker
                query={skillQuery}
                onPick={name => { setSkillName(name); setStep(3); }}
              />
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
                Навык привяжется к новой персоне при создании; отсутствующий в её каталоге
                будет пропущен молча.
              </div>
            </>
          ) : (
            <>
              <div style={{
                marginTop: SP.sm,
                background: C.infoBg, borderRadius: R.xl,
                padding: '10px 14px', display: 'flex', gap: 10, alignItems: 'flex-start',
              }}>
                <span style={{ color: C.info, flexShrink: 0, marginTop: SP.xxs }}>
                  <Sparkles size={16} strokeWidth={ICON_STROKE} />
                </span>
                <div>
                  <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                    Цель подберёт AI при создании персоны
                  </div>
                  <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xxs, lineHeight: 1.45 }}>
                    Типовое умение задаёт только тип и правило. Конкретный проект, базу знаний
                    или папку AI выберет под момент создания персоны — под свежий список её
                    проектов и источников.
                  </div>
                </div>
              </div>
              <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: SP.sm }}>
                <Button variant="primary" size="sm" onClick={() => setStep(3)}>
                  Далее
                </Button>
              </div>
            </>
          )}
        </>
      )}

      {step === 3 && type && (
        <>
          <Crumb onClick={() => setStep(2)}>
            {BINDING_TYPE_META[type].name}
            {type === 'skill' ? ` · «${skillName}»` : ' · AI подберёт'}
          </Crumb>
          <SectionLabel style={{ marginTop: SP.md, marginBottom: SP.xs }}>Когда пользоваться</SectionLabel>
          <textarea
            value={condition}
            onChange={e => setCondition(e.target.value)}
            maxLength={300}
            rows={2}
            placeholder="Например: когда спрашивают про релизы — читай CHANGELOG.md"
            style={{
              width: '100%', minHeight: 40, fontFamily: FONT.sans, fontSize: FS.sm,
              color: C.textHeading, background: C.bgWhite, borderRadius: R.xl,
              border: `1px solid ${C.border}`, padding: '8px 10px',
              resize: 'vertical', outline: 'none', lineHeight: 1.45, boxSizing: 'border-box',
            }}
          />
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm }}>
            Пусто — персона решит сама по ситуации
          </div>
          <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap', marginTop: SP.sm }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>Примеры:</span>
            {CONDITION_EXAMPLES.map(e => (
              <Button
                key={e}
                variant="ghostFilled"
                size="xs"
                pill
                onClick={() => setCondition(e)}
              >{e}</Button>
            ))}
          </div>
          <SectionLabel style={{ marginTop: SP.md, marginBottom: SP.xs }}>Режим</SectionLabel>
          <div style={{ maxWidth: 360 }}>
            <PillSwitch<PersonaBindingMode>
              fill
              value={mode}
              onChange={setMode}
              options={MODE_OPTIONS}
            />
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: SP.sm }}>
            <Button variant="ghost" size="sm" onClick={() => { reset(); onCancel(); }}>Отмена</Button>
            <Button
              variant="primary" size="sm"
              disabled={type === 'skill' && !skillName.trim()}
              onClick={() => onCommit(newDefaultBinding(type!, condition, mode, type === 'skill' ? skillName : null))}
            >
              Добавить умение
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

// Упрощённый пикер скиллов: список известных id; полный SkillSearchDialog сюда не
// встраиваем (у него свой state жизненного цикла), а поиск по подстроке перекрывает
// базовый сценарий этапа 4. Позже можно подменить на полный пикер.
function SkillPicker({ query, onPick }: {
  query: string;
  onPick: (name: string) => void;
}) {
  const q = query.trim().toLowerCase();
  const known = [
    'frontend-design', 'theme-factory', 'web-artifacts-builder', 'html',
    'web-design-guidelines', 'dataviz',
  ];
  const items = known.filter(s => !q || s.includes(q));
  if (items.length === 0) {
    return (
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: '14px 14px', fontSize: FS.sm, color: C.textMuted, marginTop: 10,
      }}>Ничего не найдено</div>
    );
  }
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      marginTop: 10, overflow: 'hidden',
    }}>
      {items.map((id, i) => (
        <Button
          key={id}
          variant="ghost"
          fullWidth
          onClick={() => onPick(id)}
          style={{
            justifyContent: 'flex-start',
            border: 'none',
            borderBottom: i < items.length - 1 ? `1px solid ${C.borderLight}` : 'none',
            borderRadius: 0,
            padding: '10px 14px',
            minHeight: 44,
            gap: 12,
          }}
        >
          <span style={{ flex: 1, minWidth: 0, textAlign: 'left' }}>
            <span style={{
              display: 'block', fontSize: FS.base, fontWeight: 600, color: C.textHeading,
              fontFamily: FONT.mono,
            }}>{id}</span>
            <span style={{
              display: 'block', fontSize: FS.sm, color: C.textMuted, marginTop: SP.xxs,
            }}>навык из реестра</span>
          </span>
        </Button>
      ))}
    </div>
  );
}
