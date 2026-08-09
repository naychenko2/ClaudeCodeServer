import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { ModelPicker } from './ModelPicker';
import { QuickOptionCard } from './QuickOptionCard';
import { PresetOptions } from './PresetOptions';
import { TIERS, TIER_ORDER, routeTier, tierSubtitle, type TierKey } from '../lib/modelProvidersShared';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { usePresets, presetIdOf } from '../lib/presets';
import { C, FONT, FS, R, SHADOW, Z } from '../lib/design';
import type { ModelOption } from '../lib/models';
import type { SpecialtySettingsLayer, SpecialtySettingsResponse } from '../types';

const PANEL_W = 320;
const PANEL_MAX_H = 340;

// Единый контрол выбора маршрута (ячейка матрицы специальности / шаг цепочки пресета):
// триггер + всплывающая панель с карточками уровней, группой «Пресеты» и полным
// ModelPicker. Флаги showTiers/showPresets режут состав панели под место: в ячейке
// уровня нельзя выбрать уровень (тавтология), в шаге цепочки — пресет (вложенность).
// Два вида триггера: обычная кнопка-строка и мини-карточка (макет специальностей).
export function RoutePicker({
  route, label, models, tierModels, ollamaModel, allowLocal = false, busy = false,
  readOnly = false, onChange, placeholder = 'не задан', cardTitle, title,
  showTiers = true, showPresets = false, presetCreation, presetScope,
}: {
  route: string;
  label: string;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  allowLocal?: boolean;
  busy?: boolean;
  readOnly?: boolean;
  onChange: (route: string) => void;
  placeholder?: string;
  // Режим мини-карточки: триггер — карточка с заголовком сверху и значением ниже
  cardTitle?: string;
  // Тултип триггера. По умолчанию пусто — старые места вызова не получают лишних подсказок.
  // Используется там, где label укорочен для узкой ячейки и полный текст нужен в тултипе
  // (спека «Исключения»: цепочка «glm-5.2 → sonnet · +2» → title с полным составом).
  title?: string;
  // Показывать карточки уровней (сильная/средняя/слабая). В поле, которое само адресовано
  // уровнем (ячейка матрицы), уровни не предлагаем — «сильная = средняя» была бы петлёй
  showTiers?: boolean;
  // Показывать группу «Пресеты» (пресет — третий вариант выбора). В шаге цепочки
  // не предлагаем: пресет в пресет не вкладывается (бэкенд отклоняет 400)
  showPresets?: boolean;
  // Доступ к слоям специальностей — включает inline-сборку цепочки в группе «Пресеты»
  // (кнопка «Собрать цепочку…» правит черновик на месте вместо перехода в раздел).
  // Передаётся только там, где эти слои уже подняты в состояние (вкладки раздела
  // «Модели и расход»); без него — прежнее поведение (открыть раздел через nav-событие).
  presetCreation?: {
    settings: SpecialtySettingsResponse | null;
    savingScope: 'global' | 'owner' | null;
    onSaveLayer: (scope: 'global' | 'owner', next: SpecialtySettingsLayer) => Promise<void>;
    // См. PresetCreationCtx.onCreated — сливать сохранение пресета с ДРУГОЙ правкой того
    // же слоя в один PUT (нужно там, где onChange этого пикера тоже пишет в этот слой).
    onCreated?: (presetId: string, scope: 'global' | 'owner', layer: SpecialtySettingsLayer) => void;
  };
  // Слой пресетов группы «Пресеты»: 'global' — место общее (список и inline-создание
  // ограничены общими пресетами), не задан — личный контекст (создание в owner, в списке
  // видны оба слоя). Прокидывается в PresetOptions.scope как есть.
  presetScope?: 'global' | 'owner';
}) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number; maxHeight: number } | null>(null);
  // Пока в группе «Пресеты» раскрыт inline-редактор цепочки — прячем соседние блоки
  // панели (карточки уровней/локали, ModelPicker): клик по ним звал бы pick() → setOpen(false)
  // и убивал бы несохранённый черновик без предупреждения (дефект ревью designer)
  const [presetEditing, setPresetEditing] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const presets = usePresets();
  const activeTier = routeTier(route);
  const interactive = !busy && !readOnly;

  useEffect(() => { if (!open) setPresetEditing(false); }, [open]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      // Черновик цепочки правится — не схлопывать ЭТУ панель по клику снаружи. Актуально
      // для вложенного пикера (шаг цепочки в ChainStepsEditor): его открытие/закрытие не
      // должно утаскивать за собой родительский presetEditing-черновик (MAJOR 4, ревью 65d8df66)
      if (presetEditing) return;
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      // Гасим Escape ВСЕГДА, пока эта панель открыта — иначе Modal (её document-listener
      // навешен раньше, при монтировании модалки) получает необработанное событие первым
      // и закрывает модалку целиком поверх этой панели, стирая черновик (MAJOR 3, ревью d23231bd)
      e.preventDefault();
      // Тот же случай, что у mousedown-гейта выше: document-level слушатель есть у ОБОИХ
      // пикеров разом — пока presetEditing, Escape достаётся только вложенному пикеру шага,
      // не схлопывая родительский черновик.
      if (presetEditing) return;
      setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    // capture:true — иначе preventDefault() выше не успевает: Modal вешает свой keydown
    // НА BUBBLE раньше (при монтировании модалки), и порядок между двумя bubble-слушателями
    // одного document — по регистрации, а не по вложенности. Capture-фаза всегда отрабатывает
    // ДО bubble-фазы в одном и том же событии — так preventDefault() успевает выставиться
    // до того, как Modal дойдёт до своей проверки e.defaultPrevented (MAJOR 3, ревью d23231bd)
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [open, presetEditing]);

  useLayoutEffect(() => {
    if (!open || !triggerRef.current) return;
    const rect = triggerRef.current.getBoundingClientRect();
    const vw = window.innerWidth, vh = window.innerHeight;
    let left = rect.right - PANEL_W;
    left = Math.max(12, Math.min(left, vw - PANEL_W - 12));
    const spaceBelow = vh - rect.bottom - 12;
    const spaceAbove = rect.top - 12;
    let top: number, maxHeight: number;
    if (spaceBelow >= 220 || spaceBelow >= spaceAbove) {
      top = rect.bottom + 6;
      maxHeight = Math.max(160, Math.min(PANEL_MAX_H, spaceBelow - 6));
    } else {
      maxHeight = Math.max(160, Math.min(PANEL_MAX_H, spaceAbove - 6));
      top = rect.top - 6 - maxHeight;
    }
    setPos({ top, left, maxHeight });
  }, [open]);

  const pick = (v: string) => { onChange(v); setOpen(false); };
  // Подсветка в ModelPicker: слот/локаль/пресет — не модельные значения, им нечего светить
  const pickerValue = activeTier || route === 'local' || presetIdOf(route) ? '' : route;

  const panel = open && pos ? (
    <div
      style={{
        position: 'fixed', top: pos.top, left: pos.left,
        width: PANEL_W, maxWidth: 'calc(100vw - 24px)', maxHeight: pos.maxHeight,
        overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 6,
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        boxShadow: SHADOW.dropdown, padding: 8, zIndex: Z.dropdown,
      }}
    >
      {!presetEditing && showTiers && TIER_ORDER.map(t => (
        <QuickOptionCard
          key={t}
          title={TIERS[t].title}
          subtitle={tierSubtitle(tierModels[t])}
          active={activeTier === t}
          onClick={() => pick(TIERS[t].route)}
        />
      ))}
      {!presetEditing && allowLocal && (
        <QuickOptionCard
          title="Локальная модель"
          subtitle={ollamaModel ? `Ollama · ${ollamaModel}` : 'не настроена'}
          active={route === 'local'}
          onClick={() => pick('local')}
        />
      )}
      {showPresets && (
        <PresetOptions value={route} onPick={pick} ctx={{ tierModels, ollamaModel }} scope={presetScope}
          creation={presetCreation ? { models, ...presetCreation } : undefined}
          onEditingChange={setPresetEditing}
        />
      )}
      {!presetEditing && !showPresets && presets.length > 0 && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
          Пресет в пресет не вкладывается — выпишите шаги подряд.
        </div>
      )}
      {!presetEditing && <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '2px 0' }} />}
      {!presetEditing && (
        <ModelPicker
          value={pickerValue}
          options={models}
          onChange={pick}
          collapsible={false}
          includeDirect={allowLocal}
          hideDefault
        />
      )}
    </div>
  ) : null;

  // Триггер-мини-карточка (макет специальностей)
  if (cardTitle !== undefined) {
    return (
      <div ref={rootRef} style={{ position: 'relative', minWidth: 0 }}>
        <button
          ref={triggerRef}
          type="button"
          onClick={() => interactive && setOpen(o => !o)}
          disabled={!interactive}
          title={title || undefined}
          style={{
            width: '100%', display: 'flex', flexDirection: 'column', gap: 1, textAlign: 'left',
            padding: '8px 10px', borderRadius: R.md, fontFamily: FONT.sans,
            background: C.bgPanel, border: `1px solid ${open ? C.accent : C.border}`,
            cursor: interactive ? 'pointer' : 'default', opacity: busy ? 0.5 : 1,
            outline: 'none', transition: 'border-color 0.15s, box-shadow 0.15s',
            boxShadow: open ? SHADOW.focus : 'none',
          }}
        >
          <span style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 10.5, color: C.textMuted }}>
            <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{cardTitle}</span>
            {interactive && (
              <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                style={{ flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
            )}
          </span>
          <span style={{
            fontSize: FS.sm, fontWeight: 600, marginTop: 1,
            color: route ? C.textHeading : C.textMuted,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {label || placeholder}
          </span>
        </button>
        {panel}
      </div>
    );
  }

  // Обычный триггер-строка
  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => interactive && setOpen(o => !o)}
        disabled={!interactive}
        title={title || undefined}
        style={{
          display: 'flex', alignItems: 'center', gap: 6, maxWidth: 230, width: '100%',
          fontFamily: FONT.sans, fontSize: FS.xs,
          padding: '4px 8px 4px 9px', borderRadius: R.md,
          cursor: interactive ? 'pointer' : 'default', opacity: busy ? 0.5 : 1,
          color: C.textSecondary, background: C.bgWhite,
          border: `1px solid ${open ? C.accent : C.border}`,
          outline: 'none', transition: 'border-color 0.15s, box-shadow 0.15s',
          boxShadow: open ? SHADOW.focus : 'none',
        }}
      >
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1, textAlign: 'left' }}>
          {label || placeholder}
        </span>
        <ChevronDown
          size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, color: C.textSecondary, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}
        />
      </button>
      {panel}
    </div>
  );
}
