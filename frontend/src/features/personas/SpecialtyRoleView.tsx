// Экран «Визитка роли» (волна 4 «Персонализация специальностей», §4.1).
// Адресуется как #/personas/specialties/{roleKey}. Строго read-only: ни
// одного поля ввода и ни одного тумблера (спека «Просмотр роли — только
// чтение»). Единственный вход в правку — кнопка «Настроить», ведущая на
// экран формы (отдельный под-адрес).
//
// Карточка «Любая специальность» НЕ рисуется (см. docs/product/specialties-
// personalization §4). Группы одинаковых троек уходят — каждая роль отдельно.

import { useMemo } from 'react';
import { ChevronLeft } from 'lucide-react';
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

// === Подпись значения ячейки модели ===
//
// Три уровня (Сильная / Средняя / Слабая) в строке матрицы. Пустая ячейка
// даёт P24 «Как „Модели по умолчанию"». Заполненная — короткая подпись из
// presets/presets.ts (routeDisplayLabel). Упрощённо показываем «как есть».
function cellLabel(value: string | null | undefined): string {
  if (!value) return '';
  return value;
}

// === Значок роли в hero-секции визитки ===
function RoleHeroIcon({ catalog, roleKey, size }: {
  catalog: SpecialtyCatalogEntry; roleKey: string; size: number;
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
      <GlyphIcon name={iconName} fallback={() => null} size={Math.round(size * 0.5)} />
    </span>
  );
}

// === Срез «Кто работает по этой роли» — read-only строка персоны ===
function PersonaRow({ persona }: { persona: Persona }): React.ReactElement {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: '7px 10px',
      border: `1px solid ${C.borderLight}`, borderRadius: R.md,
      background: C.bgWhite, fontFamily: FONT.sans, fontSize: FS.xs,
      color: C.textPrimary, boxSizing: 'border-box',
    }}>
      <PersonaAvatar persona={persona} size={26} />
      <span style={{
        fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{persona.name}</span>
    </div>
  );
}

// === Карточка секции (внутренние секции визитки) ===
function SectionTitle({ children }: { children: React.ReactNode }): React.ReactElement {
  return (
    <div style={{
      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
      color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.07em',
      marginBottom: SP.sm,
    }}>{children}</div>
  );
}

function FlatSection({ children }: { children: React.ReactNode }): React.ReactElement {
  return (
    <div style={{
      paddingTop: SP.md, marginTop: SP.md,
      borderTop: `1px solid ${C.borderLight}`,
    }}>{children}</div>
  );
}

// === Основной экран ===
export interface SpecialtyRoleViewProps {
  roleKey: string;
  catalog: SpecialtyCatalogEntry[];
  // Слой текущего выбора; значения имени/описания больше не зависят от слоя,
  // но его всё ещё использует секция моделей и переключатель.
  layer: Scope;
  layerSettings: SpecialtySettingsLayer | null;
  // Не используется после отката персонализации подписей — принимается для
  // совместимости с PersonasSpecialties; удалить вместе с правкой родителя.
  userLayer?: SpecialtySettingsLayer | null;
  // Только для owner-слоя: персоны, работающие по роли. На других слоях —
  // пустой массив (T8: «за другого пользователя список был бы про чужих»).
  personas: Persona[];
  onBack: () => void;
  onEdit: () => void;
}

