import { useState } from 'react';
import type { CSSProperties } from 'react';
import { Copy, Pencil, Plus, Trash2, X } from 'lucide-react';
import { Button, ConfirmDialog, IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { groupHeaderStyle, routeLabel, TIERS, type TierKey } from '../../components/modelProvidersShared';
import { RoutePicker } from './RoutePicker';
import { C, FS, R } from '../../lib/design';
import { ANY_SPECIALTY, cloneLayer, newPresetId, specialtyLabel } from '../../lib/specialties';
import type { ModelOption } from '../../lib/models';
import type { ModelRoutePreset, SpecialtyCatalogEntry, SpecialtySettingsLayer } from '../../types';

// Именованные пресеты правил выбора модели: список личных и общих пресетов, создание,
// переименование, дублирование, удаление и редактор правил «специальность → маршрут».
// Личные пресеты правит владелец, общие — только админ (остальным read-only).

const selectStyle: CSSProperties = {
  font: 'inherit', fontSize: FS.xs, color: C.textPrimary,
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
  padding: '5px 8px', outline: 'none', minWidth: 0, flex: 1,
};

export function PresetsTab({ catalog, globalLayer, ownerLayer, isAdmin, models,
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
  // Инлайн-переименование: какой пресет редактируется и текущее значение поля
  const [renaming, setRenaming] = useState<{ scope: 'global' | 'owner'; id: string } | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [confirmDelete, setConfirmDelete] = useState<{ scope: 'global' | 'owner'; preset: ModelRoutePreset } | null>(null);

  const layerOf = (scope: 'global' | 'owner') => scope === 'global' ? globalLayer : ownerLayer;

  const mutate = (scope: 'global' | 'owner', fn: (layer: SpecialtySettingsLayer) => void) => {
    const next = cloneLayer(layerOf(scope));
    fn(next);
    onSaveLayer(scope, next);
  };

  const createPreset = (scope: 'global' | 'owner') => {
    const id = newPresetId();
    mutate(scope, l => l.presets.push({ id, name: 'Новый пресет', description: null, rules: [] }));
    setRenaming({ scope, id });
    setRenameValue('Новый пресет');
  };

  const commitRename = () => {
    if (!renaming) return;
    const name = renameValue.trim() || 'Новый пресет';
    mutate(renaming.scope, l => {
      const p = l.presets.find(x => x.id === renaming.id);
      if (p) p.name = name;
    });
    setRenaming(null);
  };

  const deletePreset = (scope: 'global' | 'owner', id: string) => {
    mutate(scope, l => { l.presets = l.presets.filter(p => p.id !== id); });
  };

  const duplicatePreset = (preset: ModelRoutePreset) => {
    // Копия всегда в личный слой: общий пресет дублируется как личный черновик
    mutate('owner', l => l.presets.push({
      ...cloneLayer({ specialties: {}, presets: [preset] }).presets[0],
      id: newPresetId(),
      name: `${preset.name} (копия)`,
    }));
  };

  const renderCard = (scope: 'global' | 'owner', p: ModelRoutePreset) => {
    const editable = scope === 'owner' || isAdmin;
    const isRenaming = renaming?.scope === scope && renaming.id === p.id;
    const busy = savingScope === scope;

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
                onClick={() => setConfirmDelete({ scope, preset: p })}>
                <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            </div>
          )}
        </div>

        {/* Правила: специальность → маршрут. У пустого пресета — приглашение добавить первое */}
        {p.rules.length === 0 ? (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>
            {editable ? 'Правил пока нет — добавьте первое кнопкой ниже.' : 'Правил пока нет.'}
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {p.rules.map((r, i) => (
              <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                {editable ? (
                  <select
                    value={r.specialty}
                    onChange={e => mutate(scope, l => {
                      const rule = l.presets.find(x => x.id === p.id)?.rules[i];
                      if (rule) rule.specialty = e.target.value;
                    })}
                    style={selectStyle}
                    aria-label="Специальность правила"
                  >
                    <option value={ANY_SPECIALTY}>{specialtyLabel(catalog, ANY_SPECIALTY)}</option>
                    {catalog.filter(e => e.key !== 'none').map(e => (
                      <option key={e.key} value={e.key}>{specialtyLabel(catalog, e.key)}</option>
                    ))}
                  </select>
                ) : (
                  <span style={{ flex: 1, minWidth: 0, fontSize: FS.xs, color: C.textSecondary,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {specialtyLabel(catalog, r.specialty)}
                  </span>
                )}
                <span style={{ color: C.textMuted, fontSize: FS.xs, flexShrink: 0 }}>→</span>
                <RoutePicker
                  route={r.route}
                  label={routeLabel(r.route, ollamaModel, tierModels)}
                  models={models}
                  tierModels={tierModels}
                  ollamaModel={ollamaModel}
                  readOnly={!editable}
                  busy={busy}
                  onChange={route => mutate(scope, l => {
                    const rule = l.presets.find(x => x.id === p.id)?.rules[i];
                    if (rule) rule.route = route;
                  })}
                />
                {editable && (
                  <IconButton size="xs" tone="muted" title="Убрать правило" disabled={busy}
                    onClick={() => mutate(scope, l => {
                      const preset = l.presets.find(x => x.id === p.id);
                      if (preset) preset.rules.splice(i, 1);
                    })}>
                    <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                )}
              </div>
            ))}
          </div>
        )}

        {editable && (
          <button
            type="button"
            disabled={busy}
            onClick={() => mutate(scope, l => {
              // Дефолтная специальность + дефолтный маршрут — правило создаётся сразу
              // валидным (пустой маршрут бэкенд отклоняет 400 «у правила пустой маршрут»)
              l.presets.find(x => x.id === p.id)?.rules.push({ specialty: ANY_SPECIALTY, route: TIERS.medium.route });
            })}
            style={{
              font: 'inherit', fontSize: FS.xs, fontWeight: 600, color: C.accent,
              background: C.accentLight, border: `1px dashed ${C.accentMuted}`,
              borderRadius: R.md, padding: '6px', cursor: busy ? 'default' : 'pointer',
              textAlign: 'center', opacity: busy ? 0.5 : 1,
            }}
          >
            + Добавить правило
          </button>
        )}
      </div>
    );
  };

  const ownerPresets = ownerLayer.presets;
  const globalPresets = globalLayer.presets;
  const isEmpty = ownerPresets.length === 0 && globalPresets.length === 0;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
        Наборы правил «специальность → модель»: собираете один раз и правите
        в одном месте, а не по каждой персоне.
      </div>

      {isEmpty ? (
        <div style={{
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          padding: '18px 14px', display: 'flex', flexDirection: 'column', gap: 10, alignItems: 'center',
          textAlign: 'center',
        }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
            Пресетов пока нет. Соберите первый — это набор правил «специальность → модель»
            под своим именем: настраиваете один раз вместо каждой персоны отдельно.
          </div>
          <Button variant="primary" size="sm" onClick={() => createPreset('owner')}>
            Создать пресет
          </Button>
        </div>
      ) : (
        <>
          {ownerPresets.length > 0 && (
            <>
              <div style={groupHeaderStyle}>Мои пресеты</div>
              {ownerPresets.map(p => renderCard('owner', p))}
            </>
          )}
          {globalPresets.length > 0 && (
            <>
              <div style={groupHeaderStyle}>Общие пресеты</div>
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
          title="Удалить пресет?"
          subtitle={`Правила из «${confirmDelete.preset.name}» перестанут действовать: там, где выбран этот пресет, всё вернётся к настройкам по умолчанию.`}
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
