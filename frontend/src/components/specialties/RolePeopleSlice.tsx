// Срез «Кто работает по этой роли» (карточка роли, волна 4
// «Персонализация специальностей»). Только owner-слой: на global/user список
// персон был бы про чужих людей (принцип T8).
//
// Строка персоны:
//   • аватар + имя
//   • пометки в одну строку через « · » в порядке «модель, умения»:
//       «модель задана вручную» (T5 — задаётся снаружи через manualByPersona)
//       «не хватает типовых умений: N» (вычисляется: дефолтные умения роли из
//        каталога секций промптов сравниваются с личными привязками персоны
//        по (type, condition, mode); цели (target, path) игнорируются — их
//        подбирает AI при материализации)
//   • кнопка «Применить типовые» (единственное исключение из read-only на
//     экране просмотра): добавляет недостающее, ничего не перезаписывает.
//     Состояния:
//       idle          → «Применить типовые»
//       applying      → «Применяю…»
//       success(N)    → «Добавлено умений: N» (сбрасывается через 3 с)
//       error         → «Не удалось применить — попробуйте позже»
//     Перед вызовом — ConfirmDialog P13:
//     «Добавить персоне {имя} недостающие типовые умения этой роли?
//      Уже настроенные умения останутся как есть.»
//
// Вызывающий передаёт уже отфильтрованный список персон роли; компонент не
// сортирует и не фильтрует по specialty, только по имени (ru).
//
// Файлы SpecialtyRoleView/SpecialtyEditView правят другие исполнители волны —
// этот блок принимает готовые personas и catalog снаружи. Стили только из
// lib/design.ts, контролы из ui-кита, без Tailwind и CSS-модулей.

import { useEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, ConfirmDialog } from '../ui';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import type {
  Persona, PersonaBindingMode, PersonaBindingType,
  SpecialtyDefaultBinding, SpecialtyPromptSectionsCatalog,
} from '../../types';

// === Сравнение привязок по (type, condition, mode) ===
//
// Цель (target, path) в сравнении НЕ участвует: «Навык» привязывается по имени
// (skillName), а project/projectPath — конкретным id, и одно и то же типовое
// умение может быть материализовано в разные цели в момент создания персоны.
// Для подсчёта «нехватки» важно, что суть одна и та же (тот же тип, режим и
// условие), а не куда именно оно смотрит.
function bindingKey(
  type: PersonaBindingType, condition: string, mode: PersonaBindingMode,
): string {
  return `${type}|${condition.trim()}|${mode}`;
}

function missingDefaults(
  defaults: SpecialtyDefaultBinding[], persona: Persona,
): SpecialtyDefaultBinding[] {
  if (defaults.length === 0) return [];
  const present = new Set<string>();
  for (const b of persona.bindings ?? []) {
    present.add(bindingKey(b.type, b.condition, b.mode));
  }
  return defaults.filter(d => !present.has(bindingKey(d.type, d.condition, d.mode)));
}

// === Состояние кнопки «Применить типовые» ===
type ApplyState =
  | { kind: 'idle' }
  | { kind: 'applying' }
  | { kind: 'success'; applied: number }
  | { kind: 'error' };

// Через сколько миллисекунд состояние success/error сбрасывается обратно в idle.
// 3 секунды — достаточно, чтобы человек прочитал «Добавлено умений: N», и
// не слишком долго, чтобы кнопка не висела «мёртвой» после реального действия.
const RESET_AFTER_MS = 3000;

// === Заголовок секции (общий с SpecialtyRoleView / SpecialtyEditView) ===
function SectionTitle({ children }: { children: React.ReactNode }): React.ReactElement {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
      color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.07em',
    }}>{children}</div>
  );
}

