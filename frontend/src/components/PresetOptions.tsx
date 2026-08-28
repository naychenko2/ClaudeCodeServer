import { useEffect, useState } from 'react';
import { Link2 } from 'lucide-react';
import { QuickOptionCard } from './QuickOptionCard';
import { ChainStepsEditor } from './ChainStepsEditor';
import { Button } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { C, FONT, FS } from '../lib/design';
import { chainSummary, findPreset, presetIdOf, presetRoute, usePresets, type ChainLabelContext } from '../lib/presets';
import { requestNewPreset } from '../lib/modelProvidersNav';
import { newPresetId, withNewPreset } from '../lib/specialties';
import type { ModelOption } from '../lib/models';
import type { SpecialtySettingsLayer, SpecialtySettingsResponse } from '../types';

// Доступ к слоям специальностей для inline-сборки цепочки (см. RoutePicker.presetCreation).
// Без него «Собрать цепочку…» ведёт себя по-старому — открывает раздел (PersonaForm).
export interface PresetCreationCtx {
  models: ModelOption[];
  settings: SpecialtySettingsResponse | null;
  savingScope: 'global' | 'owner' | 'user' | null;
  // Промис — вызывающий (PresetOptions.savePreset) ждёт резолва PUT слоя, прежде чем
  // назначать место на новый пресет (гонка «создать → назначить», MAJOR 1, ревью d23231bd)
  onSaveLayer: (scope: 'global' | 'owner' | 'user', next: SpecialtySettingsLayer) => Promise<void>;
  // Когда сборка цепочки — не единственная правка слоя (матрица «Исключений»: тут же
  // пишется ячейка тем же PUT) — потребитель подключает onCreated и сам ОДНИМ onSaveLayer
  // фиксирует и пресет, и свою правку на том же клоне layer. Без onCreated — старое
  // поведение (savePreset сам шлёт onSaveLayer пресета, отдельным вызовом от onPick).
  onCreated?: (presetId: string, scope: 'global' | 'owner' | 'user', layer: SpecialtySettingsLayer) => void;
}

