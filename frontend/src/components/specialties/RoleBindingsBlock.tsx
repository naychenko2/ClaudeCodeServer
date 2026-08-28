// Блок «Умения роли» — типовой профиль умений специальности (массив
// SpecialtyDefaultBinding, «Привязки по умолчанию»). Переиспользуется карточкой
// роли (mode='view', SpecialtyRoleView) и настройкой роли (mode='edit',
// SpecialtyEditView) — по образцу RolePresetsBlock с режимами view/edit.
//
// Образец интерактива — «Умения» персоны (PersonaBindingsPanel): карточки с
// инлайн-раскрытием, степпер добавления, «⚡ Найти навык» из реестра. Два отличия:
//   • цель хранится ТОЛЬКО у навыков (skillName); для остальных типов конкретную
//     цель подбирает ИИ при создании персоны — в SpecialtyDefaultBinding её нет;
//   • своих запросов к API нет — правки уходят в черновик родителя (onChange) и
//     сохраняются ОБЩЕЙ кнопкой «Сохранить» формы роли (у персоны — мгновенно).
//
// Все стили — токены lib/design.ts, контролы ui-кита. Сырого hex нет.

import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, Plus, X } from 'lucide-react';
import type {
  PersonaBindingMode, PersonaBindingType, RegistrySkill, SpecialtyDefaultBinding,
} from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Button, PillSwitch, TextArea } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { SkillSearchDialog } from '../SkillSearchDialog';
import { newDefaultBinding, newDefaultBindingId } from '../../lib/specialties';
import { SectionLabel } from '../../features/tasks/bits';
import {
  BINDING_ICONS, BINDING_TYPE_META, BINDING_TYPE_ORDER, MODE_HINT,
  BindingModeBadge, BindingTypeIcon,
} from '../../features/personas/bindingMeta';
import { Stepper, Crumb } from '../../features/personas/stepperUi';

// Пояснение про цель: в типовом умении роли цель не хранится (кроме skillName),
// конкретную цель подбирает ИИ при создании персоны
const AI_TARGET_HINT = 'Цель подберёт ИИ при создании персоны';

const MODE_OPTIONS: { value: PersonaBindingMode; label: string }[] = [
  { value: 'auto', label: 'Авто' },
  { value: 'always', label: 'Всегда' },
  { value: 'off', label: 'Выкл' },
];

// Примеры условий для шага «Правило»
const CONDITION_EXAMPLES = [
  'когда спрашивают про релизы',
  'при правке фронтенда',
  'в каждом ответе про архитектуру',
];

// Заголовок карточки: у навыка — имя из реестра, у остальных — название типа
// (конкретная цель подбирается ИИ при создании персоны, хранить нечего)
function bindingTitle(b: SpecialtyDefaultBinding): string {
  if (b.type === 'skill') {
    const name = b.skillName?.trim();
    return name ? `Навык «${name}»` : 'Навык — не выбран';
  }
  return BINDING_TYPE_META[b.type].name;
}

// Счётчик «N умений · M выкл» для заголовка блока
function bindingsCounter(bindings: SpecialtyDefaultBinding[]): string {
  const n = bindings.length;
  if (n === 0) return 'нет умений';
  const offN = bindings.filter(b => b.mode === 'off').length;
  const m10 = n % 10, m100 = n % 100;
  const word = (m10 === 1 && m100 !== 11) ? 'умение'
    : (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) ? 'умения' : 'умений';
  return `${n} ${word}${offN ? ` · ${offN} выкл` : ''}`;
}

// Состояние инлайн-панели добавления: для skill — ① Тип → ② Цель → ③ Правило,
// для остальных типов — ① Тип → ② Правило (шаг «Цель» пропускается: цель хранится
// только у навыков). Номер шага «Правило» зависит от типа: ruleStep.
interface AddPanelState {
  step: number;
  type?: PersonaBindingType;
  // Выбранное имя навыка из реестра — только при type === 'skill'
  skillName?: string;
  condition: string;
  mode: PersonaBindingMode;
}

// Шаг «Правило»: 3 у навыка (после шага «Цель»), 2 у остальных типов
function ruleStepOf(type: PersonaBindingType | undefined): number {
  return type === 'skill' ? 3 : 2;
}

export interface RoleBindingsBlockProps {
  roleKey: string;
  bindings: SpecialtyDefaultBinding[];
  mode: 'view' | 'edit';
  // Цвет роли — рамки активных/развёрнутых карточек и степпер (как accent персоны)
  accent: string;
  // Только для edit: правка черновика списка. Своего сохранения нет — форму
  // закрывает общая кнопка «Сохранить» родителя.
  onChange?: (v: SpecialtyDefaultBinding[]) => void;
  // Не рисовать собственный SectionLabel — родитель вешает его снаружи блока
  showTitle?: boolean;
}

