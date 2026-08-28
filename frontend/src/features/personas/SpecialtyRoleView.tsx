// Визитка роли (волна 2 «Специальности как у персон»). Адресуется как
// #/personas/specialties/{roleKey}. С переходом на единый глобальный слой
// (f8e7d0e0) — read-only для всех, правка только админу. Единственный вход
// в правку — кнопка «Редактировать» в шапке, ведущая на экран формы.
//
// Раскладка — по образцу PersonaPreview:
//   • шапка-тулбар: стрелка «Назад», аватар роли 40, название роли serif 28/500
//     в цвете роли, подпись, справа кнопка «Редактировать» (только админу);
//   • под тулбаром акцентная полоса `height:2, background:{accent}55`;
//   • шапка и полоса — во всю ширину центра (как у персоны), полотно под ними:
//     maxWidth isMobile ? 680 : 1020, margin 0 auto, раскладка display:flex,
//     gap:28, flexWrap:wrap — визитка flex:1 1 380px, правая колонка flex:1 1 300px;
//   • отдельного hero (аватар + название) нет: идентичность роли несёт тулбар,
//     второй заголовок дублировал его; описание на мобиле — строкой под полосой;
//   • разделы визитки — сегмент в тулбаре (PillSwitch, по образцу вкладок
//     персоны в PersonaToolbar): «Общая информация» (секции «Настройки» и
//     «Секции промпта») и «Умения» (типовой профиль привязок — RoleBindingsBlock
//     в режиме view); правая колонка RolePeopleSlice общая для обоих разделов;
//   • блоки — плоские секции на общем фоне, без белых коробок:
//     { borderTop:'1px solid C.borderLight', paddingTop:20 }, заголовки через
//     общий SectionLabel; внутренние чипы фактов — как factChip в PersonaPreview;
//   • правая колонка — RolePeopleSlice (список персон роли).
//
// Карточка «Любая специальность» НЕ рисуется.

import { useMemo, useState } from 'react';
import { Book, ChevronLeft, Info, Pencil } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { roleColorKey } from '../../lib/specialties';
import { RoleAvatar } from '../../components/specialties/RoleAvatar';
import { RoleBindingsBlock } from '../../components/specialties/RoleBindingsBlock';
import { RolePeopleSlice } from '../../components/specialties/RolePeopleSlice';
import { RolePresetsBlock } from '../../components/specialties/RolePresetsBlock';
import { SectionLabel } from '../../features/tasks/bits';
import { useIsMobile } from '../../lib/breakpoints';
import { Toolbar, ToolbarIconButton, PillSwitch, tbBtnGhost } from '../../components/Toolbar';
import type {
  Persona, PersonaAccess, SpecialtyCatalogEntry,
  SpecialtyPromptSectionsCatalog, SpecialtySettingsLayer, ModelRoutePreset,
} from '../../types';

// === Подписи значений чипов «Настройки» ===

// Доступ роли: full | readOnly | custom. Человекочитаемые подписи совпадают
// с PersonaForm (см. tooltip «Полный / Только чтение / Свой список»).
const ACCESS_LABEL: Record<PersonaAccess, string> = {
  full: 'Полный',
  readOnly: 'Только чтение',
  custom: 'Свой список',
};

// Состав возможностей: ключ → имя. Совпадает с PersonaForm.TOOL_OPTIONS.
const TOOL_LABEL: Record<string, string> = {
  tasks: 'Задачи',
  notes: 'Заметки',
  web: 'Веб',
};

// === Значок роли (hero + тулбар) — аватарка из assets/specialties/<key>.jpg,
//     при отсутствии файла — lucide-глиф в круге цвета роли (RoleAvatar). ===
function RoleIcon({ catalog, roleKey, size }: {
  catalog: SpecialtyCatalogEntry; roleKey: string; size: number;
}): React.ReactElement {
  return <RoleAvatar catalog={catalog} roleKey={roleKey} size={size} />;
}

// === Плоская секция визитки ===
//
// Разделитель borderTop + paddingTop; без белой коробки вокруг — секция живёт
// прямо на фоне полотна, как в PersonaPreview.
const section: React.CSSProperties = {
  borderTop: `1px solid ${C.borderLight}`, paddingTop: 20,
};

// Чип факта: белая плашка, как factChip в PersonaPreview.
const factChip: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: 3,
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '8px 13px', fontFamily: FONT.sans, minWidth: 0,
};

