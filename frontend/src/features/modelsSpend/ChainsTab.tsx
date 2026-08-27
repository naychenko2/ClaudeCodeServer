import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronDown, ChevronUp, Pencil, Plus, Trash2 } from 'lucide-react';
import { Modal, ConfirmDialog, EmptyState, InlineSegmented, Field, TextField, Button, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { ChainStepsEditor } from '../../components/ChainStepsEditor';
import { C, FONT, FS, SP, R } from '../../lib/design';
import { api } from '../../lib/api';
import { isChainStepDimmed, reloadPresetSettings,
  stepsWord, substitutionsWord, usePresets, useSubstitutionBudget } from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import { newPresetId, withNewPreset } from '../../lib/specialties';
import type { ModelOption } from '../../lib/models';
import type { TierKey } from '../../lib/modelProvidersShared';
import type { PresetUsageResponse, ScopedPreset, SpecialtySettingsLayer } from '../../types';

// === Четвёртая вкладка раздела «Модели и расход» (спекa B1–B3, C2, C4) ===
// Здесь живут цепочки целиком: список, бейдж слоя, диалоги создания/переименования/
// удаления, видимый бюджет подмен и приглушение хвостовых шагов. Чтобы useSubstitutionBudget,
// isChainStepDimmed и substitutionsWord перестали быть «мёртвым кодом» — они здесь и читаются.

const HARD_MAX_STEPS = 5;

// ADR-012 снял owner/user-слои: цепочка бывает только общей, писать её может лишь админ.
// Прежние scope-ветки звали удалённые маршруты (GET /settings/user/{id},
// PUT /settings/fallback/owner) и молча падали в 404/403.
type ChainScope = 'global';

type CreateDraft = { name: string; scope: ChainScope; steps: string[] };

export function ChainsTab({ isAdmin, contextUserId, savingScope, onSaveLayer,
  onReloadSettings, models, tierModels, ollamaModel }: {
  isAdmin: boolean;
  // Выбранный в модалке пользователь. Для цепочек больше не значим (слой один общий) —
  // проп остался ради единой сигнатуры вкладок модалки.
  contextUserId: string | null;
  // Идёт ли сейчас сохранение слоя на стороне модалки — для busy на кнопках диалогов.
  savingScope: 'global' | 'owner' | 'user' | null;
  onSaveLayer: (scope: ChainScope, reducer: LayerReducer,
    userId?: string | null) => Promise<void>;
  // Неразрушающая перечитка настроек: пикеры продолжают показывать прежние цепочки,
  // пока не пришёл новый снимок. Сигнатура void (не промис) — fire-and-forget.
  onReloadSettings: () => void;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
}) {
  void contextUserId;
  const presets = usePresets();
  const budget = useSubstitutionBudget();
  const [creating, setCreating] = useState<CreateDraft | null>(null);
  // rename — редактирование имени существующей цепочки (диалог открыт)
  const [renaming, setRenaming] = useState<{ id: string; name: string } | null>(null);
  // deleteTarget — кого удаляем; usage подгружается отдельно (модально)
  const [deleteTarget, setDeleteTarget] = useState<{ id: string; name: string } | null>(null);
  const [deleteUsage, setDeleteUsage] = useState<PresetUsageResponse | null>(null);
  const [deleteUsageLoading, setDeleteUsageLoading] = useState(false);
  // Развёрнутые цепочки: показывают шаги редактором, по умолчанию свёрнуты
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  const openDelete = (target: { id: string; name: string }) => {
    setDeleteUsage(null);
    setDeleteUsageLoading(true);
    setDeleteTarget(target);
    // Места использования идут отдельным запросом — для случая 0 мест показываем
    // нейтральный текст, а не «она используется в 0 местах»
    api.models.presetUsage(target.id)
      .then(d => setDeleteUsage(d))
      .catch(() => setDeleteUsage(null))
      .finally(() => setDeleteUsageLoading(false));
  };

  const handleCreate = async (draft: CreateDraft) => {
    // Цепочка общая, писать может только админ. На уровне контрола кнопка уже спрятана
    // от не-админа; проверка — страховка от рассинхронизации UI с правом.
    if (!isAdmin) return;
    if (draft.name.trim() === '' || draft.steps.length === 0) return;
    const id = newPresetId();
    // Редьюсер «над» cur (стор уже выбрал актуальный слой), дописываем пресет.
    // После успеха стор шлёт PUT и фиксирует снимок.
    await onSaveLayer('global', (cur) => withNewPreset(cur, id, draft.name.trim(), draft.steps));
    setCreating(null);
  };

  const handleRename = async (id: string, name: string) => {
    const trimmed = name.trim();
    if (trimmed === '') return;
    const sourceSteps = presets.find(p => p.id === id)?.steps ?? [];
    await onSaveLayer('global', (cur) => {
      const next = JSON.parse(JSON.stringify(cur)) as SpecialtySettingsLayer;
      const i = next.presets.findIndex(p => p.id === id);
      if (i === -1) return cur;
      next.presets[i] = { id, name: trimmed, description: null, steps: sourceSteps };
      return next;
    });
    setRenaming(null);
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await onSaveLayer('global', (cur) => {
      const next = JSON.parse(JSON.stringify(cur)) as SpecialtySettingsLayer;
      const i = next.presets.findIndex(p => p.id === deleteTarget.id);
      if (i === -1) return cur;
      next.presets.splice(i, 1);
      return next;
    });
    setDeleteTarget(null);
    setDeleteUsage(null);
  };

  // Изменение бюджета: успех → перечитать снэпшот настроек, чтобы useSubstitutionBudget
  // обновился без отдельной дороги store'а; снимаем значение нельзя — потолок задаётся
  // глобально (спека) и ползунок 1..5. Ошибка — откат через onReloadSettings.
  // Бюджет теперь только общий: PUT /settings/fallback/owner удалён вместе с owner-слоем,
  // поэтому у не-админа ползунка нет вовсе (прежде он давал 404 и молчаливый откат).
  const handleBudgetChange = async (next: number) => {
    if (next === budget || !isAdmin) return;
    try {
      await api.specialties.setMaxSubstitutions('global', next);
      reloadPresetSettings();
    } catch {
      // откат через onReloadSettings — на ошибке перечитаем прежний снэпшот
      await onReloadSettings();
    }
  };

  const dimChainSteps = (steps: string[]): boolean[] => steps.map((_, i) => isChainStepDimmed(i, budget));
  const stepNumbersToWarn = (steps: string[]): number[] => {
    // Предупреждение показывается по правилу спеки: (шагов − 1) > эффективного бюджета.
    // Один раз на цепочку, по ВЕРХНЕЙ границе «мёртвого хвоста»
    const liveUpTo = budget + 1;
    if (steps.length - 1 > budget && steps.length > liveUpTo) return [steps.length];
    return [];
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, paddingBottom: SP.xl }}>
      <HeaderBlock budget={budget} canEdit={isAdmin}
        onBudgetChange={handleBudgetChange} />

      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap' }}>
        <div style={{
          fontSize: FS.lg, fontFamily: FONT.serif, fontWeight: 600, color: C.textHeading,
          letterSpacing: '-0.01em',
        }}>
          Цепочки
        </div>
        {/* Цепочка общая, пишет её только админ — не-админу кнопка гарантированно
            вернула бы 403, поэтому её нет вовсе. */}
        {isAdmin && (
          <Button
            size="sm"
            variant="primary"
            leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            onClick={() => setCreating({
              name: '', scope: 'global', steps: ['strong:default', 'medium:default'],
            })}
          >
            Новая цепочка
          </Button>
        )}
      </div>

      {presets.length === 0 ? (
        <EmptyState
          icon={<ChainIcon />}
          title="Цепочек пока нет"
          subtitle={isAdmin
            ? 'Соберите первую — она подстрахует ход, если основная модель не ответит.'
            : 'Цепочки общие: их собирает администратор.'}
        />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
          {presets.map(p => (
            <ChainCard
              key={p.id}
              preset={p}
              isAdmin={isAdmin}
              expanded={!!expanded[p.id]}
              dim={dimChainSteps(p.steps)}
              morgueStep={stepNumbersToWarn(p.steps)[0]}
              budget={budget}
              models={models}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              savingScope={savingScope}
              onToggleExpand={() => setExpanded(s => ({ ...s, [p.id]: !s[p.id] }))}
              onRename={() => setRenaming({ id: p.id, name: p.name })}
              onDelete={() => openDelete({ id: p.id, name: p.name })}
              onSaveSteps={(steps) => onSaveLayer('global', (cur) => {
                const next = JSON.parse(JSON.stringify(cur)) as SpecialtySettingsLayer;
                const i = next.presets.findIndex(x => x.id === p.id);
                if (i === -1) return cur;
                next.presets[i] = { ...p, steps };
                return next;
              }).catch(() => {})}
            />
          ))}
        </div>
      )}

      {creating && (
        <CreateChainDialog
          draft={creating}
          models={models}
          tierModels={tierModels}
          ollamaModel={ollamaModel}
          onChange={setCreating}
          onCancel={() => setCreating(null)}
          onSubmit={() => handleCreate(creating)}
          busy={savingScope !== null}
        />
      )}

      {renaming && (
        <RenameChainDialog
          initialName={renaming.name}
          onCancel={() => setRenaming(null)}
          onSubmit={(name) => handleRename(renaming.id, name)}
          busy={savingScope !== null}
        />
      )}

      {deleteTarget && (
        <ConfirmDialog
          title={`Удалить цепочку «${deleteTarget.name}»?`}
          subtitle={deleteUsageBody(deleteUsage, deleteUsageLoading)}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onCancel={() => { setDeleteTarget(null); setDeleteUsage(null); }}
          onConfirm={handleDelete}
        />
      )}
    </div>
  );
}