// Группа «Пресеты» в панелях выбора модели (спека, блок 2): между карточками уровней
// и списком моделей. Пресет — третий вариант в том же выборе, отдельного контрола нет.
// Пустая группа не показывается вовсе (пока пресетов нет — интерфейс не меняется).
// scope — двойная роль: (1) какие пресеты показывать (только своего слоя — если задан;
// оба слоя — если нет), (2) в какой слой уйдёт НОВЫЙ пресет из inline-редактора. 'global' —
// место общее для всех (каталог мест, «Для всех» в исключениях): личный пресет там был бы
// битой ссылкой у всех, кроме автора (400 на бэкенде — LocalActionsAdminController.Set
// проверяет только Global.Presets). Не задан — личный контекст (слот, «Только для меня»):
// создаётся в owner, но в списке видны оба слоя — общий пресет тоже валиден для личного маршрута.
export function PresetOptions({ value, onPick, ctx, scope, creation, onEditingChange }: {
  value: string;
  onPick: (route: string) => void;
  ctx: ChainLabelContext;
  scope?: 'global' | 'owner' | 'user';
  creation?: PresetCreationCtx;
  // Родитель (RoutePicker) должен на время inline-редактирования прятать соседние блоки
  // своей панели — иначе случайный клик по ним схлопывает панель и стирает черновик
  onEditingChange?: (editing: boolean) => void;
}) {
  const all = usePresets();
  const presets = scope ? all.filter(p => p.scope === scope) : all;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<string[]>([]);
  // Имя вводит пользователь — без авто-номера «Цепочка N»: подпись должна осмысленно
  // говорить о содержимом («Экономная, но живая», «С фолбэком по уровню»). Пустая строка
  // считается пустой формой и блокирует сохранение. Конфликт имён — бан в момент записи
  // на бэке; UI дубликат не блокирует, чтобы не плодить «Цепочка N» в обход правила.
  const [draftName, setDraftName] = useState('');
  // Заголовок редактора: черновик преднаполнен шагами уже выбранного пресета — честно
  // подписываем как копию, а не «Новая цепочка» (молчаливый дубль без этого)
  const [copyOf, setCopyOf] = useState<string | null>(null);
  useEffect(() => { onEditingChange?.(editing); }, [editing, onEditingChange]);

  // Раскрытый inline-редактор — вместо списка пресетов и кнопки (решение владельца
  // 08.08.2026: «Собрать цепочку…» внутри уже открытого раздела собирает цепочку
  // на месте, а не открывает раздел заново). Проверяем editing первым, чтобы состояние
  // не зависело от гонки с presets.length (см. ниже)
  if (editing && creation) {
    const targetScope: 'global' | 'owner' | 'user' = scope ?? 'owner';
    const savePreset = () => {
      if (!creation.settings || draft.length === 0) return;
      const name = draftName.trim();
      // Пустое имя — отказ без записи: мини-валидация на клиенте, чтобы не получить
      // пресет с пустым именем
      if (name === '') return;
      // user-слой может быть ещё не загружен (админ открыл редактор раньше вкладки
      // «Особые правила»); без базы положить пресет некуда — отказ без записи
      const base = creation.settings[targetScope];
      if (!base) return;
      const id = newPresetId();
      const next = withNewPreset(base, id, name, draft);
      if (creation.onCreated) {
        creation.onCreated(id, targetScope, next);
        setEditing(false);
        setDraftName('');
      } else {
        // Назначаем место ТОЛЬКО после того, как сервер подтвердил новый пресет — иначе
        // pick() улетает параллельно с PUT слоя и обгоняет его: бэкенд валидирует
        // preset:{id} по снимку без ещё не записанного пресета → 400 (MAJOR 1).
        // Редактор закрываем тоже по успеху: на отказе черновик из нескольких шагов
        // должен остаться на месте, иначе собирать цепочку заново
        creation.onSaveLayer(targetScope, next)
          .then(() => { onPick(presetRoute(id)); setEditing(false); setDraftName(''); })
          .catch(() => {});
      }
    };
    const busy = creation.savingScope !== null;
    const trimName = draftName.trim();
    const canSave = !busy && draft.length > 0 && trimName !== '';
    return (
      <>
        <div style={{
          fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
          textTransform: 'uppercase', letterSpacing: '0.4px', margin: '2px 0 0',
        }}>
          {copyOf ? `Копия «${copyOf}»` : 'Новая цепочка'}
        </div>
        <input
          autoComplete="off"
          type="text"
          value={draftName}
          onChange={e => setDraftName(e.target.value)}
          placeholder="Например: «Экономная, но живая»"
          disabled={busy}
          autoFocus
          style={{
            font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm,
            padding: '6px 10px', borderRadius: 6,
            border: `1px solid ${C.border}`, background: C.bgWhite,
            color: C.textPrimary, outline: 'none',
          }}
        />
        <ChainStepsEditor
          steps={draft}
          onChange={setDraft}
          models={creation.models}
          tierModels={ctx.tierModels}
          ollamaModel={ctx.ollamaModel}
          busy={busy}
        />
        <div style={{ display: 'flex', gap: 8, marginTop: 2 }}>
          <Button size="sm" variant="ghost" disabled={busy}
            onClick={() => { setEditing(false); setDraftName(''); }}>Отмена</Button>
          <Button size="sm" variant="primary" disabled={!canSave} onClick={savePreset}>Сохранить</Button>
        </div>
      </>
    );
  }

  // В контексте «только общие» (места каталога) личные пресеты скрыты фильтром — без
  // подсказки это выглядело как «пресетов нет вовсе» (дефект приёмки 19d8f18e)
  const hiddenByScope = scope === 'global' && all.length > presets.length;
  const scopeNote = (
    <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
      Месту доступны только общие цепочки — личные здесь не показываются.
    </div>
  );
  // «Собрать цепочку…» обязана остаться видна и при нуле пресетов, когда есть creation —
  // иначе на чистой инсталляции завести первую цепочку неоткуда (MAJOR 5, ревью 65d8df66)
  if (presets.length === 0 && !creation) return hiddenByScope ? scopeNote : null;
  const activeId = presetIdOf(value);

  const startEditing = () => {
    if (!creation) { requestNewPreset(); return; }
    const existing = findPreset(all, presetIdOf(value));
    setDraft(existing?.steps ?? []);
    setCopyOf(existing?.name ?? null);
    setEditing(true);
  };

  return (
    <>
      {presets.length > 0 && (
        <>
          <div style={{
            fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
            textTransform: 'uppercase', letterSpacing: '0.4px', margin: '2px 0 0',
          }}>
            Цепочки
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
        </>
      )}
      {hiddenByScope && scopeNote}
      {/* Кнопка видна ВСЕГДА, а не только при переданном creation — иначе она пропадает
          там, где панель не подняла слои специальностей в состояние (форма/матрица
          персоны), и завести первую цепочку оттуда нельзя (MAJOR 4, ревью d23231bd).
          startEditing сам решает: creation есть — inline-редактор; нет — requestNewPreset()
          (диплинк в раздел). В контексте «только общие» сборка идёт в targetScope, поэтому
          валидна и там: создаёт ОБЩИЙ пресет, а не личный (было MAJOR 2 — 400 на сохранении) */}
      <button
        type="button"
        onClick={startEditing}
        style={{
          alignSelf: 'flex-start', font: 'inherit', fontSize: FS.sm, fontWeight: 600,
          color: C.accent, background: 'none', border: 'none', padding: '2px 2px',
          cursor: 'pointer', fontFamily: FONT.sans,
        }}
      >
        Собрать цепочку…
      </button>
    </>
  );
}
