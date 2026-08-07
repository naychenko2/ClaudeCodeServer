import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { ArrowDown, ArrowUp, Copy, Pencil, Plus, Trash2, X } from 'lucide-react';
import { Button, ConfirmDialog, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { groupHeaderStyle, TIERS, type TierKey } from '../../components/modelProvidersShared';
import { RoutePicker } from './RoutePicker';
import { C, FS, R } from '../../lib/design';
import { api } from '../../lib/api';
import { cloneLayer, newPresetId } from '../../lib/specialties';
import {
  chainStepLabel, chainSummary, isChainStepDimmed, placesWord, stepsWord, substitutionsWord,
  useSubstitutionBudget,
} from '../../lib/presets';
import { consumeDraftRequest } from '../../lib/modelProvidersNav';
import type { ModelOption } from '../../lib/models';
import type { ModelRoutePreset, SpecialtySettingsLayer } from '../../types';

// Именованные пресеты-цепочки (итерация 2, ADR-007): имя + описание + упорядоченный
// список шагов. Шаг — та же панель выбора, что везде (модель / уровень / локальная),
// кроме пресета (вложенность запрещена). Личные пресеты правит владелец, общие —
// только админ (остальным read-only). Новый пресет — локальный черновик: бэкенд
// отклоняет пустую цепочку, поэтому в слой он попадает с первым же шагом.

const selectStyle: CSSProperties = {
  font: 'inherit', fontSize: FS.xs, color: C.textPrimary,
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
  padding: '5px 8px', outline: 'none', minWidth: 0, flex: 1,
};

// Потолок цепочки общий с бэкендом (FallbackSettingsStore.HardMaxSubstitutions)
const MAX_STEPS = 5;

export function PresetsTab({ globalLayer, ownerLayer, isAdmin, models,
  tierModels, ollamaModel, savingScope, onSaveLayer, onGoProviders }: {
  globalLayer: SpecialtySettingsLayer;
  ownerLayer: SpecialtySettingsLayer;
  isAdmin: boolean;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
  // Переход на вкладку «Провайдеры» (ссылка «Изменить» в строке о бюджете подмен)
  onGoProviders?: () => void;
}) {
  const labelCtx = { tierModels, ollamaModel };
  // Фактический бюджет подмен — из GET /api/specialties/settings (maxSubstitutions)
  const budget = useSubstitutionBudget();

  // Инлайн-переименование: какой пресет редактируется и текущее значение поля
  const [renaming, setRenaming] = useState<{ scope: 'global' | 'owner'; id: string } | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [confirmDelete, setConfirmDelete] = useState<{ scope: 'global' | 'owner'; preset: ModelRoutePreset } | null>(null);
  // Места использования для диалога удаления: считает сервер (GET …/presets/{id}/usage)
  // в момент открытия. items null — не удалось посчитать (общая формулировка из спеки)
  const [deleteUsages, setDeleteUsages] = useState<{ loading: boolean; items: string[] | null }>({ loading: false, items: null });
  // Черновик нового пресета: живёт локально до первого шага (пустую цепочку бэкенд
  // отклонит 400 «цепочка должна быть длиной 1..5»), потом сохраняется в слой
  const [draft, setDraft] = useState<{ scope: 'global' | 'owner'; preset: ModelRoutePreset } | null>(null);

  const layerOf = (scope: 'global' | 'owner') => scope === 'global' ? globalLayer : ownerLayer;

  const mutate = (scope: 'global' | 'owner', fn: (layer: SpecialtySettingsLayer) => void) => {
    const next = cloneLayer(layerOf(scope));
    fn(next);
    onSaveLayer(scope, next);
  };

  const createPreset = (scope: 'global' | 'owner') => {
    const preset: ModelRoutePreset = { id: newPresetId(), name: 'Новый пресет', description: null, steps: [] };
    setDraft({ scope, preset });
    setRenaming({ scope, id: preset.id });
    setRenameValue(preset.name);
  };

  // Переход «Собрать цепочку…» из панели выбора модели: вкладка смонтировалась
  // по запросу — сразу начинаем черновик нового личного пресета. setTimeout, потому что
  // синхронный setState в теле эффекта запрещён правилом react-hooks/set-state-in-effect
  useEffect(() => {
    if (!consumeDraftRequest()) return;
    const t = setTimeout(() => createPreset('owner'), 0);
    return () => clearTimeout(t);
  }, []);

  // Правка черновика; fn возвращает обновлённый пресет. Первый шаг — точка сохранения:
  // черновик уезжает в слой и дальше правится как обычный пресет.
  const mutateDraft = (fn: (p: ModelRoutePreset) => ModelRoutePreset) => {
    if (!draft) return;
    const next = fn(draft.preset);
    if (next.steps.length > 0) {
      const { scope } = draft;
      setDraft(null);
      mutate(scope, l => { l.presets.push(next); });
    } else {
      setDraft({ ...draft, preset: next });
    }
  };

  const commitRename = () => {
    if (!renaming) return;
    const name = renameValue.trim() || 'Новый пресет';
    if (draft && renaming.id === draft.preset.id) {
      mutateDraft(p => ({ ...p, name }));
    } else {
      mutate(renaming.scope, l => {
        const p = l.presets.find(x => x.id === renaming.id);
        if (p) p.name = name;
      });
    }
    setRenaming(null);
  };

  // Описание коммитится на blur (инпут неконтролируемый) — иначе каждый keystroke
  // уходил бы отдельным PUT слоя
  const commitDescription = (scope: 'global' | 'owner', id: string, raw: string) => {
    const v = raw.trim() || null;
    if (draft && draft.preset.id === id) {
      if (v !== (draft.preset.description ?? null)) mutateDraft(p => ({ ...p, description: v }));
      return;
    }
    const current = layerOf(scope).presets.find(x => x.id === id);
    if (!current || v === (current.description ?? null)) return;
    mutate(scope, l => { const x = l.presets.find(y => y.id === id); if (x) x.description = v; });
  };

  const deletePreset = (scope: 'global' | 'owner', id: string) => {
    mutate(scope, l => { l.presets = l.presets.filter(p => p.id !== id); });
  };

  const duplicatePreset = (preset: ModelRoutePreset) => {
    // Копия всегда в личный слой: общий пресет дублируется как личный черновик
    const copy: ModelRoutePreset = {
      id: newPresetId(), name: `${preset.name} (копия)`,
      description: preset.description ?? null, steps: [...preset.steps],
    };
    if (copy.steps.length > 0) mutate('owner', l => { l.presets.push(copy); });
    else setDraft({ scope: 'owner', preset: copy });
  };

  // --- Правки шагов (общие для сохранённого пресета и черновика) ---
  const editSteps = (scope: 'global' | 'owner', id: string, fn: (steps: string[]) => string[]) => {
    if (draft && draft.preset.id === id) {
      mutateDraft(p => ({ ...p, steps: fn(p.steps) }));
      return;
    }
    mutate(scope, l => {
      const p = l.presets.find(x => x.id === id);
      if (p) p.steps = fn(p.steps);
    });
  };

  const moveStep = (scope: 'global' | 'owner', id: string, i: number, dir: -1 | 1) =>
    editSteps(scope, id, steps => {
      const j = i + dir;
      if (j < 0 || j >= steps.length) return steps;
      const next = [...steps];
      [next[i], next[j]] = [next[j], next[i]];
      return next;
    });

  // Открытие диалога удаления: сразу просим сервер посчитать места использования
  const askDelete = (scope: 'global' | 'owner', preset: ModelRoutePreset) => {
    setConfirmDelete({ scope, preset });
    setDeleteUsages({ loading: true, items: null });
    api.models.presetUsage(preset.id)
      .then(r => setDeleteUsages({
        loading: false,
        // Серверные подписи с заглавной — в середине фразы читается лучше со строчной
        items: r.usages.map(u => u.label.charAt(0).toLowerCase() + u.label.slice(1)),
      }))
      .catch(() => setDeleteUsages({ loading: false, items: null }));
  };

  // Текст диалога удаления (спека, блок 6): со списком мест / «нигде не выбран» /
  // общая форма, когда посчитать не удалось
  const deleteSubtitle = ((): string => {
    const name = confirmDelete?.preset.name ?? '';
    if (deleteUsages.loading || deleteUsages.items === null)
      return 'Там, где выбран этот пресет, всё вернётся к настройке по умолчанию.';
    if (deleteUsages.items.length === 0)
      return `Пресет «${name}» нигде не выбран — удаление ничего не затронет.`;
    return `Он выбран в ${placesWord(deleteUsages.items.length)}: ${deleteUsages.items.join(', ')}. Там всё вернётся к настройке по умолчанию.`;
  })();

  const renderCard = (scope: 'global' | 'owner', p: ModelRoutePreset) => {
    const editable = scope === 'owner' || isAdmin;
    const isRenaming = renaming?.scope === scope && renaming.id === p.id;
    const busy = savingScope === scope;
    const hasTierStep = p.steps.some(s => s.startsWith('tier:') || s === 'claude' || s === 'default');
    const overBudget = p.steps.length > budget + 1;

    return (
      <div key={`${scope}:${p.id}`} style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: 8,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
          {isRenaming ? (
            <input
              autoFocus
              value={renameValue}
              onChange={e => setRenameValue(e.target.value)}
              onBlur={commitRename}
              onKeyDown={e => { if (e.key === 'Enter') commitRename(); if (e.key === 'Escape') setRenaming(null); }}
              style={{ ...selectStyle, fontSize: FS.base, fontWeight: 600, flex: 1 }}
              aria-label="Имя пресета"
            />
          ) : (
            <span style={{
              fontSize: FS.base, fontWeight: 600, color: C.textHeading,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0,
            }}>
              {p.name}
              {scope === 'global' && (
                <span style={{ marginLeft: 6, fontSize: 10, fontWeight: 700, color: C.textMuted }}>для всех</span>
              )}
            </span>
          )}
          {editable && (
            <div style={{ display: 'flex', gap: 4, flexShrink: 0 }}>
              <IconButton size="xs" tone="muted" title="Переименовать" disabled={busy}
                onClick={() => { setRenaming({ scope, id: p.id }); setRenameValue(p.name); }}>
                <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
              <IconButton size="xs" tone="muted" title="Скопировать в мои" disabled={busy}
                onClick={() => duplicatePreset(p)}>
                <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
              <IconButton size="xs" tone="muted" title="Удалить" disabled={busy}
                onClick={() => {
                  if (draft?.preset.id === p.id) setDraft(null);
                  else askDelete(scope, p);
                }}>
                <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            </div>
          )}
        </div>

        {/* Описание — свободная строка под именем (зачем эта цепочка) */}
        {editable ? (
          <input
            key={`${scope}:${p.id}:${p.description ?? ''}`}
            defaultValue={p.description ?? ''}
            placeholder="Описание — зачем эта цепочка"
            onBlur={e => commitDescription(scope, p.id, e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
            style={{ ...selectStyle, color: C.textSecondary }}
            aria-label="Описание пресета"
          />
        ) : (
          p.description && (
            <div style={{ fontSize: FS.xs, color: C.textMuted }}>{p.description}</div>
          )
        )}

        {/* Сводка цепочки: имя пресета само по себе ничего не говорит — порядок шагов
            виден без открытия редактора (спека, блок 2 «Наблюдаемое поведение») */}
        {p.steps.length > 0 && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
            {overBudget
              ? `${stepsWord(p.steps.length)} · обычно работают первые ${budget + 1}`
              : chainSummary(p, labelCtx)}
          </div>
        )}

        {/* Шаги цепочки по порядку; пустой список — приглашение добавить первую модель */}
        {p.steps.length === 0 ? (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>
            {editable ? 'Шагов пока нет — добавьте первую модель.' : 'Шагов пока нет.'}
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {p.steps.map((step, i) => {
              // Шаги за пределом бюджета подмен — честное предупреждение, не запрет
              const dimmed = isChainStepDimmed(i, budget);
              return (
                <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6, opacity: dimmed ? 0.55 : 1 }}>
                  <span style={{ fontSize: FS.xs, color: C.textMuted, width: 14, flexShrink: 0, textAlign: 'right' }}>
                    {i + 1}.
                  </span>
                  <RoutePicker
                    route={step}
                    label={chainStepLabel(step, labelCtx)}
                    models={models}
                    tierModels={tierModels}
                    ollamaModel={ollamaModel}
                    allowLocal
                    readOnly={!editable}
                    busy={busy}
                    onChange={v => editSteps(scope, p.id, steps => steps.map((s, j) => j === i ? v : s))}
                  />
                  {dimmed && (
                    <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0 }}>обычно не используется</span>
                  )}
                  {editable && (
                    <span style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                      <IconButton size="xs" tone="muted" title="Выше" disabled={busy || i === 0}
                        onClick={() => moveStep(scope, p.id, i, -1)}>
                        <ArrowUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                      <IconButton size="xs" tone="muted" title="Ниже" disabled={busy || i === p.steps.length - 1}
                        onClick={() => moveStep(scope, p.id, i, 1)}>
                        <ArrowDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                      <IconButton size="xs" tone="muted" title={p.steps.length === 1
                          ? 'Пустая цепочка не сохранится — удалите пресет целиком'
                          : 'Убрать шаг'}
                        disabled={busy || p.steps.length === 1}
                        onClick={() => editSteps(scope, p.id, steps => steps.filter((_, j) => j !== i))}>
                        <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </IconButton>
                    </span>
                  )}
                </div>
              );
            })}
          </div>
        )}

        {hasTierStep && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
            Уровни внутри пресета берутся из общих «Моделей по умолчанию» — настройки
            персоны и специальности сюда не заглядывают.
          </div>
        )}

        {editable && (
          <button
            type="button"
            disabled={busy || p.steps.length >= MAX_STEPS}
            title={p.steps.length >= MAX_STEPS ? 'В цепочке не больше пяти моделей' : undefined}
            onClick={() => editSteps(scope, p.id, steps => [...steps, TIERS.medium.route])}
            style={{
              font: 'inherit', fontSize: FS.xs, fontWeight: 600, color: C.accent,
              background: C.accentLight, border: `1px dashed ${C.accentMuted}`,
              borderRadius: R.md, padding: '6px',
              cursor: busy || p.steps.length >= MAX_STEPS ? 'default' : 'pointer',
              textAlign: 'center',
              opacity: busy || p.steps.length >= MAX_STEPS ? 0.5 : 1,
            }}
          >
            + Добавить модель
          </button>
        )}

        {/* Строка-итог о бюджете подмен: молчать нельзя — иначе шаг за пределом бюджета
            выглядел бы рабочим (спека, блок 3) */}
        {p.steps.length > 0 && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
            За один ответ система успевает сменить модель {substitutionsWord(budget)} —
            дальше {budget + 1}-го шага дело обычно не доходит.
            {isAdmin && onGoProviders && (
              <>
                {' '}
                <button
                  type="button"
                  onClick={onGoProviders}
                  style={{
                    font: 'inherit', fontSize: 'inherit', color: C.accent, background: 'none',
                    border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
                  }}
                >
                  Изменить
                </button>
              </>
            )}
          </div>
        )}
      </div>
    );
  };

  const ownerPresets = ownerLayer.presets;
  const globalPresets = globalLayer.presets;
  const isEmpty = ownerPresets.length === 0 && globalPresets.length === 0 && !draft;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
        Пресет — несколько моделей по порядку под одним именем. Первая не ответила —
        отвечает следующая. Выбирается везде, где продукт спрашивает модель.
      </div>

      {isEmpty ? (
        <div style={{
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          padding: '18px 14px', display: 'flex', flexDirection: 'column', gap: 10, alignItems: 'center',
          textAlign: 'center',
        }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
            Пресетов пока нет. Соберите первый: несколько моделей по порядку под своим
            именем — и выбирайте это имя вместо модели в любом месте.
          </div>
          <Button variant="primary" size="sm" onClick={() => createPreset('owner')}>
            Создать пресет
          </Button>
        </div>
      ) : (
        <>
          {(ownerPresets.length > 0 || draft?.scope === 'owner') && (
            <>
              <div style={groupHeaderStyle}>Мои пресеты</div>
              {draft?.scope === 'owner' && renderCard('owner', draft.preset)}
              {ownerPresets.map(p => renderCard('owner', p))}
            </>
          )}
          {(globalPresets.length > 0 || draft?.scope === 'global') && (
            <>
              <div style={groupHeaderStyle}>Общие пресеты</div>
              {draft?.scope === 'global' && renderCard('global', draft.preset)}
              {globalPresets.map(p => renderCard('global', p))}
            </>
          )}
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            <Button variant="ghost" size="sm" leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              disabled={savingScope !== null} onClick={() => createPreset('owner')}>
              Новый личный пресет
            </Button>
            {isAdmin && (
              <Button variant="ghost" size="sm" leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                disabled={savingScope !== null} onClick={() => createPreset('global')}>
                Новый общий пресет
              </Button>
            )}
          </div>
        </>
      )}

      {confirmDelete && (
        <ConfirmDialog
          title={`Удалить пресет «${confirmDelete.preset.name}»?`}
          subtitle={deleteSubtitle}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => {
            deletePreset(confirmDelete.scope, confirmDelete.preset.id);
            setConfirmDelete(null);
          }}
          onCancel={() => setConfirmDelete(null)}
        />
      )}
    </div>
  );
}