// Строка пометок в одну линию через « · » — порядок «модель, умения».
// Склеиваются ТОЛЬКО те, что есть: «модель задана вручную» идёт первой
// (про модель), «не хватает типовых умений: N» — второй (про умения).
function NotesLine({ notes }: { notes: string[] }): React.ReactElement | null {
  if (notes.length === 0) return null;
  return (
    <div style={{
      fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: 2,
    }}>{notes.join(' · ')}</div>
  );
}

// Кнопка состояния «Применить типовые» — четыре формы под стейт-машину.
function ApplyStateButton({ state, onClick }: {
  state: ApplyState;
  onClick: () => void;
}): React.ReactElement {
  switch (state.kind) {
    case 'applying':
      return (
        <Button variant="ghost" size="sm" disabled>
          Применяю…
        </Button>
      );
    case 'success':
      return (
        <Button variant="ghost" size="sm" disabled>
          Добавлено умений: {state.applied}
        </Button>
      );
    case 'error':
      return (
        <Button variant="ghost" size="sm" disabled>
          Не удалось применить — попробуйте позже
        </Button>
      );
    case 'idle':
    default:
      return (
        <Button variant="ghost" size="sm" onClick={onClick}>
          Применить типовые
        </Button>
      );
  }
}

// Строка персоны с аватаром, именем, пометками и кнопкой «Применить типовые».
function RichPersonaRow({ persona, notes, applyState, onApply }: {
  persona: Persona;
  notes: string[];
  applyState: ApplyState;
  onApply: () => void;
}): React.ReactElement {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: '7px 10px',
      border: `1px solid ${C.borderLight}`, borderRadius: R.md,
      background: C.bgWhite, fontFamily: FONT.sans, fontSize: FS.xs,
      color: C.textPrimary, boxSizing: 'border-box',
      flexWrap: 'wrap',
    }}>
      <PersonaAvatar persona={persona} size={26} />
      <div style={{
        flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column',
      }}>
        <span style={{
          fontWeight: 600, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{persona.name}</span>
        <NotesLine notes={notes} />
      </div>
      <ApplyStateButton state={applyState} onClick={onApply} />
    </div>
  );
}

export interface RolePeopleSliceProps {
  roleKey: string;
  // Уже отфильтрованные по specialty === roleKey (фильтрация делается родителем,
  // чтобы не дублировать SpecialtyRoleView.filter(p => p.specialty === roleKey)).
  personas: Persona[];
  // Каталог секций промптов (и типовых умений роли); null — ещё не загружен.
  catalog: SpecialtyPromptSectionsCatalog | null;
  // Пометка T5 «модель задана вручную» — снаружи (parent считает по своим
  // правилам сравнения эффективной модели персоны с правилом роли). Если не
  // задана — все manual=false, пометка не показывается.
  manualByPersona?: Record<string, boolean>;
  // Колбэк после успешного apply-defaults: parent получит обновлённую персону
  // (новый набор bindings) и обновит свой список/стор.
  onPersonaUpdated?: (persona: Persona) => void;
}