// === Подзаголовок + бюджет подмен ===
function HeaderBlock({ budget, canEdit, onBudgetChange }: {
  budget: number;
  // Бюджет подмен после ADR-012 только общий (PUT /settings/fallback/global, admin-only).
  // Не-админу показываем значение текстом: прежний ползунок звал снятый .../fallback/owner
  // и молча откатывался.
  canEdit: boolean;
  onBudgetChange: (v: number) => void;
}) {
  const [showHint, setShowHint] = useState(false);
  const options = [1, 2, 3, 4, 5].map(v => ({ value: String(v), label: String(v) }));
  const body = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.4px',
      }}>
        Подмен за один ход
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        {canEdit ? (
          <InlineSegmented
            value={String(budget)}
            options={options}
            onChange={(v) => onBudgetChange(Number(v))}
          />
        ) : (
          <span style={{
            fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 700, color: C.textHeading,
          }}>{budget}</span>
        )}
        <span style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
          {substitutionsWord(budget)} · первый шаг — не подмена: цепочка из {budget + 1} шага
          отработает целиком
        </span>
      </div>
    </div>
  );
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{
        fontFamily: FONT.serif, fontSize: 21, fontWeight: 500, color: C.textHeading,
        letterSpacing: '-0.01em',
      }}>
        Цепочки
      </div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
        Запасной путь хода: если модель не ответила, ход продолжит следующая.
      </div>
      <div
        onMouseEnter={() => setShowHint(true)}
        onMouseLeave={() => setShowHint(false)}
        onFocus={() => setShowHint(true)}
        onBlur={() => setShowHint(false)}
        style={{ marginTop: SP.xs }}
      >
        {body}
        {showHint && (
          <div style={hintStyle}>
            Бюджет считает только смены поставщика. Первый шаг цепочки — базовая модель уровня, не
            подмена. Цепочка из 5 шагов с бюджетом 4 отрабатывает целиком.
          </div>
        )}
      </div>
    </div>
  );
}

