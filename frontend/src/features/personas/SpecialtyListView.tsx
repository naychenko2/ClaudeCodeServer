// Экран «Список ролей» раздела «Специальности».
// Адресуется как #/personas/specialties (без roleKey) — на нём человек выбирает,
// какую роль открыть. Роли в каталоге без служебной «Не задана» (отфильтровано
// в realRoles локально). Карточка «Любая специальность» НЕ рисуется.
// Все роли каталога показываются всегда (без тумблера): персонализировать можно
// и роли, по которым пока никто не работает.
//
// С переходом на единый глобальный слой (f8e7d0e0) — все настройки ролей
// общие, аватарки персон показываются всегда.

import { useMemo, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { PersonaAvatar } from './PersonaAvatar';
import { RoleAvatar } from '../../components/specialties/RoleAvatar';
import type { Persona, SpecialtyCatalogEntry, SpecialtySettingsLayer } from '../../types';

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
// или краткое summary тройки.
function rowSubtitle(
  triple: [string, string, string],
  people: Persona[],
): string {
  const hasAny = triple.some(v => !!v);
  if (!hasAny) {
    if (people.length === 0) return 'Правил нет';
    const word = people.length === 1 ? 'персона работает' :
      people.length < 5 ? 'персоны работают' : 'персон работают';
    return `Правил нет — ${people.length} ${word} по общим настройкам`;
  }
  const filled = triple.filter(v => !!v).length;
  return filled === 3 ? 'Сильная · Средняя · Слабая заданы' : `Задано ${filled} из 3 уровней`;
}

// === Список персон роли ===
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

// === Значок роли в строке списка — аватарка из assets/specialties/<key>.jpg,
//     при отсутствии файла — lucide-глиф в круге цвета роли (RoleAvatar). ===
function RoleIcon({ catalog, roleKey, size }: {
  catalog: SpecialtyCatalogEntry | null; roleKey: string; size: number;
}): React.ReactElement {
  return <RoleAvatar catalog={catalog} roleKey={roleKey} size={size} />;
}

// === Стопка аватаров ===
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

// === Сетка карточек ролей ===
//
// minmax(190px, 1fr) — нижний предел держит карточки читабельными (аватар 40 + имя
// + подпись + padding), 1fr сжимает треки до фактической ширины контейнера. Шире, чем
// в витрине персон (`PersonasHub.showcaseGrid` — 150 px): подписи ролей длиннее имён
// (напр. «Исполнитель», «Координатор»), и при 150 px они рвутся посреди слова.
const roleGrid: React.CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))',
  gap: 12,
};

// Обрезка в N строк многоточием: длинные подписи и описания не должны растягивать
// карточку — иначе соседние в ряду разъезжаются по высоте.
function clampLines(lines: number): React.CSSProperties {
  return {
    display: '-webkit-box',
    WebkitLineClamp: lines,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
  } as React.CSSProperties;
}