// === Подпись значения уровня модели (tierStrong/Medium/Weak) ===
//
// «preset:{id}» — это ссылка на пресет, не сама модель. Раскрывать её здесь
// не нужно: подпись ограничивается именем пресета, чтобы человек понял,
// что модель — не прямая (состав цепочки настраивается в «Моделях по умолчанию»).
function tierLabel(value: string | null | undefined, presets: ModelRoutePreset[]): string {
  if (!value) return '';
  if (value.startsWith('preset:')) {
    const id = value.slice('preset:'.length);
    const preset = presets.find(p => p.id === id);
    return preset ? `Пресет «${preset.name}»` : `Пресет ${id}`;
  }
  return value;
}

// === Разделы визитки: «Общая информация» | «Умения» ===
//
// Сегмент в тулбаре — по образцу вкладок персоны (PersonaToolbar). Выбор —
// ЛОКАЛЬНОЕ состояние: при уходе с визитки (смена viewMode в родителе)
// компонент размонтируется и раздел сбрасывается на «Общую информацию».
// Иконки — как у видов персоны (ICON_SIZE.xs), на мобиле сегмент компактный.
type RoleTab = 'general' | 'skills';

const TAB_OPTIONS: { value: RoleTab; label: string; icon: React.ReactElement }[] = [
  { value: 'general', label: 'Общая информация', icon: <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
  { value: 'skills', label: 'Умения', icon: <Book size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
];

// === Основной экран ===
export interface SpecialtyRoleViewProps {
  roleKey: string;
  catalog: SpecialtyCatalogEntry[];
  layerSettings: SpecialtySettingsLayer | null;
  // Каталог секций промптов (и типовых умений роли); null — ещё не загружен.
  promptSectionsCatalog: SpecialtyPromptSectionsCatalog | null;
  // Персоны, работающие по роли. Аватарки показываются на общем слое всегда.
  personas: Persona[];
  // Колбэк после успешного apply-defaults на срезе персон — перечитывает стор персон.
  onPersonaUpdated?: (persona: Persona) => void;
  onBack: () => void;
  // Колбэк кнопки «Редактировать». На не-админе НЕ вызывается — кнопка просто не рисуется.
  onEdit?: () => void;
  isAdmin?: boolean;
}

export function SpecialtyRoleView({
  roleKey, catalog, layerSettings, promptSectionsCatalog,
  personas, onPersonaUpdated, onBack, onEdit, isAdmin,
}: SpecialtyRoleViewProps): React.ReactElement {
  const isMobile = useIsMobile();
  // Активный раздел визитки — локальное состояние (без persistKey): уход с
  // экрана размонтирует визитку, и раздел сбрасывается на «Общую информацию».
  const [tab, setTab] = useState<RoleTab>('general');
  const role = useMemo(() => catalog.find(r => r.key === roleKey) ?? null, [catalog, roleKey]);
  const accent = role ? (AGENT_COLORS[roleColorKey(role, roleKey)] ?? C.textHeading) : C.textHeading;

  // Без роли (например, roleKey пришёл мусором или ещё не загрузился) —
  // пустое состояние с возвратом.
  if (!role) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: isMobile ? '20px 16px 32px' : '26px 32px 40px' }}>
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

  // Шаблон прав и инструментов — из каталога: эффективные значения для
  // вызывающего, обновляются бэкендом поверх дефолтов кода.
  const template = role.template;
  const access = template?.access ?? 'full';
  const tools = template?.tools ?? null;
  const toolsText = tools === null
    ? 'Все'
    : (tools.length === 0
      ? 'Только чат'
      : tools.map(t => TOOL_LABEL[t] ?? t).join(' · '));

  // Тройка моделей по уровням — из слоя настроек. Пустая ячейка означает
  // «наследуется сверху» (правило роли → «Модели по умолчанию»).
  const rec = layerSettings?.specialties?.[roleKey] ?? null;
  const triple: [string | null, string | null, string | null] = rec
    ? [rec.tierStrong ?? null, rec.tierMedium ?? null, rec.tierWeak ?? null]
    : [null, null, null];
  const hasAnyRule = triple.some(v => !!v);

  // Пресеты (цепочки моделей по уровням) — глобальный список из слоя.
  const presets = layerSettings?.presets ?? [];

  // Дефолтные привязки роли (типовые умения) — из каталога секций промптов.
  const defaultBindings = promptSectionsCatalog?.specialties?.[roleKey]?.defaultBindings ?? [];

  // Подпись заголовка тулбара — у роли нет «имени под именем» как у персоны
  // (handle/handle), но каталог может нести короткое пояснение; показываем
  // первую строку описания, иначе — подпись по умолчанию.
  const subtitle = roleDescription
    ? (roleDescription.split('\n')[0]?.trim() || '')
    : '';

  // === Настройки: доступ · инструменты · модели по уровням ===
  const settingsSection = (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 12 }}>Настройки</SectionLabel>
      <div style={{
        display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: 10,
      }}>
        <div style={factChip}>
          <span style={{
            fontSize: FS.xs, fontWeight: 600, letterSpacing: '0.04em',
            textTransform: 'uppercase', color: C.textMuted,
          }}>Доступ</span>
          <span style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
          }}>{ACCESS_LABEL[access]}</span>
        </div>
        <div style={factChip}>
          <span style={{
            fontSize: FS.xs, fontWeight: 600, letterSpacing: '0.04em',
            textTransform: 'uppercase', color: C.textMuted,
          }}>Инструменты</span>
          <span style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
          }}>{toolsText}</span>
        </div>
      </div>
      <div style={{
        display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))',
        gap: 10, marginTop: 10,
      }}>
        {([
          { tier: 'Сильная', value: triple[0] },
          { tier: 'Средняя', value: triple[1] },
          { tier: 'Слабая', value: triple[2] },
        ] as const).map(({ tier, value }) => (
          <div key={tier} style={factChip}>
            <span style={{
              fontSize: FS.xs, fontWeight: 600, letterSpacing: '0.04em',
              textTransform: 'uppercase', color: C.textMuted,
            }}>Модель · {tier}</span>
            <span style={{
              fontSize: FS.base, fontWeight: 600,
              color: value ? C.textHeading : C.textMuted,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }} title={value ? tierLabel(value, presets) : 'Наследуется'}>
              {value ? tierLabel(value, presets) : 'Как «Модели по умолчанию»'}
            </span>
          </div>
        ))}
      </div>
      {!hasAnyRule && (
        <div style={{
          fontSize: FS.xs, color: C.textMuted, marginTop: 10, lineHeight: 1.5,
        }}>
          Правил нет — персоны роли работают по «Моделям по умолчанию».
        </div>
      )}
    </div>
  );

  // === Секции промпта (RolePresetsBlock в режиме view) ===
  // Заголовок «Секции промпта» рисует родитель через SectionLabel — блок
  // внутри показывает только карточки секций.
  const presetsSection = (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 12 }}>Секции промпта</SectionLabel>
      <RolePresetsBlock
        roleKey={roleKey}
        catalog={promptSectionsCatalog}
        editLayer={null}
        globalLayer={layerSettings}
        userLayer={null}
        mode="view"
        showTitle={false}
      />
    </div>
  );

  // === Умения (типовой профиль привязок) — раздел «Умения» ===
  // Карточки типовых умений роли — RoleBindingsBlock в режиме view (по образцу
  // вкладки «Умения» персоны): заголовок с счётчиком блок рисует сам.
  const skillsSection = (
    <div style={section}>
      <RoleBindingsBlock
        roleKey={roleKey}
        bindings={defaultBindings}
        mode="view"
        accent={accent}
      />
    </div>
  );

  // === Колонка-визитка (левая на десктопе, на мобиле во всю ширину) ===
  // Состав — по активному разделу: «Общая информация» (настройки + секции
  // промпта) или «Умения» (типовой профиль привязок).
  const mainColumn = (
    <div style={{
      flex: '1 1 380px', minWidth: 0,
      display: 'flex', flexDirection: 'column', gap: 24,
    }}>
      {tab === 'general' ? (
        <>
          {settingsSection}
          {presetsSection}
        </>
      ) : (
        skillsSection
      )}
    </div>
  );

  // === Правая колонка: список персон роли ===
  const peopleColumn = (
    <aside style={{ flex: '1 1 300px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: 10 }}>
      <div style={{
        display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10,
      }}>
        <SectionLabel>Кто работает по этой роли</SectionLabel>
        <span style={{
          fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0,
        }}>
          {personas.length === 0
            ? 'пусто'
            : `${personas.length} ${pluralPersonas(personas.length)}`}
        </span>
      </div>
      <RolePeopleSlice
        roleKey={roleKey}
        personas={personas}
        catalog={promptSectionsCatalog}
        onPersonaUpdated={onPersonaUpdated}
      />
    </aside>
  );

  return (
    // Прокрутка живёт у родителя (PersonasSpecialties): двойные скроллеры съедали
    // место на 360 CSS и резали PillSwitch доступа. Шапка и акцентная полоса идут
    // во всю ширину центра — как у персоны (PersonaStudio), где тулбар не сидит
    // внутри центрированного полотна; полотно центрируется уже под ними.
    <div>
      {/* Шапка-тулбар визитки — единый Toolbar из кита с полосой цвета роли
          слева (как у PersonaToolbar). Заголовок раздела — тулбар, а не hero. */}
      <Toolbar
        isMobile={isMobile}
        noBorder
        bg="transparent"
        style={{ borderLeft: `3px solid ${accent}` }}
      >
        <ToolbarIconButton onClick={onBack} title="Назад" isMobile={isMobile}>
          <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
        </ToolbarIconButton>
        <RoleIcon catalog={role} roleKey={roleKey} size={isMobile ? 32 : 40} />
        {/* На мобиле имя роли и подпись уже есть в шапке экрана (PersonasPage);
            в тулбаре их рисовать не надо — текст сжимается до одной буквы
            и дублирует шапку. */}
        {!isMobile && (
          <div style={{
            flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1,
          }}>
            <div style={{
              fontFamily: FONT.serif, fontSize: 28, fontWeight: 500,
              color: accent, letterSpacing: '-0.01em', lineHeight: 1.2,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {roleName}
            </div>
            {subtitle && (
              <div style={{
                fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>
                {subtitle}
              </div>
            )}
          </div>
        )}
        {/* Сегмент разделов «Общая информация | Умения» — по образцу вкладок
            персоны (PersonaToolbar). На десктопе живёт в строке тулбара между
            именем роли и кнопкой «Редактировать»; на мобиле уходит своей строкой
            во всю ширину (Toolbar переносит детей) и сжимается до иконок —
            компактно уже на 360 CSS. */}
        <div style={{
          display: 'flex', minWidth: 0,
          flex: isMobile ? '1 0 100%' : '0 0 auto',
          overflowX: 'auto',
        }}>
          <PillSwitch<RoleTab>
            value={tab}
            onChange={setTab}
            options={TAB_OPTIONS}
            compact={isMobile}
            isMobile={isMobile}
          />
        </div>
        {isAdmin && onEdit && (
          <button
            type="button"
            onClick={onEdit}
            title="Редактировать"
            style={{
              ...tbBtnGhost,
              display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0,
            }}
          >
            <Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            Редактировать
          </button>
        )}
      </Toolbar>

      {/* Тонкая акцентная полоса роли — разделитель шапки и контента, как у
          персоны (PersonaStudio). Внутри Toolbar полоса уже есть слева, но
          между тулбаром и контентом нужна отдельная черта на всю ширину. */}
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />

      {/* Полотно контента — центрированное, с горизонтальными полями. */}
      <div style={{
        maxWidth: isMobile ? 680 : 1020, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '20px 16px 32px' : '26px 32px 40px',
      }}>
        {/* Описание роли: на десктопе его несёт подпись тулбара, на мобиле
            тулбар текста не рисует — там показываем строку описания здесь. */}
        {isMobile && roleDescription?.trim() && (
          <div style={{
            fontSize: FS.base, color: C.textSecondary, fontFamily: FONT.sans,
            lineHeight: 1.5, marginBottom: 22,
          }}>{roleDescription}</div>
        )}

        {/* Контент — две колонки, переносятся сами, когда не помещаются. */}
        <div style={{
          display: 'flex', gap: 28, alignItems: 'flex-start', flexWrap: 'wrap',
        }}>
          {mainColumn}
          {peopleColumn}
        </div>
      </div>
    </div>
  );
}

// Склонение «персон/персоны/персоны» для счётчика строк в правой колонке.
function pluralPersonas(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'персона';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'персоны';
  return 'персон';
}

// Кнопка «Назад» в пустом состоянии (роль не найдена).
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