const hintStyle: CSSProperties = {
  marginTop: 8,
  padding: '10px 12px',
  background: C.bgPanel,
  border: `1px solid ${C.border}`,
  borderRadius: R.md,
  fontSize: FS.xs,
  color: C.textSecondary,
  lineHeight: 1.5,
};

// === Карточка одной цепочки (B1, B2, C2, C4) ===
function ChainCard({ preset, isAdmin, expanded, dim, morgueStep, budget, models, tierModels,
  ollamaModel, savingScope, onToggleExpand, onRename, onDelete, onSaveSteps }: {
  preset: ScopedPreset;
  isAdmin: boolean;
  expanded: boolean;
  dim: boolean[];
  morgueStep?: number;
  budget: number;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | 'user' | null;
  onToggleExpand: () => void;
  onRename: () => void;
  onDelete: () => void;
  onSaveSteps: (steps: string[]) => Promise<void>;
}) {
  const [steps, setSteps] = useState<string[]>(preset.steps);
  const [dirty, setDirty] = useState(false);
  // Правка шагов общей цепочки админом — с подтверждением (спека A5)
  const [confirmSharedSave, setConfirmSharedSave] = useState(false);
  // Сбросить черновик при смене исходных шагов (например, после правки слоя)
  useEffect(() => { setSteps(preset.steps); setDirty(false); }, [preset.steps]);

  // Цепочка общая всегда (ADR-012), править её может только админ.
  const canEditSteps = isAdmin;
  const busy = savingScope !== null;

  const commitSave = () => {
    if (steps.length === 0) return;
    setConfirmSharedSave(true);
  };

  const cardDimWrap: CSSProperties = { borderRadius: R.lg, border: `1px solid ${C.border}`,
    background: C.bgWhite, padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm };

  return (
    <div style={cardDimWrap}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <button
          type="button"
          onClick={onToggleExpand}
          title={expanded ? 'Свернуть шаги' : 'Развернуть шаги'}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 6, padding: '4px 6px',
            background: 'transparent', border: 'none', cursor: 'pointer', color: C.textSecondary,
            fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600,
          }}
        >
          {expanded
            ? <ChevronUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            : <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          <span>{preset.name}</span>
        </button>
        <ScopeBadge />
        <span style={{ flex: 1 }} />
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>
          {stepsWord(preset.steps.length)}
        </span>
        {canEditSteps && (
          <span style={{ display: 'inline-flex', gap: 4 }}>
            <IconButton size="xs" tone="muted" title="Переименовать" onClick={onRename} disabled={busy}>
              <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton size="xs" tone="muted" title="Удалить" onClick={onDelete} disabled={busy}>
              <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </span>
        )}
      </div>

      {!expanded && (
        <CompactStepPreview steps={preset.steps} dim={dim} budget={budget}
          models={models} tierModels={tierModels} ollamaModel={ollamaModel} />
      )}

      {expanded && canEditSteps && (
        <ChainStepsEditor
          steps={steps}
          onChange={(next) => { setSteps(next); setDirty(true); }}
          models={models}
          tierModels={tierModels}
          ollamaModel={ollamaModel}
          busy={busy}
          maxSteps={HARD_MAX_STEPS}
        />
      )}

      {expanded && canEditSteps && dirty && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
          borderTop: `1px dashed ${C.border}`, paddingTop: SP.sm }}>
          <span style={{ fontSize: FS.xs, color: C.warningText, fontWeight: 600 }}>
            Шаги изменены · не записаны
          </span>
          <span style={{ flex: 1 }} />
          <Button size="sm" variant="ghost" disabled={busy} onClick={() => { setSteps(preset.steps); setDirty(false); }}>
            Отменить
          </Button>
          <Button size="sm" variant="primary" disabled={busy || steps.length === 0} onClick={commitSave}>
            Сохранить шаги
          </Button>
        </div>
      )}

      {morgueStep && (
        <div style={{
          padding: '8px 10px', borderRadius: R.md,
          background: C.warningBg, color: C.warningText,
          border: `1px solid ${C.warningBg}`,
          fontSize: FS.xs, lineHeight: 1.5,
        }}>
          Шаг {morgueStep} не успеет сработать — подмен за ход: {budget}, сработают шаги 1–{budget + 1}.
          Уберите лишний шаг или поднимите бюджет.
        </div>
      )}

      {confirmSharedSave && (
        <ConfirmDialog
          title="Сохранить шаги для всех?"
          subtitle="Цепочка общая. Изменение увидят все пользователи."
          confirmLabel="Сохранить для всех"
          confirmVariant="primary"
          onCancel={() => setConfirmSharedSave(false)}
          onConfirm={() => {
            setConfirmSharedSave(false);
            void onSaveSteps(steps).then(() => setDirty(false));
          }}
        />
      )}
    </div>
  );
}