// Карточка роли в стиле AssistantCard (PersonasHub.tsx:282-285): белый фон,
// рамка C.border, радиус R.xxl, padding 14, без тени; ховер меняет цвет рамки на
// C.accentMuted. Контент — по образцу: аватар 40, название 13.5/700, подпись
// 11.5 C.textMuted, описание с клэмпом 2 строки (12, lineHeight 1.5). Доп. поля
// (PersonaStack, признак «свои правила») — как в постановке задачи.
function RoleCard({ role, layerSettings, people, dimmed, onOpen }: {
  role: SpecialtyCatalogEntry;
  layerSettings: SpecialtySettingsLayer | null;
  people: Persona[];
  // Роль без своих правил — приглушаем, но карточку не прячем: она отвечает на вопрос
  // «по каким настройкам работают её персоны», а не «трогали ли её».
  dimmed?: boolean;
  onOpen: () => void;
}): React.ReactElement {
  const triple = tripleOfLayer(layerSettings, role.key);
  const [hover, setHover] = useState(false);
  return (
    <button type="button" onClick={onOpen} title={role.label}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      className="cc-role-card"
      style={{
        display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 9,
        minWidth: 0, padding: 14,
        border: `1px solid ${hover ? C.accentMuted : C.border}`, borderRadius: R.xxl,
        background: C.bgWhite,
        textAlign: 'left', cursor: 'pointer',
        fontFamily: FONT.sans, boxSizing: 'border-box', height: '100%',
        opacity: dimmed ? 0.7 : 1,
        transition: 'border-color 0.15s',
        outline: 'none',
      }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0, width: '100%' }}>
        <RoleIcon catalog={role} roleKey={role.key} size={40} />
        <div style={{ minWidth: 0, flex: 1 }}>
          <div style={{
            fontSize: FS.md, fontWeight: 700, color: C.textHeading, lineHeight: 1.3,
            // overflowWrap: 'break-word' — разрыв слова остаётся страховкой на совсем
            // узком контейнере, но там, где слово влезает целиком, перенос идёт по
            // пробелу ('anywhere' рвал бы «Исполнитель», «Координатор» и т.п.).
            overflowWrap: 'break-word',
            ...clampLines(2),
          }}>{role.label}</div>
          <div style={{
            fontSize: FS.xs, color: C.textMuted, marginTop: 1,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{rowSubtitle(triple, people)}</div>
        </div>
      </div>
      {role.description && (
        <div style={{
          fontSize: 12, color: C.textMuted, lineHeight: 1.5,
          minWidth: 0, width: '100%',
          ...clampLines(2),
        }}>{role.description}</div>
      )}
      {people.length > 0 && (
        <div style={{ marginTop: 'auto', paddingTop: 4 }}>
          <PersonaStack people={people} />
        </div>
      )}
    </button>
  );
}

// === Основной экран ===
export interface SpecialtyListViewProps {
  catalog: SpecialtyCatalogEntry[] | null;
  layerSettings: SpecialtySettingsLayer | null;
  // Полный список персон владельца — единый источник для стопок аватаров.
  personas: Persona[];
  onOpenRole: (key: string) => void;
}

export function SpecialtyListView({
  catalog, layerSettings, personas, onOpenRole,
}: SpecialtyListViewProps): React.ReactElement {
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
  // Все остальные роли каталога — без своих правил, с персонами и без: показываем
  // всегда, персонализировать можно и роли без персон (например, назвать
  // «Библиотекаря» до первой персоны).
  const noRules = useMemo(() => sorted.filter(r => {
    const t = tripleOfLayer(layerSettings, r.key);
    return !t.some(v => !!v);
  }), [sorted, layerSettings]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {/* Список ролей с правилами */}
      {withRules.length === 0 ? (
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl,
          padding: '22px 18px', textAlign: 'center',
          color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.55,
        }}>
          <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: 4 }}>
            Правил пока нет
          </div>
          Откройте роль — и задайте ей модели, пресеты и типовые умения.
        </div>
      ) : (
        <div style={roleGrid}>
          {withRules.map(r => (
            <RoleCard key={r.key} role={r} layerSettings={layerSettings}
              people={personasByRole.get(r.key) ?? []}
              onOpen={() => onOpenRole(r.key)} />
          ))}
        </div>
      )}

      {/* Секция «Без своих правил» — все роли каталога без правил моделей: и с
          персонами, и без них. На общем слое аватарки показываются всегда
          (нет отдельного owner/user). */}
      {noRules.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.sm }}>
          <div style={{
            fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
            color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.07em',
          }}>
            Без своих правил
          </div>
          <div style={roleGrid}>
          {noRules.map(r => (
            <RoleCard key={r.key} role={r} layerSettings={layerSettings} dimmed
              people={personasByRole.get(r.key) ?? []}
              onOpen={() => onOpenRole(r.key)} />
          ))}
          </div>
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