export function RoleBindingsBlock({
  roleKey, bindings, mode, accent, onChange, showTitle = true,
}: RoleBindingsBlockProps): React.ReactElement {
  const isMobile = useIsMobile();
  const editable = mode === 'edit' && typeof onChange === 'function';

  // Стабильные ключи строк: у SpecialtyDefaultBinding нет id (это просто записи
  // массива слоя), а ключ по позиции+содержимому ронял бы фокус при правке условия.
  // Пересчёт только при смене длины или роли — правки полей длину не меняют.
  const rowIds = useMemo(
    () => bindings.map(() => newDefaultBindingId()),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [roleKey, bindings.length],
  );

  // Локальный UI-стейт (edit)
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const [panel, setPanel] = useState<AddPanelState | null>(null);
  // Диалог реестра навыков: из шага «Цель» степпера и по кнопке «⚡ Найти навык»
  const [panelSkillSearch, setPanelSkillSearch] = useState(false);
  const [quickSkillSearch, setQuickSkillSearch] = useState(false);
  // Короткая подсветка свежедобавленной строки (последняя строка черновика)
  const [flashIndex, setFlashIndex] = useState<number | null>(null);
  const flashTimer = useRef<number | null>(null);

  // Смена роли (родитель не перемонтирует блок) — сброс локального UI
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс локального UI при смене роли
    setExpandedId(null);
    setHoveredId(null);
    setPanel(null);
    setPanelSkillSearch(false);
    setQuickSkillSearch(false);
    setFlashIndex(null);
  }, [roleKey]);

  useEffect(() => () => { if (flashTimer.current) window.clearTimeout(flashTimer.current); }, []);

  const flashLast = () => {
    setFlashIndex(bindings.length);   // новая строка станет последней
    if (flashTimer.current) window.clearTimeout(flashTimer.current);
    flashTimer.current = window.setTimeout(() => setFlashIndex(null), 1200);
  };

  // === Мутации черновика (сохранение — общая кнопка формы роли) ===

  const addBinding = (b: SpecialtyDefaultBinding) => {
    onChange?.([...bindings, b]);
    flashLast();
  };

  const patchAt = (i: number, patch: Partial<SpecialtyDefaultBinding>) => {
    onChange?.(bindings.map((b, j) => (j === i ? { ...b, ...patch } : b)));
  };

  const removeAt = (i: number) => {
    onChange?.(bindings.filter((_, j) => j !== i));
    setExpandedId(null);
  };

  const toggleCard = (id: string) => {
    setExpandedId(cur => (cur === id ? null : id));
  };

  const openAdd = () => {
    setExpandedId(null);
    setPanel({ step: 1, condition: '', mode: 'auto' });
  };

  const commitPanel = () => {
    if (!panel?.type) return;
    if (panel.type === 'skill' && !panel.skillName?.trim()) return;
    addBinding(newDefaultBinding(
      panel.type, panel.condition, panel.mode,
      panel.type === 'skill' ? panel.skillName : null,
    ));
    setPanel(null);
  };

  // Выбор навыка из реестра: в панели степпера — заполнить шаг «Цель» и перейти
  // к правилу; по кнопке «⚡ Найти навык» — сразу добавить умение типа skill
  const pickSkillForPanel = (s: RegistrySkill) => {
    setPanelSkillSearch(false);
    setPanel(p => (p ? { ...p, skillName: s.skill, step: 3 } : p));
  };
  const pickSkillQuick = (s: RegistrySkill) => {
    setQuickSkillSearch(false);
    addBinding(newDefaultBinding('skill', '', 'auto', s.skill));
  };

  // === Карточка одного умения ===
  const renderCard = (i: number, b: SpecialtyDefaultBinding) => {
    const id = rowIds[i];
    const open = editable && expandedId === id;
    const dim = b.mode === 'off' && !open;
    const flashing = flashIndex === i;
    const hovered = hoveredId === id;
    return (
      <div
        key={id}
        onMouseEnter={() => setHoveredId(id)}
        onMouseLeave={() => setHoveredId(h => (h === id ? null : h))}
        style={{
          background: flashing ? C.accentLight : C.bgWhite,
          border: `1px solid ${open || hovered ? accent : C.border}`,
          borderRadius: R.xl, padding: '10px 14px',
          transition: 'border-color 0.15s, background 0.6s',
        }}
      >
        {/* Свёрнутая строка */}
        <div
          onClick={editable ? () => toggleCard(id) : undefined}
          style={{ display: 'flex', alignItems: 'center', gap: 12, cursor: editable ? 'pointer' : 'default' }}
        >
          <BindingTypeIcon type={b.type} dim={dim} />
          <div style={{ flex: 1, minWidth: 0, opacity: dim ? 0.55 : 1 }}>
            <div style={{
              fontSize: 13.5, fontWeight: 600, color: C.textHeading,
              fontFamily: b.type === 'skill' ? FONT.mono : FONT.sans,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{bindingTitle(b)}</div>
            {b.type !== 'skill' && (
              <div style={{
                fontSize: 12, color: C.textMuted, marginTop: 1,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>{AI_TARGET_HINT}</div>
            )}
            {b.condition ? (
              <div style={{
                fontSize: 12, color: C.textSecondary, marginTop: 1,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>{b.condition}</div>
            ) : (
              <div style={{
                fontSize: 12, color: C.textMuted, fontStyle: 'italic', marginTop: 1,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>Всегда под рукой — условие не задано</div>
            )}
          </div>
          <BindingModeBadge mode={b.mode} />
        </div>

        {/* Развёрнутое тело — редактирование по месту (черновик, без API) */}
        {open && (
          <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 10, paddingTop: 12 }}>
            <div style={{ fontSize: 12, color: b.type === 'skill' ? C.textSecondary : C.textMuted }}>
              {b.type === 'skill'
                ? <>Навык: <span style={{ fontFamily: FONT.mono }}>{b.skillName?.trim() || 'не выбран'}</span></>
                : AI_TARGET_HINT}
            </div>
            <div style={{ ...fLabel, marginTop: 10 }}>Когда пользоваться</div>
            <TextArea
              value={b.condition}
              onChange={v => patchAt(i, { condition: v })}
              autoGrow
              minHeight={56}
              maxHeight={160}
              placeholder="Например: когда спрашивают про релизы"
            />
            <div style={{ ...fLabel, marginTop: 14 }}>Режим</div>
            <PillSwitch<PersonaBindingMode>
              fill
              value={b.mode}
              onChange={m => patchAt(i, { mode: m })}
              options={MODE_OPTIONS}
            />
            <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>{MODE_HINT[b.mode]}</div>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 14 }}>
              <button onClick={() => removeAt(i)} style={delLink}>Удалить умение</button>
              <Button variant="ghost" size="sm" onClick={() => setExpandedId(null)}>Готово</Button>
            </div>
          </div>
        )}
      </div>
    );
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      {/* Заголовок секции + счётчик */}
      {showTitle && (
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
          <SectionLabel>Умения роли</SectionLabel>
          <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
            {bindingsCounter(bindings)}
          </span>
        </div>
      )}
      <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.5, marginTop: showTitle ? 4 : 0 }}>
        {editable
          ? 'Типовой профиль: при создании персоны специальности умения материализуются в её личные привязки. Правки уходят в черновик — сохраняет кнопка «Сохранить» формы.'
          : 'Типовые умения: при создании персоны специальности материализуются в её личные привязки.'}
      </div>

      {/* Пустое состояние */}
      {bindings.length === 0 && !panel && (
        <div style={{
          marginTop: 12, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
          padding: isMobile ? '18px 14px' : '24px 22px', textAlign: 'center',
        }}>
          {editable ? (
            <>
              <div style={{ color: C.textMuted, marginBottom: 8, display: 'flex', justifyContent: 'center' }}>
                <Link size={22} strokeWidth={ICON_STROKE} />
              </div>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>
                Типовых умений пока нет
              </div>
              <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5, marginTop: 5 }}>
                Добавьте источники и правила — новые персоны роли получат их
                в свои личные привязки при создании.
              </div>
              <div style={{ display: 'flex', gap: 8, justifyContent: 'center', flexWrap: 'wrap', marginTop: 14 }}>
                <AddBindingButton onClick={openAdd} />
                <Button variant="ghost" size="sm" onClick={() => setQuickSkillSearch(true)}>
                  ⚡ Найти навык
                </Button>
              </div>
            </>
          ) : (
            <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.5 }}>
              Типовых умений нет — персоны роли стартуют с пустым набором привязок.
            </div>
          )}
        </div>
      )}

      {/* Карточки умений */}
      {bindings.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 12 }}>
          {bindings.map((b, i) => renderCard(i, b))}
        </div>
      )}

      {/* Кнопки под списком (edit, пока закрыта панель добавления) */}
      {editable && bindings.length > 0 && !panel && (
        <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
          <AddBindingButton onClick={openAdd} />
          <Button variant="ghost" size="sm" onClick={() => setQuickSkillSearch(true)}>
            ⚡ Найти навык
          </Button>
        </div>
      )}

      {/* Инлайн-панель добавления (степпер) */}
      {editable && panel && (
        <AddPanel
          panel={panel}
          accent={accent}
          isMobile={isMobile}
          onChange={setPanel}
          onClose={() => setPanel(null)}
          onCommit={commitPanel}
          onPickSkill={() => setPanelSkillSearch(true)}
        />
      )}

      {/* Диалог реестра навыков: шаг «Цель» степпера и «⚡ Найти навык» */}
      {panelSkillSearch && (
        <SkillSearchDialog
          onClose={() => setPanelSkillSearch(false)}
          onPick={pickSkillForPanel}
        />
      )}
      {quickSkillSearch && (
        <SkillSearchDialog
          onClose={() => setQuickSkillSearch(false)}
          onPick={pickSkillQuick}
        />
      )}
    </div>
  );
}