// === Свёрнутая сводка шагов: показывает все шаги, но за пределами бюджета — приглушённые ===
function CompactStepPreview({ steps, dim, budget, models, tierModels, ollamaModel }: {
  steps: string[];
  dim: boolean[];
  budget: number;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
}) {
  void models; void budget;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', lineHeight: 1.4 }}>
      {steps.map((s, i) => {
        const isDim = dim[i];
        return (
          <span key={i} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
            <span style={{
              fontSize: FS.xs, color: isDim ? C.textMuted : C.textSecondary,
              opacity: isDim ? 0.6 : 1, fontStyle: isDim ? 'italic' : 'normal',
            }}>
              <span style={{ color: C.textMuted, marginRight: 4 }}>{i + 1}</span>
              {stepInlineLabel(s, { tierModels, ollamaModel })}
            </span>
            {i < steps.length - 1 && (
              <span style={{ color: isDim || dim[i + 1] ? C.textMuted : C.accent, opacity: isDim ? 0.6 : 1 }}>→</span>
            )}
          </span>
        );
      })}
    </div>
  );
}

function stepInlineLabel(step: string, ctx: { tierModels: Record<TierKey, string>; ollamaModel?: string }): string {
  // Короткая подпись для сводки: уровень — «Средняя»; модель — displayName или короткий id.
  // Достаточно для визуального отличия шагов в свёрнутом виде — полная подпись под ярлыком RoutePicker'а при раскрытии.
  const s = step.trim();
  if (s === 'local') return ctx.ollamaModel ? `Локальная · ${ctx.ollamaModel}` : 'Локальная';
  const tierMatch = s.match(/^(strong|medium|weak):(default|current)$/);
  if (tierMatch) {
    const tierName = tierMatch[1] === 'strong' ? 'Сильная' : tierMatch[1] === 'medium' ? 'Средняя' : 'Слабая';
    return tierName;
  }
  return step;
}

