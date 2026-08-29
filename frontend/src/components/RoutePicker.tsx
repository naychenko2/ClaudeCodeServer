import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronDown, CircleAlert } from 'lucide-react';
import { ModelPicker } from './ModelPicker';
import { QuickOptionCard } from './QuickOptionCard';
import { PresetOptions } from './PresetOptions';
import { Button } from './ui';
import { TIERS, TIER_ORDER, routeTier, tierSubtitle, type TierKey } from '../lib/modelProvidersShared';
import { localEngineLabel } from '../lib/localEngine';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { usePresets, presetIdOf } from '../lib/presets';
import {
  validateLocalActionRoute, routeCanSave, RATE_PICKER_HINT,
} from '../lib/routeFormat';
import { C, FONT, FS, R, SHADOW, Z, FIELD } from '../lib/design';
import { incPopupDepth } from '../lib/popupEscape';
import type { ModelOption } from '../lib/models';
import type { LayerReducer } from '../lib/presets';
import type { SpecialtySettingsLayer } from '../types';
import { NO_AUTOFILL } from '../lib/noAutofill';

const PANEL_W = 320;
const PANEL_MAX_H = 340;

// Единый контрол выбора маршрута (ячейка матрицы специальности / шаг цепочки пресета):
// триггер + всплывающая панель с карточками уровней, группой «Пресеты» и полным
// ModelPicker. Флаги showTiers/showPresets режут состав панели под место: в ячейке
// уровня нельзя выбрать уровень (тавтология), в шаге цепочки — пресет (вложенность).
// Два вида триггера: обычная кнопка-строка и мини-карточка (макет специальностей).
export function RoutePicker({
  route, label, models, tierModels, ollamaModel, ollamaProvider, allowLocal = false, busy = false,
  readOnly = false, onChange, placeholder = 'не задан', cardTitle, title,
  showTiers = true, showPresets = false, presetCreation, presetScope, manual,
  fullWidth = false, dashed = false,
}: {
  route: string;
  label: string;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  ollamaProvider?: string;
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
  // Снимок слоя PresetOptions берёт сам из стора lib/presets.ts — снаружи проп settings
  // не передаём (структурный запрет: точки записи не получают слой через проп).
  presetCreation?: {
    savingScope: 'global' | 'owner' | 'user' | null;
    // Контракт редьюсерный (см. presets.saveLayer): стор сам читает текущий слой и
    // шлёт PUT в нужный scope+userId. Старая «(scope, next)» сигнатура несовместима
    // с PresetCreationCtx — PresetOptions теперь сам готовит редьюсер.
    onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer,
      userId?: string | null) => Promise<void>;
    // См. PresetCreationCtx.onCreated — сливать сохранение пресета с ДРУГОЙ правкой того
    // же слоя в один PUT (нужно там, где onChange этого пикера тоже пишет в этот слой).
    onCreated?: (presetId: string, scope: 'global' | 'owner' | 'user', layer: SpecialtySettingsLayer) => void;
  };
  // Слой пресетов группы «Пресеты»: 'global' — место общее (список и inline-создание
  // ограничены общими пресетами), 'user' — админский пользовательский слой (тот же
  // принцип), не задан — личный контекст (создание в owner, в списке видны оба слоя).
  // Прокидывается в PresetOptions.scope как есть.
  presetScope?: 'global' | 'owner' | 'user';
  // Блок ручного ввода id маршрута с клиентской валидацией (ADR-009). Включается только
  // для local-action-контекстов (вкладка «Применение»): карточки выбора дают значение
  // с уже корректным префиксом, а редкий ручной ввод проверяется по тому же перечню
  // форм, что и бэкенд — сохранение заблокировано, пока значение невалидно. Не задан —
  // блок не рендерится (слоты/матрицы/исключения работают как раньше).
  manual?: { models?: ModelOption[]; presetIds?: string[] };
  // Триггер-строка тянется на всю ширину места вместо фиксированных 230px. Нужен строкам
  // «Особых правил» (уровень слева — значение во всю оставшуюся ширину): там имя цепочки
  // длинное, а места в карточке хватает.
  fullWidth?: boolean;
  // Пунктирный контур приглушённого значения — «поле не задано, оно наследуется».
  // Работает только на пустом значении: у заданного маршрута контур обычный.
  dashed?: boolean;
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

  // === Ручной ввод id маршрута (блок `manual`) ===
  // Черновик живёт только пока панель открыта: при открытии восстанавливается из
  // текущего значения места. Ошибка показывается после первого взаимодействия
  // (правка/blur) либо сразу, если сохранённое значение уже кривое — иначе человек
  // не поймёт, почему значение «не работает» (макет model-route-error-state §3).
  const manualCtx = manual;
  const [draft, setDraft] = useState(route);
  const [touched, setTouched] = useState(false);
  useEffect(() => {
    if (!open || !manualCtx) return;
    setDraft(route);
    setTouched(!routeCanSave(route, { models: manualCtx.models, presetIds: manualCtx.presetIds }));
  }, [open, route, manualCtx]);

  const draftCheck = manualCtx
    ? validateLocalActionRoute(draft, { models: manualCtx.models, presetIds: manualCtx.presetIds })
    : null;
  const draftError = draftCheck && !draftCheck.ok && draftCheck.message ? draftCheck.message : null;
  const draftValid = manualCtx
    ? routeCanSave(draft, { models: manualCtx.models, presetIds: manualCtx.presetIds }) && draft.trim() !== (route ?? '').trim()
    : false;

  useEffect(() => { if (!open) setPresetEditing(false); }, [open]);

  useEffect(() => {
    if (!open) return;
    // Регистрируем попап: пока счётчик > 0, Modal игнорирует Escape (см. lib/popupEscape).
    const release = incPopupDepth();
    const onDown = (e: MouseEvent) => {
      // Черновик цепочки правится — не схлопывать ЭТУ панель по клику снаружи. Актуально
      // для вложенного пикера (шаг цепочки в ChainStepsEditor): его открытие/закрытие не
      // должно утаскивать за собой родительский presetEditing-черновик (MAJOR 4, ревью 65d8df66)
      if (presetEditing) return;
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      // Тот же случай, что у mousedown-гейта выше: пока presetEditing, Escape достаётся
      // только вложенному пикеру шага, не схлопывая родительский черновик. Саму модалку
      // теперь защищает счётчик активных попапов (см. lib/popupEscape и Modal.tsx) —
      // пока эта панель открыта, Modal игнорирует Escape, и preventDefault() не нужен.
      if (presetEditing) return;
      setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      release();
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
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
          subtitle={ollamaModel ? `${localEngineLabel(ollamaProvider)} · ${ollamaModel}` : 'не настроена'}
          active={route === 'local'}
          onClick={() => pick('local')}
        />
      )}
      {showPresets && (
        <PresetOptions value={route} onPick={pick} ctx={{ tierModels, ollamaModel, ollamaProvider }} scope={presetScope}
          creation={presetCreation ? { models, ...presetCreation } : undefined}
          onEditingChange={setPresetEditing}
        />
      )}
      {!presetEditing && !showPresets && presets.length > 0 && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, padding: '0 2px' }}>
          Цепочка в цепочку не вкладывается — выпишите шаги подряд.
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
      {manualCtx && !presetEditing && (
        <RouteManualEntry
          draft={draft}
          onChange={v => { setDraft(v); setTouched(true); }}
          onBlur={() => setTouched(true)}
          showError={touched && !!draftError}
          error={draftError}
          canSave={draftValid}
          disabled={!!busy || readOnly}
          onSave={() => pick(draft)}
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
          <span style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: FS.xs, color: C.textMuted }}>
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
  const inherited = dashed && !route;
  return (
    <div ref={rootRef} style={{
      position: 'relative', display: 'flex', alignItems: 'center', gap: 8,
      ...(fullWidth ? { flex: '1 1 auto', minWidth: 0 } : { flexShrink: 0 }),
    }}>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => interactive && setOpen(o => !o)}
        disabled={!interactive}
        title={title || undefined}
        style={{
          display: 'flex', alignItems: 'center', gap: 6,
          maxWidth: fullWidth ? '100%' : 230, width: '100%',
          fontFamily: FONT.sans, fontSize: FS.xs,
          padding: '4px 8px 4px 9px', borderRadius: R.md,
          cursor: interactive ? 'pointer' : 'default', opacity: busy ? 0.5 : 1,
          color: inherited ? C.textMuted : C.textSecondary,
          background: inherited ? 'transparent' : C.bgWhite,
          border: `1px ${inherited ? 'dashed' : 'solid'} ${open ? C.accent : inherited ? C.dashed : C.border}`,
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

// === Блок ручного ввода id маршрута (нижняя секция панели выбора) ===
// Сюда приходят только local-action-контексты (через prop `manual`). Стилистически —
// та же карточка-поле, что в макете model-route-error-state.html: моноширинный инпут,
// danger-рамка/фон/ринг на ошибке, конкретный текст под полем с CircleAlert и всегда
// видимая подсказка о том, что модель берётся из списка (текст — из docs/features).
function RouteManualEntry({ draft, onChange, onBlur, showError, error, canSave, disabled, onSave }: {
  draft: string;
  onChange: (v: string) => void;
  onBlur: () => void;
  showError: boolean;
  error: string | null;
  canSave: boolean;
  disabled: boolean;
  onSave: () => void;
}) {
  const [focused, setFocused] = useState(false);
  const err = showError && !!error;
  // Геометрия — поле ввода проекта (FIELD): моноширинный шрифт, R.xl, padding 10/13.
  // error вытесняет accent-фокус: рамка/фон/ринг красные (макет §3 «error-состояние»).
  const fieldStyle: CSSProperties = {
    background: err ? C.dangerBg : FIELD.background,
    border: `1px solid ${err ? C.danger : focused ? FIELD.borderFocus : C.border}`,
    borderRadius: FIELD.borderRadius,
    padding: '10px 13px',
    fontSize: FIELD.fontSize,
    color: FIELD.color,
    outline: 'none',
    fontFamily: FONT.mono,
    width: '100%',
    boxSizing: 'border-box',
    boxShadow: err ? SHADOW.focusDanger : focused ? SHADOW.focus : 'none',
    transition: 'border-color 0.15s, box-shadow 0.15s, background 0.15s',
  };
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 2 }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.06em',
      }}>
        Или ввести id вручную
      </div>
      <input
        value={draft}
        onChange={e => onChange(e.target.value)}
        onBlur={() => { setFocused(false); onBlur(); }}
        onFocus={() => setFocused(true)}
        placeholder="tier:strong · preset:{id} · local · id модели"
        spellCheck={false}
        {...NO_AUTOFILL}
        aria-invalid={err}
        aria-describedby={err ? 'route-manual-err' : undefined}
        style={fieldStyle}
      />
      {err && (
        <div id="route-manual-err" style={{
          display: 'flex', alignItems: 'flex-start', gap: 7, marginTop: 1,
          fontSize: FS.sm, lineHeight: 1.45, color: C.dangerText,
        }}>
          <CircleAlert size={14} strokeWidth={2.2} style={{ flexShrink: 0, marginTop: 1, color: C.dangerText }} />
          <span>{error}</span>
        </div>
      )}
      <div style={{ fontSize: FS.xs, lineHeight: 1.45, color: C.textMuted }}>{RATE_PICKER_HINT}</div>
      <Button size="sm" variant="primary" disabled={disabled || !canSave} onClick={onSave}
        style={{ alignSelf: 'flex-start', marginTop: 2 }}>
        Сохранить
      </Button>
    </div>
  );
}