// === Инлайн-панель «Добавить умение» ===
// skill: ① Тип → ② Цель (навык из реестра) → ③ Правило;
// остальные типы: ① Тип → ② Правило — шаг «Цель» пропускается, цель хранится
// только у навыков (остальные подбирает ИИ при создании персоны).
function AddPanel({ panel, accent, isMobile, onChange, onClose, onCommit, onPickSkill }: {
  panel: AddPanelState;
  accent: string;
  isMobile: boolean;
  onChange: (p: AddPanelState) => void;
  onClose: () => void;
  onCommit: () => void;
  onPickSkill: () => void;
}): React.ReactElement {
  const isSkill = panel.type === 'skill';
  const ruleStep = ruleStepOf(panel.type);
  const steps = panel.type
    ? (isSkill
      ? [{ n: 1, label: 'Тип' }, { n: 2, label: 'Цель' }, { n: 3, label: 'Правило' }]
      : [{ n: 1, label: 'Тип' }, { n: 2, label: 'Правило' }])
    : [{ n: 1, label: 'Тип' }];

  // Возврат на «Тип» сбрасывает выбор (тип и цель); с «Правила» навыка на «Цель» —
  // выбранный навык сохраняем (можно заменить на том же шаге)
  const backToType = () =>
    onChange({ ...panel, step: 1, type: undefined, skillName: undefined });

  return (
    <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 14, paddingTop: 18 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <span style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>Добавить умение</span>
        <button onClick={onClose} aria-label="Закрыть" style={xBtn}>
          <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </button>
      </div>

      <Stepper
        step={panel.step}
        accent={accent}
        steps={steps}
        onStep={s => {
          if (s >= panel.step) return;
          if (s === 1) backToType();
          else onChange({ ...panel, step: s });   // только шаг «Цель» навыка
        }}
      />

      {/* Шаг ① «Тип» */}
      {panel.step === 1 && (
        <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr 1fr' : 'repeat(4, 1fr)', gap: 10, marginTop: 14 }}>
          {BINDING_TYPE_ORDER.map(t => {
            const m = BINDING_TYPE_META[t];
            return (
              <button
                key={t}
                onClick={() => onChange({ ...panel, step: 2, type: t, skillName: undefined })}
                onMouseEnter={e => { e.currentTarget.style.borderColor = accent; e.currentTarget.style.background = C.bgCard; }}
                onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.background = C.bgWhite; }}
                style={{
                  textAlign: 'left', background: C.bgWhite, border: `1px solid ${C.border}`,
                  borderRadius: R.xl, padding: 12, cursor: 'pointer', fontFamily: FONT.sans,
                  transition: 'border-color 0.15s',
                }}
              >
                <BindingTypeIcon type={t} />
                <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, marginTop: 8 }}>{m.name}</div>
                <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.4, marginTop: 3 }}>{m.hint}</div>
              </button>
            );
          })}
        </div>
      )}

      {/* Шаг ② «Цель» — только для навыка: выбор из реестра через диалог */}
      {panel.step === 2 && isSkill && (
        <>
          <Crumb onClick={backToType}>
            {BINDING_ICONS.skill(13)} {BINDING_TYPE_META.skill.name}
          </Crumb>
          <div style={{
            marginTop: 14, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
            padding: '18px 16px', textAlign: 'center',
          }}>
            <div style={{ display: 'flex', justifyContent: 'center', marginBottom: 8 }}>
              <BindingTypeIcon type="skill" />
            </div>
            <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>Навык из реестра</div>
            <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5, marginTop: 5 }}>
              Единственный тип умения с явной целью: при создании персоны навык
              установится и привяжется автоматически. Остальные типы цель не хранят —
              её подберёт ИИ.
            </div>
            {panel.skillName?.trim() ? (
              <>
                <div style={{ fontSize: 12.5, fontFamily: FONT.mono, color: C.textHeading, marginTop: 12 }}>
                  Выбран: «{panel.skillName.trim()}»
                </div>
                <div style={{ display: 'flex', gap: 8, justifyContent: 'center', flexWrap: 'wrap', marginTop: 12 }}>
                  <Button variant="ghost" size="sm" onClick={onPickSkill}>Заменить</Button>
                  <Button variant="primary" size="sm" onClick={() => onChange({ ...panel, step: 3 })}>Далее</Button>
                </div>
              </>
            ) : (
              <div style={{ marginTop: 12 }}>
                <Button variant="primary" size="sm" onClick={onPickSkill}>Выбрать навык</Button>
              </div>
            )}
          </div>
        </>
      )}

      {/* Шаг «Правило»: у навыка — ③, у остальных — ② */}
      {panel.step === ruleStep && panel.step > 1 && panel.type && (
        <>
          <Crumb onClick={() => (isSkill
            ? onChange({ ...panel, step: 2 })
            : backToType())}>
            {BINDING_ICONS[panel.type](13)} {BINDING_TYPE_META[panel.type].name}
            {isSkill && panel.skillName?.trim() ? ` · ${panel.skillName.trim()}` : ''}
          </Crumb>
          <div style={{ ...fLabel, marginTop: 16 }}>Когда пользоваться</div>
          <TextArea
            value={panel.condition}
            onChange={v => onChange({ ...panel, condition: v })}
            autoGrow
            minHeight={56}
            maxHeight={160}
            placeholder="Например: когда спрашивают про релизы"
          />
          <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>Пусто — персона решит сама по ситуации</div>
          {!isSkill && (
            <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 4 }}>{AI_TARGET_HINT}</div>
          )}
          <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap', marginTop: 8 }}>
            <span style={{ fontSize: 11.5, color: C.textMuted }}>Примеры:</span>
            {CONDITION_EXAMPLES.map(e => (
              <ExampleChip key={e} label={e} onClick={() => onChange({ ...panel, condition: e })} />
            ))}
          </div>
          <div style={{ ...fLabel, marginTop: 16 }}>Режим</div>
          <PillSwitch<PersonaBindingMode>
            fill
            value={panel.mode}
            onChange={m => onChange({ ...panel, mode: m })}
            options={MODE_OPTIONS}
          />
          <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>{MODE_HINT[panel.mode]}</div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
            <Button variant="ghost" size="sm" onClick={onClose}>Отмена</Button>
            <Button
              variant="primary" size="sm" onClick={onCommit}
              disabled={isSkill && !panel.skillName?.trim()}
            >
              Добавить умение
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