export function RolePeopleSlice({
  roleKey, personas, catalog, manualByPersona, onPersonaUpdated,
}: RolePeopleSliceProps): React.ReactElement {
  // Типовой профиль умений роли — из каталога секций промптов.
  // SpecialtyPromptSectionsCatalog.specialties[roleKey].defaultBindings несёт
  // ТОЛЬКО дефолты кода (или слой global/owner, если бэкенд их туда подмешал);
  // сравнение идёт по (type, condition, mode), а не по target/path (см. выше).
  const defaults: SpecialtyDefaultBinding[] = useMemo(
    () => catalog?.specialties[roleKey]?.defaultBindings ?? [],
    [catalog, roleKey],
  );

  // Сортировка по имени (ru) — стабильная, как в SpecialRulesTab.
  const sorted = useMemo(() => {
    return [...personas].sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  }, [personas]);

  // Локальный override: после успешного apply-defaults API возвращает обновлённого
  // persona с новым набором bindings. Родитель мог ещё не успеть обновить prop
  // (realtime-сигнал придёт отдельно через personas_changed) — пока держим свою
  // копию, чтобы число «не хватает» пересчиталось сразу.
  const [overrides, setOverrides] = useState<Record<string, Persona>>({});

  // Состояния кнопки «Применить типовые» — по persona.id.
  const [applyStates, setApplyStates] = useState<Record<string, ApplyState>>({});

  // Текущее открытое подтверждение (null — закрыто).
  const [confirmFor, setConfirmFor] = useState<Persona | null>(null);

  // Словарь таймеров авто-сброса success/error — чистим на unmount.
  const resetTimers = useRef<Record<string, number>>({});
  useEffect(() => () => {
    for (const t of Object.values(resetTimers.current)) {
      window.clearTimeout(t);
    }
    resetTimers.current = {};
  }, []);

  const scheduleReset = (personaId: string) => {
    if (resetTimers.current[personaId]) {
      window.clearTimeout(resetTimers.current[personaId]);
    }
    resetTimers.current[personaId] = window.setTimeout(() => {
      setApplyStates(s => {
        const { [personaId]: _drop, ...rest } = s;
        return rest;
      });
      delete resetTimers.current[personaId];
    }, RESET_AFTER_MS);
  };

  const handleApplyClick = (persona: Persona) => {
    setConfirmFor(persona);
  };

  const handleConfirmApply = async () => {
    const persona = confirmFor;
    if (!persona) return;
    setConfirmFor(null);
    setApplyStates(s => ({ ...s, [persona.id]: { kind: 'applying' } }));
    try {
      const result = await api.personas.applyDefaultBindings(persona.id);
      // Обновлённая персона пришла с сервера — кладём в локальный override и
      // уведомляем родителя. Число нехватки пересчитается на следующем рендере
      // (через overrides[persona.id] ?? persona).
      setOverrides(o => ({ ...o, [persona.id]: result.persona }));
      setApplyStates(s => ({ ...s, [persona.id]: { kind: 'success', applied: result.applied } }));
      onPersonaUpdated?.(result.persona);
      showToast('Типовые умения', `Добавлено умений: ${result.applied}`);
      scheduleReset(persona.id);
    } catch (_e) {
      setApplyStates(s => ({ ...s, [persona.id]: { kind: 'error' } }));
      showToast('Типовые умения', 'Не удалось применить — попробуйте позже');
      scheduleReset(persona.id);
    }
  };

  if (sorted.length === 0) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <SectionTitle>Кто работает по этой роли</SectionTitle>
        <div style={{
          fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5,
        }}>
          По этой роли пока никто не работает. Назначьте специальность персоне в её
          карточке — она получит эти модели и доступы автоматически.
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <SectionTitle>Кто работает по этой роли</SectionTitle>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
        {sorted.map(persona => {
          // Берём override (после успешного apply-defaults), иначе prop.
          const effective = overrides[persona.id] ?? persona;
          const missing = missingDefaults(defaults, effective);
          // Пометки в порядке «модель, умения» — T5 идёт первой.
          const notes: string[] = [];
          if (manualByPersona?.[persona.id]) notes.push('модель задана вручную');
          if (missing.length > 0) {
            notes.push(`не хватает типовых умений: ${missing.length}`);
          }
          const applyState = applyStates[persona.id] ?? { kind: 'idle' };
          return (
            <RichPersonaRow
              key={persona.id}
              persona={effective}
              notes={notes}
              applyState={applyState}
              onApply={() => handleApplyClick(effective)}
            />
          );
        })}
      </div>
      {confirmFor && (
        <ConfirmDialog
          title="Применить типовые умения"
          subtitle={`Добавить персоне ${confirmFor.name} недостающие типовые умения этой роли? Уже настроенные умения останутся как есть.`}
          confirmLabel="Добавить"
          onConfirm={handleConfirmApply}
          onCancel={() => setConfirmFor(null)}
        />
      )}
    </div>
  );
}

