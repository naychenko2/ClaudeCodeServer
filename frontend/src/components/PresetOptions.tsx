import { useEffect, useState } from 'react';
import { Link2 } from 'lucide-react';
import { QuickOptionCard } from './QuickOptionCard';
import { ChainStepsEditor } from './ChainStepsEditor';
import { Button } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { C, FONT } from '../lib/design';
import { chainSummary, findPreset, presetIdOf, presetRoute, usePresets, type ChainLabelContext } from '../lib/presets';
import { requestNewPreset } from '../lib/modelProvidersNav';
import { cloneLayer, newPresetId } from '../lib/specialties';
import type { ModelOption } from '../lib/models';
import type { SpecialtySettingsLayer, SpecialtySettingsResponse } from '../types';

// Доступ к слоям специальностей для inline-сборки цепочки (см. RoutePicker.presetCreation).
// Без него «Собрать цепочку…» ведёт себя по-старому — открывает раздел (PersonaForm).
export interface PresetCreationCtx {
  models: ModelOption[];
  settings: SpecialtySettingsResponse | null;
  savingScope: 'global' | 'owner' | null;
  onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => void;
}

// Группа «Пресеты» в панелях выбора модели (спека, блок 2): между карточками уровней
// и списком моделей. Пресет — третий вариант в том же выборе, отдельного контрола нет.
// Пустая группа не показывается вовсе (пока пресетов нет — интерфейс не меняется).
// scope — ограничить слой: место каталога общее для всех, поэтому «Кто что выполняет»
// показывает только общие пресеты (личный бэкенд отклонит 400 — у других пользователей
// он был бы битой ссылкой).
export function PresetOptions({ value, onPick, ctx, scope, creation, onEditingChange }: {
  value: string;
  onPick: (route: string) => void;
  ctx: ChainLabelContext;
  scope?: 'global' | 'owner';
  creation?: PresetCreationCtx;
  // Родитель (RoutePicker) должен на время inline-редактирования прятать соседние блоки
  // своей панели — иначе случайный клик по ним схлопывает панель и стирает черновик
  onEditingChange?: (editing: boolean) => void;
}) {
  const all = usePresets();
  const presets = scope ? all.filter(p => p.scope === scope) : all;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<string[]>([]);
  useEffect(() => { onEditingChange?.(editing); }, [editing, onEditingChange]);

  // Раскрытый inline-редактор — вместо списка пресетов и кнопки (решение владельца
  // 08.08.2026: «Собрать цепочку…» внутри уже открытого раздела собирает цепочку
  // на месте, а не открывает раздел заново). Проверяем editing первым, чтобы состояние
  // не зависело от гонки с presets.length (см. ниже)
  if (editing && creation) {
    const savePreset = () => {
      if (!creation.settings || draft.length === 0) return;
      const copy = { id: newPresetId(), name: `Цепочка ${all.length + 1}`, description: null, steps: draft };
      const next = cloneLayer(creation.settings.owner);
      next.presets.push(copy);
      creation.onSaveLayer('owner', next);
      onPick(presetRoute(copy.id));
      setEditing(false);
    };
    const busy = creation.savingScope !== null;
    return (
      <>
        <div style={{
          fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
          textTransform: 'uppercase', letterSpacing: '0.4px', margin: '2px 0 0',
        }}>
          Новая цепочка
        </div>
        <ChainStepsEditor
          steps={draft}
          onChange={setDraft}
          models={creation.models}
          tierModels={ctx.tierModels}
          ollamaModel={ctx.ollamaModel}
          busy={busy}
        />
        <div style={{ display: 'flex', gap: 8, marginTop: 2 }}>
          <Button size="sm" variant="ghost" disabled={busy} onClick={() => setEditing(false)}>Отмена</Button>
          <Button size="sm" variant="primary" disabled={busy || draft.length === 0} onClick={savePreset}>Сохранить</Button>
        </div>
      </>
    );
  }

  // В контексте «только общие» (места каталога) личные пресеты скрыты фильтром — без
  // подсказки это выглядело как «пресетов нет вовсе» (дефект приёмки 19d8f18e)
  const hiddenByScope = scope === 'global' && all.length > presets.length;
  const scopeNote = (
    <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
      Местам доступны только общие пресеты — личные здесь не показываются.
    </div>
  );
  if (presets.length === 0) return hiddenByScope ? scopeNote : null;
  const activeId = presetIdOf(value);

  const startEditing = () => {
    if (!creation) { requestNewPreset(); return; }
    setDraft(findPreset(all, presetIdOf(value))?.steps ?? []);
    setEditing(true);
  };

  return (
    <>
      <div style={{
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.4px', margin: '2px 0 0',
      }}>
        Пресеты
      </div>
      {presets.map(p => (
        <div key={p.id} style={{ position: 'relative' }}>
          <QuickOptionCard
            title={p.name}
            subtitle={chainSummary(p, ctx)}
            active={activeId === p.id}
            onClick={() => onPick(presetRoute(p.id))}
          />
          <Link2
            size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ position: 'absolute', top: 8, right: 10, color: C.textMuted, pointerEvents: 'none' }}
          />
        </div>
      ))}
      {hiddenByScope && scopeNote}
      {/* Вместо инлайн-цепочки «только для этого места» — переход в редактор пресетов
          (спека, расхождение п.2). В контексте «только общие» (места каталога) не
          показываем: кнопка начинает ЛИЧНЫЙ черновик, а месту он всё равно не годится */}
      {scope !== 'global' && (
        <button
          type="button"
          onClick={startEditing}
          style={{
            alignSelf: 'flex-start', font: 'inherit', fontSize: 12, fontWeight: 600,
            color: C.accent, background: 'none', border: 'none', padding: '2px 2px',
            cursor: 'pointer', fontFamily: FONT.sans,
          }}
        >
          Собрать цепочку…
        </button>
      )}
    </>
  );
}