// Пунктирная кнопка «+ Добавить умение»
function AddBindingButton({ onClick }: { onClick: () => void }): React.ReactElement {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.accent; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.dashed; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        border: `1.5px dashed ${C.dashed}`, background: 'transparent', color: C.textSecondary,
        borderRadius: R.lg, padding: '7px 14px', fontSize: 12.5, fontWeight: 600,
        cursor: 'pointer', fontFamily: FONT.sans, transition: 'border-color 0.15s, color 0.15s',
      }}
    >
      <Plus size={14} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      Добавить умение
    </button>
  );
}

// Чип-пример условия (шаг «Правило»)
function ExampleChip({ label, onClick }: { label: string; onClick: () => void }): React.ReactElement {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.textPrimary; }}
      onMouseLeave={e => { e.currentTarget.style.background = C.bgWhite; e.currentTarget.style.borderColor = C.border; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.pill,
        padding: '4px 11px', fontSize: 12, color: C.textSecondary, cursor: 'pointer',
        fontFamily: FONT.sans, transition: 'background 0.12s, border-color 0.12s',
      }}
    >
      {label}
    </button>
  );
}

const fLabel: React.CSSProperties = {
  fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: '0.05em',
  color: C.textSecondary, marginBottom: 6, fontFamily: FONT.sans,
};

const delLink: React.CSSProperties = {
  border: 'none', background: 'none', fontSize: 12.5, fontWeight: 600,
  color: C.dangerText, padding: '4px 0', cursor: 'pointer', fontFamily: FONT.sans,
};

const xBtn: React.CSSProperties = {
  width: 28, height: 28, border: 'none', background: 'transparent', borderRadius: R.md,
  color: C.textMuted, cursor: 'pointer',
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};