export function SpecialtyRoleView({
  roleKey, catalog, layer, layerSettings, personas, onBack, onEdit,
}: SpecialtyRoleViewProps): React.ReactElement {
  const isMobile = useIsMobile();
  const role = useMemo(() => catalog.find(r => r.key === roleKey) ?? null, [catalog, roleKey]);

  // Без роли (например, roleKey пришёл мусором или ещё не загрузился) —
  // пустое состояние с возвратом.
  if (!role) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <BackRow onBack={onBack} />
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl,
          padding: '22px 18px', textAlign: 'center',
          color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.55,
        }}>Роль не найдена в каталоге.</div>
      </div>
    );
  }

  // Имя и описание роли — из каталога (системные подписи, не персонализируются).
  const roleName = role.label;
  const roleDescription = role.description;

  // Тройка моделей в слое (effective values — пока смотрим слой как есть;
  // кросс-слойное наследование для моделей рисует отдельный резолв на бэке,
  // здесь показываем именно текущий слой, чтобы не врать про источник).
  const rec = layerSettings?.specialties?.[roleKey] ?? null;
  const triple: [string, string, string] = rec
    ? [rec.tierStrong ?? '', rec.tierMedium ?? '', rec.tierWeak ?? '']
    : ['', '', ''];
  const hasAnyRule = triple.some(v => !!v);

  const isOwner = layer === 'owner';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {/* Шапка визитки: «Назад к списку» + кнопка «Настроить» (единственный
          вход в правку — спека §4.1). */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <button type="button" onClick={onBack} style={{
          display: 'inline-flex', alignItems: 'center', gap: 6,
          font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
          color: C.textHeading, background: 'none', border: 'none',
          padding: 0, cursor: 'pointer',
        }}>
          <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          <span>Специальности</span>
        </button>
        <span style={{ flex: 1 }} />
        <button type="button" onClick={onEdit} style={{
          font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
          color: C.textHeading, background: C.bgWhite,
          border: `1px solid ${C.border}`, borderRadius: R.md,
          padding: '6px 12px', cursor: 'pointer',
        }}>Настроить</button>
      </div>

      {/* Переключатель слоёв — для навигации между специализациями роли */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
      }}>
        <div style={{
          display: 'flex', gap: 2, background: C.bgSelected, borderRadius: R.pill, padding: 2,
          width: isMobile ? '100%' : undefined, flexWrap: isMobile ? 'wrap' : undefined,
        }}>
          <LayerSwitch
            scope={layer}
            onScope={() => { /* слой меняется через navPush/PersonasSpecialties */ }}
            isAdmin={true}
            isMobile={isMobile}
          />
        </div>
        <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
          Слой определяет, кого коснётся правило.
        </span>
      </div>

      {/* Hero визитки: значок + имя + описание (read-only).
          Значок — DynamicIcon, без выбора (решение владельца §2.3). */}
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: isMobile ? SP.md : SP.lg, display: 'flex',
        flexDirection: isMobile ? 'column' : 'row',
        gap: SP.md, alignItems: isMobile ? 'center' : 'flex-start',
      }}>
        <RoleHeroIcon catalog={role} roleKey={roleKey} size={isMobile ? 64 : 80} />
        <div style={{
          flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column',
          gap: SP.sm,
          textAlign: isMobile ? 'center' : 'left',
        }}>
          <h2 style={{
            fontFamily: FONT.serif, fontSize: isMobile ? FS.xl : FS.h2, fontWeight: 700,
            color: AGENT_COLORS[roleColorKey(role, roleKey)] ?? C.textHeading,
            margin: 0,
          }}>{roleName}</h2>
          <div style={{
            fontSize: FS.md, lineHeight: 1.5, color: C.textSecondary,
          }}>{roleDescription}</div>
        </div>
      </div>

      {/* «Модели по уровням» — три ячейки, плейсхолдер P24 для пустых. */}
      <FlatSection>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
          <SectionTitle>Модели по уровням</SectionTitle>
          <span style={{ flex: 1 }} />
          {!hasAnyRule && (
            <span style={{
              fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
              padding: '2px 8px', borderRadius: R.max,
              background: C.bgSelected, color: C.textSecondary,
            }}>Правил нет</span>
          )}
        </div>
        <div style={{
          display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))',
          gap: SP.sm,
        }}>
          {([
            { tier: 'Сильная', value: triple[0] },
            { tier: 'Средняя', value: triple[1] },
            { tier: 'Слабая', value: triple[2] },
          ] as const).map(({ tier, value }) => (
            <div key={tier} style={{
              padding: '8px 10px',
              background: C.bgCard, borderRadius: R.md,
              border: `1px solid ${C.borderLight}`,
              fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary,
            }}>
              <div style={{ fontWeight: 700, color: C.textHeading, marginBottom: 2 }}>{tier}</div>
              <div style={{ color: value ? C.textPrimary : C.textMuted }}>
                {value ? cellLabel(value) : 'Как «Модели по умолчанию»'}
              </div>
            </div>
          ))}
        </div>
        {!hasAnyRule && (
          <div style={{
            fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5,
          }}>
            Правил нет — персоны роли работают по «Моделям по умолчанию».
          </div>
        )}
      </FlatSection>

      {/* Срез «Кто работает по этой роли» — read-only список персон.
          Только на owner-слое: на global/user подмешивать своих персон — враньё (T8). */}
      <FlatSection>
        <SectionTitle>Кто работает по этой роли</SectionTitle>
        {isOwner ? (
          personas.length === 0 ? (
            <div style={{
              fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5,
            }}>
              По этой роли пока никто не работает. Назначьте специальность персоне в её
              карточке — она получит эти модели и доступы автоматически.
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
              {personas.map(p => <PersonaRow key={p.id} persona={p} />)}
            </div>
          )
        ) : (
          <div style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5 }}>
            Список персон показан только в ваших настройках: за другого пользователя
            он был бы про ваших персон.
          </div>
        )}
      </FlatSection>
    </div>
  );
}

// Кнопка «Назад к списку» в пустом состоянии (роль не найдена).
function BackRow({ onBack }: { onBack: () => void }): React.ReactElement {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      <button type="button" onClick={onBack} style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
        color: C.textHeading, background: 'none', border: 'none',
        padding: 0, cursor: 'pointer',
      }}>
        <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        <span>Специальности</span>
      </button>
    </div>
  );
}