function ScopeBadge() {
  // Слоёв больше нет (ADR-012): любая цепочка общая — подпись одна.
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center',
      padding: '2px 8px', borderRadius: R.max,
      background: C.accentLight,
      color: C.accent,
      fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans,
    }}>
      Для всех
    </span>
  );
}

// === Диалог создания цепочки (B1) ===
function CreateChainDialog({ draft, models, tierModels, ollamaModel, onChange,
  onCancel, onSubmit, busy }: {
  draft: CreateDraft;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  onChange: (next: CreateDraft) => void;
  onCancel: () => void;
  onSubmit: () => void;
  busy: boolean;
}) {
  const nameEmpty = draft.name.trim() === '';
  const noSteps = draft.steps.length === 0;
  const stepNames = draft.steps.map((_, i) => `Шаг ${i + 1}`);
  return (
    <Modal title="Новая цепочка" width={520} onClose={onCancel}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <Field label="Имя">
          <TextField
            value={draft.name}
            onChange={(v) => onChange({ ...draft, name: v })}
            placeholder="Например: «Экономная, но живая»"
            autoFocus
          />
        </Field>

        <Field label="Шаги">
          <ChainStepsEditor
            steps={draft.steps}
            onChange={(next) => onChange({ ...draft, steps: next })}
            models={models}
            tierModels={tierModels}
            ollamaModel={ollamaModel}
            busy={busy}
            maxSteps={HARD_MAX_STEPS}
            addLabel="Добавьте первый шаг →"
          />
        </Field>

        {stepNames.length > 0 && (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>
            Будет {stepNames.length === 1 ? '1 шаг' : `${stepNames.length} шага`} в общем слое —
            {' '}цепочку увидят все.
          </div>
        )}

        <div style={{ display: 'flex', gap: SP.sm, justifyContent: 'flex-end' }}>
          <Button variant="ghost" size="md" disabled={busy} onClick={onCancel}>Отмена</Button>
          <Button variant="primary" size="md" disabled={busy || nameEmpty || noSteps} onClick={onSubmit}>
            Создать цепочку
          </Button>
        </div>
      </div>
    </Modal>
  );
}

// === Диалог переименования (B1) ===
function RenameChainDialog({ initialName, onCancel, onSubmit, busy }: {
  initialName: string;
  onCancel: () => void;
  onSubmit: (name: string) => void;
  busy: boolean;
}) {
  const [name, setName] = useState(initialName);
  useEffect(() => { setName(initialName); }, [initialName]);
  return (
    <Modal title="Переименовать цепочку" width={420} onClose={onCancel}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <Field label="Имя">
          <TextField
            value={name}
            onChange={setName}
            placeholder="Например: «Экономная, но живая»"
            autoFocus
            onEnter={() => onSubmit(name)}
          />
        </Field>
        <div style={{ display: 'flex', gap: SP.sm, justifyContent: 'flex-end' }}>
          <Button variant="ghost" size="md" disabled={busy} onClick={onCancel}>Отмена</Button>
          <Button
            variant="primary"
            size="md"
            disabled={busy || name.trim() === '' || name.trim() === initialName.trim()}
            onClick={() => onSubmit(name)}
          >
            Сохранить
          </Button>
        </div>
      </div>
    </Modal>
  );
}

// Тело диалога удаления: пока подгрузка — мягкая заглушка; 0 мест — честное «не используется»
function deleteUsageBody(u: PresetUsageResponse | null, loading: boolean): string {
  if (loading) return 'Проверяем, где используется цепочка…';
  if (!u || u.count === 0) return 'Она нигде не используется. Удаление ничего не сломает.';
  const labels = u.usages.map(x => x.label).join(', ');
  return `Она используется в ${u.count === 1 ? '1 месте' : `${u.count} местах`}: ${labels}. Эти места
    вернутся к наследованию — ход пойдёт по «Модели по умолчанию».`;
}

function ChainIcon() {
  return (
    <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="6" cy="12" r="2.5" />
      <circle cx="18" cy="6" r="2.5" />
      <circle cx="18" cy="18" r="2.5" />
      <path d="M8 11l8-4M8 13l8 4" />
    </svg>
  );
}
