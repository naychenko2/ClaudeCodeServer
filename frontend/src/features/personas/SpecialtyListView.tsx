// Экран «Список ролей» раздела «Специальности».
// Адресуется как #/personas/specialties (без roleKey) — на нём человек выбирает,
// какую роль открыть. Роли в каталоге без служебной «Не задана» (отфильтровано
// в realRoles локально). Карточка «Любая специальность» НЕ рисуется.
//
// Слои (для всех / только для меня / пользователю …): имена ролей приходят из
// каталога, подпись под строкой — summary правил слоя. Стопка аватаров «кто
// работает» — только на слое «Только для меня», иначе подмешивали бы чужих
// персон (принцип T8 — на чужом слое не рассказываем про своих).

import { useMemo, useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { PersonaAvatar } from './PersonaAvatar';
import { useIsMobile } from '../../lib/breakpoints';
import { roleIconName, roleColorKey } from '../../lib/specialties';
import type { Persona, SpecialtyCatalogEntry, SpecialtySettingsLayer } from '../../types';
import type { Scope } from './personaSpecialtyShared';
import { LayerSwitch } from './personaSpecialtyShared';
import { GlyphIcon } from '../../lib/projectGlyphs';

// Каталог без служебной «none» (она живёт на стороне персоны как specialty=undefined).
// Дубликат catalogRoles из lib/specialties.ts — здесь локально, чтобы не
// импортировать приватную логику. Все 14 ролей волны 4 имеют описание и иконку.
function realRoles(catalog: SpecialtyCatalogEntry[] | null): SpecialtyCatalogEntry[] {
  return (catalog ?? []).filter(e => e.key !== 'none');
}

// Правила на слое (для краткой сводки и подписи «Правил нет»).
// Тройка — массив из трёх строк (strong/medium/weak), пустая строка = поле не задано.
function tripleOfLayer(layer: SpecialtySettingsLayer | null, key: string): [string, string, string] {
  const rec = layer?.specialties?.[key];
  if (!rec) return ['', '', ''];
  return [
    rec.tierStrong ?? '',
    rec.tierMedium ?? '',
    rec.tierWeak ?? '',
  ];
}

// Подпись роли в строке списка: «Правил нет — N персон работают по общим настройкам»
// (P25) или краткое summary тройки. Число персон — только на owner (см. personasOf
// в useRolePersonasOf ниже).
function rowSubtitle(
  triple: [string, string, string],
  people: Persona[],
  isOwner: boolean,
): string {
  const hasAny = triple.some(v => !!v);
  if (!hasAny) {
    if (!isOwner) return 'Правил нет';
    if (people.length === 0) return 'Правил нет, персон по этой роли пока нет';
    const word = people.length === 1 ? 'персона работает' :
      people.length < 5 ? 'персоны работают' : 'персон работают';
    return `Правил нет — ${people.length} ${word} по общим настройкам`;
  }
  const filled = triple.filter(v => !!v).length;
  return filled === 3 ? 'Сильная · Средняя · Слабая заданы' : `Задано ${filled} из 3 уровней`;
}

// === Список персон роли, только для owner-слоя ===
//
// Собственная функция вместо useRoleSlices из SpecialRulesTab: нам нужны ТОЛЬКО id
// и имя, а не резолв по уровням (на owner-строке списка показываем аватары без
// чипов моделей — это «кто работает», а не «какими моделями»). Срез с чипами
// живёт внутри визитки роли.
function useRolePersonasOf(allPersonas: Persona[]): Map<string, Persona[]> {
  return useMemo(() => {
    const out = new Map<string, Persona[]>();
    for (const p of allPersonas) {
      const k = !p.specialty || p.specialty === 'none' ? 'none' : p.specialty;
      const list = out.get(k);
      if (list) list.push(p);
      else out.set(k, [p]);
    }
    return out;
  }, [allPersonas]);
}

// === Значок роли в строке списка — DynamicIcon на цветной подложке ===
function RoleIcon({ catalog, roleKey, size }: {
  catalog: SpecialtyCatalogEntry | null; roleKey: string; size: number;
}): React.ReactElement {
  const iconName = roleIconName(catalog, roleKey);
  const colorKey = roleColorKey(catalog, roleKey);
  const bg = AGENT_COLORS[colorKey] ?? AGENT_COLORS.brown;
  return (
    <span title="Значок роли задан продуктом и не настраивается" style={{
      flex: 'none',
      width: size, height: size, borderRadius: R.full,
      background: bg, color: 'white',
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    }}>
      <GlyphIcon name={iconName} fallback={() => null} size={Math.round(size * 0.55)} />
    </span>
  );
}

// === Стопка аватаров (только на owner-слое) ===
function PersonaStack({ people }: { people: Persona[] }): React.ReactElement | null {
  if (people.length === 0) return null;
  const shown = people.slice(0, 3);
  const more = people.length - shown.length;
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0 }}>
      {shown.map((p, i) => (
        <span key={p.id} style={{
          marginLeft: i === 0 ? 0 : -8,
          border: `2px solid ${C.bgWhite}`,
          borderRadius: R.full,
          display: 'inline-flex',
        }}>
          <PersonaAvatar persona={p} size={22} />
        </span>
      ))}
      {more > 0 && (
        <span style={{
          marginLeft: -8, padding: '0 6px', height: 22, borderRadius: 11,
          background: C.bgSelected, color: C.textSecondary,
          fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
          border: `2px solid ${C.bgWhite}`,
          display: 'inline-flex', alignItems: 'center',
        }}>+{more}</span>
      )}
    </span>
  );
}

// === Строка роли ===
function RoleRow({ role, layer, isOwner, people, onOpen }: {
  role: SpecialtyCatalogEntry;
  layer: SpecialtySettingsLayer | null;
  isOwner: boolean;
  people: Persona[];
  onOpen: () => void;
}): React.ReactElement {
  const triple = tripleOfLayer(layer, role.key);
  return (
    <button type="button" onClick={onOpen} style={{
      display: 'flex', alignItems: 'center', gap: 12,
      width: '100%', minWidth: 0,
      padding: '11px 14px',
      border: `1px solid ${C.border}`, borderRadius: R.xl,
      background: C.bgWhite, textAlign: 'left', cursor: 'pointer',
      fontFamily: FONT.sans, boxSizing: 'border-box',
    }}>
      <RoleIcon catalog={role} roleKey={role.key} size={36} />
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
        <span style={{
          fontSize: FS.base, fontWeight: 700, color: C.textHeading,
          minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{role.label}</span>
        <span style={{
          fontSize: FS.xs, color: C.textMuted,
          minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{rowSubtitle(triple, people, isOwner)}</span>
      </span>
      {isOwner && <PersonaStack people={people} />}
      <ChevronRight size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{
        color: C.textMuted, flexShrink: 0,
      }} />
    </button>
  );
}

// === Основной экран ===
export interface SpecialtyListViewProps {
  isAdmin: boolean;
  layer: Scope;
  onLayerChange: (s: Scope) => void;
  catalog: SpecialtyCatalogEntry[] | null;
  layerSettings: SpecialtySettingsLayer | null;
  // Полный список персон владельца — единый источник для стопок аватаров на
  // owner-слое. Загрузка делается родителем.
  personas: Persona[];
  onOpenRole: (key: string) => void;
}

export function SpecialtyListView({
  isAdmin, layer, onLayerChange, catalog, layerSettings, personas, onOpenRole,
}: SpecialtyListViewProps): React.ReactElement {
  const isMobile = useIsMobile();
  // «Показать все роли каталога» — состояние P15, чтобы персонализировать можно
  // было и роли без персон (например, назвать «Библиотекаря» до первой персоны).
  // Сбрасывается при смене слоя: иначе после переключения владелец видит чужие
  // роли без правил (на его слое) — это сбивает с толку (QA B4 «Что 2», 25.08.2026).
  const [showAll, setShowAll] = useState(false);
  const personasByRole = useRolePersonasOf(personas);
  const roles = useMemo(() => realRoles(catalog), [catalog]);
  // Сортировка — по подписи каталога (русская локаль).
  const sorted = useMemo(() => {
    return [...roles].sort((a, b) => a.label.localeCompare(b.label, 'ru'));
  }, [roles]);
  const withRules = useMemo(() => sorted.filter(r => {
    const t = tripleOfLayer(layerSettings, r.key);
    return t.some(v => !!v);
  }), [sorted, layerSettings]);
  const noRules = useMemo(() => sorted.filter(r => {
    const t = tripleOfLayer(layerSettings, r.key);
    if (t.some(v => !!v)) return false;
    return (personasByRole.get(r.key)?.length ?? 0) > 0;
  }), [sorted, layerSettings, personasByRole]);
  const rest = useMemo(() => sorted.filter(r => {
    const t = tripleOfLayer(layerSettings, r.key);
    if (t.some(v => !!v)) return false;
    return (personasByRole.get(r.key)?.length ?? 0) === 0;
  }), [sorted, layerSettings, personasByRole]);
  const isOwner = layer === 'owner';
  const covered = withRules.length;
  const total = roles.length;

  // Слой меняется снаружи — оборачиваем, чтобы сбросить раскрытие «Показать все».
  const handleLayerChange = (s: Scope) => {
    setShowAll(false);
    onLayerChange(s);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {/* Переключатель режима центра (P14, дословно из постановки) */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
      }}>
        <div style={{
          display: 'flex', gap: 2, background: C.bgSelected, borderRadius: R.pill, padding: 2,
          width: isMobile ? '100%' : undefined, flexWrap: isMobile ? 'wrap' : undefined,
        }}>
          <LayerSwitch
            scope={layer}
            onScope={handleLayerChange}
            isAdmin={isAdmin}
            isMobile={isMobile}
          />
          {/* Бейдж «N вручную» — сколько ролей каталога уже имеют правила на
              текущем слое. Виден всегда (даже при 0), чтобы на любом слое было
              видно, сколько ролей настроено (QA B4 «Что 1», 25.08.2026). */}
          {total > 0 && (
            <span title="Ролей с правилами на текущем слое" style={{
              fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
              color: covered > 0 ? C.textHeading : C.textMuted,
              background: covered > 0 ? C.bgWhite : C.bgSelected,
              padding: '2px 8px', borderRadius: 12, marginLeft: 8,
              border: covered > 0 ? `1px solid ${C.borderLight}` : 'none',
            }}>{covered} вручную</span>
          )}
        </div>
        <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
          Слой определяет, кого коснётся правило.
        </span>
      </div>

      {/* Список ролей с правилами */}
      {withRules.length === 0 ? (
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl,
          padding: '22px 18px', textAlign: 'center',
          color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.55,
        }}>
          <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: 4 }}>
            {layer === 'global' ? 'Общих правил пока нет' : 'Особых правил пока нет'}
          </div>
          Откройте роль — и задайте ей модели, пресеты и типовые умения.
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {withRules.map(r => (
            <RoleRow key={r.key} role={r} layer={layerSettings} isOwner={isOwner}
              people={personasByRole.get(r.key) ?? []}
              onOpen={() => onOpenRole(r.key)} />
          ))}
        </div>
      )}

      {/* Секция «Без своих правил» — роли без правил моделей, но с персонами.
          Только на owner, иначе чужие персоны (T8). На других слоях — пусто. */}
      {isOwner && noRules.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.sm }}>
          <div style={{
            fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
            color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.07em',
          }}>
            Без своих правил
          </div>
          {noRules.map(r => (
            <RoleRow key={r.key} role={r} layer={layerSettings} isOwner={isOwner}
              people={personasByRole.get(r.key) ?? []}
              onOpen={() => onOpenRole(r.key)} />
          ))}
        </div>
      )}

      {/* Переключатель «Показать все роли каталога» (P15) */}
      <label style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        cursor: 'pointer', marginTop: SP.sm,
      }}>
        <span
          role="switch" aria-checked={showAll}
          onClick={() => setShowAll(v => !v)}
          style={{
            position: 'relative', width: 42, height: 25,
            borderRadius: 13, background: showAll ? C.accent : C.track,
            transition: 'background 0.15s', cursor: 'pointer', flexShrink: 0,
          }}>
          <span style={{
            position: 'absolute', top: 2, left: showAll ? 19 : 2,
            width: 21, height: 21, borderRadius: '50%', background: C.bgWhite,
            boxShadow: 'var(--shadow-thumb)', transition: 'left 0.15s',
          }} />
        </span>
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
            Показать все роли каталога
          </span>
          <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            Роли, по которым пока никто не работает и правил нет — чтобы назвать их заранее.
          </span>
        </div>
      </label>

      {showAll && rest.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {rest.map(r => (
            <RoleRow key={r.key} role={r} layer={layerSettings} isOwner={isOwner}
              people={personasByRole.get(r.key) ?? []}
              onOpen={() => onOpenRole(r.key)} />
          ))}
        </div>
      )}

      {/* Подсказка под списком (P16) */}
      <div style={{
        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5,
        maxWidth: 640, marginTop: SP.sm,
      }}>
        Список ролей задан продуктом: добавить новую или удалить нельзя — на них держится
        распределение работы между персонами.
      </div>
    </div>
  );
}
