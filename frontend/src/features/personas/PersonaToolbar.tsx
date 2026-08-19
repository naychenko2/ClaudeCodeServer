import { useState } from 'react';
import { Book, CheckSquare, ChevronLeft, EllipsisVertical, Layers, Pencil, Star, Trash2, User, X, Zap } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Menu, MenuItem, IconButton } from '../../components/ui';
import { Toolbar, PillSwitch, tbBtnPrimary, tbBtnGhost } from '../../components/Toolbar';
import { useContainerWidth } from '../../hooks/useContainerWidth';
import { PersonaAvatar } from './PersonaAvatar';
import { personaTitleLines } from '../../lib/personas';
import type { PersonaFormStatus } from './PersonaForm';

// Единый тулбар студии персоны — общий для глобальной студии (PersonasPage) и
// проектной панели (ProjectPersonaPane). Состав в режиме просмотра/редактирования:
// [полоса цвета] аватар + Роль(Имя) + бейдж зоны | сегмент Профиль|Умения|Память|Задачи
// (Умения — за флагом persona-bindings) | Поговорить | в «Профиле» — Редактировать
// + ⋯-меню (Удалить внутри). Во время редактирования профиля вкладки/Поговорить/меню
// скрыты, справа — [Отмена] и Сохранить (+точка dirty).
// В режиме создания: «Новая персона» + [Отмена] [Создать].

export type PersonaView = 'preview' | 'knowledge' | 'memory' | 'tasks' | 'automation';

// Порог узкого тулбара — по ширине КОНТЕЙНЕРА (боковые панели режут место, окно об
// этом не знает). Ниже: «Редактировать» теряет подпись, вкладки уходят своей строкой
// во всю ширину со скроллом, бейджи зоны и «по умолчанию» сокращаются.
const TOOLBAR_NARROW = 900;
// Второй, более низкий порог: бейдж зоны сокращается до первого слова, «по умолчанию»
// схлопывается в звезду. В макете на 832 бейджи ещё полные, на 560 — уже сокращены.
const TOOLBAR_TIGHT = 700;

// Иконки видов — на мобиле пилюли компактные (подпись только у активного)
const VIEW_OPTIONS: { value: PersonaView; label: string; icon: React.ReactNode }[] = [
  // Профиль — визитка персоны (человек): просмотр по умолчанию, правка по кнопке
  { value: 'preview', label: 'Профиль', icon: <User size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
  // Умения — книга (фича persona-bindings): источники знаний, инструменты и правила
  { value: 'knowledge', label: 'Умения', icon: <Book size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
  // Проактивность — молния (правила «событие → действие»)
  { value: 'automation', label: 'Проактивность', icon: <Zap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
  // Память — слои
  { value: 'memory', label: 'Память', icon: <Layers size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
  // Задачи — чек-лист (поручения персоне-исполнителю)
  { value: 'tasks', label: 'Задачи', icon: <CheckSquare size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} /> },
];

interface CommonProps {
  accent: string;
  status: PersonaFormStatus;
  onSave: () => void;
  onBack?: () => void;
  isMobile?: boolean;
  // Стиль Islands (глобальная студия, десктоп): тулбар — заголовок раздела прямо
  // на холсте (без фона и нижней границы), контент студии ниже — карточка-остров
  hero?: boolean;
  // Крестик закрытия справа (студия в ЦЕНТРЕ воркспейса — возврат к чату);
  // взаимоисключим по смыслу с левой стрелкой onBack
  onClose?: () => void;
}

interface EditProps extends CommonProps {
  mode: 'edit';
  persona: Persona;
  zoneLabel: string;
  view: PersonaView;
  onView: (v: PersonaView) => void;
  // Идёт ли редактирование профиля (форма развёрнута вместо визитки)
  editing: boolean;
  onEdit: () => void;
  onCancelEdit: () => void;
  talking?: boolean;
  onTalk: () => void;
  onDelete: () => void;
  // Дефолт-персона (фича default-personas-onboarding): признак «эта персона — дефолт
  // своей зоны» и действие назначения; onMakeDefault не задан — пункт меню не рисуется
  isDefault?: boolean;
  onMakeDefault?: () => void;
}

interface CreateProps extends CommonProps {
  mode: 'create';
  onCancel: () => void;
}

export function PersonaToolbar(props: EditProps | CreateProps) {
  const { accent, status, onSave, onBack, isMobile, hero, onClose } = props;
  const creating = props.mode === 'create';
  const viewOptions = VIEW_OPTIONS;

  // Раскладка тулбара — по ширине его собственного контейнера: в центре воркспейса
  // с раскрытыми панелями окно широкое, а места нет (именно отсюда весь дефект).
  // До первого замера (width === null) — широкая раскладка, как на десктопе.
  const [rootRef, width] = useContainerWidth<HTMLDivElement>();
  const narrow = width !== null && width < TOOLBAR_NARROW;
  const tight = width !== null && width < TOOLBAR_TIGHT;

  // Текст и доступность кнопки сохранения зависят от режима
  const saveLabel = status.saving
    ? (creating ? 'Создаю…' : 'Сохраняю…')
    : (creating ? 'Создать' : 'Сохранить');
  const saveDisabled = creating
    ? (!status.canSave || status.saving)
    : (!status.canSave || status.saving || !status.dirty);

  const [menuOpen, setMenuOpen] = useState(false);

  // Полоса цвета персоны слева — допустимая персонализация поверх общего Toolbar
  const rowOverride: React.CSSProperties = {
    borderLeft: `3px solid ${accent}`, position: 'relative',
  };

  // Выход из карточки — ПЕРВЫМ элементом слева и при любой ширине: тем, чем закрывают
  // экран, нельзя стоять в очереди на вылет за правый край. Стрелка — возврат к списку,
  // крестик — закрытие студии в центре воркспейса; смысл один, рисуем один элемент.
  const exit = onBack ?? onClose;
  const exitBtn = exit && (
    <IconButton onClick={exit} title={onBack ? 'Назад' : 'Закрыть'} size={isMobile ? 'lg' : 'md'}>
      {onBack
        ? <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        : <X size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
    </IconButton>
  );

  const saveArea = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
      {/* Индикатор несохранённых правок */}
      {!creating && status.dirty && !status.saving && (
        <span title="Есть несохранённые изменения"
          style={{ width: 7, height: 7, borderRadius: R.full, background: accent, flexShrink: 0 }} />
      )}
      <button onClick={onSave} disabled={saveDisabled}
        style={{ ...tbBtnPrimary, opacity: saveDisabled ? 0.55 : 1, cursor: saveDisabled ? 'default' : 'pointer' }}>
        {saveLabel}
      </button>
    </div>
  );

  if (creating) {
    return (
      <Toolbar rootRef={rootRef} isMobile={isMobile} noBorder={hero} bg={hero ? 'transparent' : undefined} style={rowOverride}>
        {exitBtn}
        <div style={{ flex: 1, minWidth: 140, fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          Новая персона
        </div>
        <button onClick={props.onCancel} style={tbBtnGhost}>Отмена</button>
        {saveArea}
      </Toolbar>
    );
  }

  const { persona, zoneLabel, view, onView, editing, onEdit, onCancelEdit, onDelete, isDefault, onMakeDefault } = props;
  const lines = personaTitleLines(persona);

  return (
    <Toolbar rootRef={rootRef} isMobile={isMobile} noBorder={hero} bg={hero ? 'transparent' : undefined} style={rowOverride}>
      {/* В режиме правки крестик закрытия по-прежнему скрыт (выход — Отмена/Сохранить,
          чтобы не потерять несохранённое), а стрелка «Назад» остаётся, как и раньше */}
      {(!editing || !!onBack) && exitBtn}
      <PersonaAvatar persona={persona} size={hero ? 40 : 32} />

      {/* Идентичность: роль (serif, цвет персоны) + имя + бейдж зоны.
          Пол minWidth 140 — блок обрезается многоточием, но не схлопывается в ноль
          (иначе вторая строка ложилась под кнопку «Редактировать») */}
      <div style={{ flex: 1, minWidth: 140, display: 'flex', flexDirection: 'column', gap: 1 }}>
        {/* Hero: размер заголовка — как у раздела «Календарь» (serif 28 / 500) */}
        <div style={{ fontFamily: FONT.serif, fontSize: hero ? 28 : 15, fontWeight: hero ? 500 : 600, color: accent, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {lines.primary}
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
          {lines.secondary && (
            <span style={{ fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 160 }}>
              {lines.secondary}
            </span>
          )}
          {/* В самом узком контейнере бейдж зоны сокращается до первого слова
              («Проект · Здоровье» → «Проект»), полный текст остаётся в подсказке */}
          <span title={zoneLabel} style={zoneBadge(accent)}>
            {tight ? zoneLabel.split(' · ')[0] : zoneLabel}
          </span>
          {/* Бейдж дефолт-персоны зоны (фича default-personas-onboarding) */}
          {isDefault && (
            <span title="Персона по умолчанию для новых чатов этой зоны" style={{
              display: 'inline-flex', alignItems: 'center', gap: 3, fontSize: 10.5, fontWeight: 600,
              padding: tight ? '1px 5px' : '1px 7px', borderRadius: R.pill, background: C.accentLight, color: C.accent,
              whiteSpace: 'nowrap', flexShrink: 0,
            }}>
              <Star size={10} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
              {!tight && 'по умолчанию'}
            </span>
          )}
        </div>
      </div>

      {editing ? (
        // Режим правки профиля: вкладки/Поговорить/меню скрыты (чтобы не потерять
        // несохранённое переключением), справа — Отмена + Сохранить
        <>
          <button onClick={onCancelEdit} style={tbBtnGhost}>Отмена</button>
          {saveArea}
        </>
      ) : (
        <>
          {/* Редактировать + ⋯-меню (Удалить) — только десктоп. На мобиле «Редактировать»
              вынесена в плавающую кнопку PersonaEditFab, «Удалить» — в «Опасную зону» формы. */}
          {!isMobile && (
            <>
              {/* Первый шаг деградации: в узком контейнере кнопка теряет подпись */}
              {narrow ? (
                <IconButton onClick={onEdit} title="Редактировать" size="md">
                  <Pencil size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                </IconButton>
              ) : (
                <button onClick={onEdit} title="Редактировать"
                  style={{ ...tbBtnGhost, display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
                  <Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  Редактировать
                </button>
              )}
              <div style={{ position: 'relative', flexShrink: 0 }}>
                <IconButton onClick={() => setMenuOpen(o => !o)} title="Ещё" size="md">
                  <EllipsisVertical size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                </IconButton>
                {menuOpen && (
                  <Menu onClose={() => setMenuOpen(false)} align="right" top={38} minWidth={220}>
                    {/* Назначение дефолт-персоны зоны (фича default-personas-onboarding) */}
                    {onMakeDefault && !isDefault && (
                      <MenuItem
                        icon={<Star size={15} strokeWidth={ICON_STROKE} />}
                        label="Сделать персоной по умолчанию"
                        onClick={() => { setMenuOpen(false); onMakeDefault(); }}
                      />
                    )}
                    <MenuItem
                      danger
                      icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />}
                      label="Удалить персону"
                      onClick={() => { setMenuOpen(false); onDelete(); }}
                    />
                  </Menu>
                )}
              </div>
            </>
          )}

          {/* Сегмент Профиль | [Умения] | Память | Задачи (на мобиле — компактный, иконки).
              Второй шаг деградации: в узком контейнере вкладки уходят своей строкой во всю
              ширину и прокручиваются по горизонтали — доступны все пять, ничего не срезано */}
          <div style={{
            display: 'flex', minWidth: 0,
            flex: narrow || isMobile ? '1 0 100%' : '0 0 auto',
            overflowX: 'auto',
          }}>
            <PillSwitch<PersonaView>
              value={view}
              onChange={onView}
              options={viewOptions}
              compact={isMobile}
              isMobile={isMobile}
            />
          </div>
        </>
      )}
    </Toolbar>
  );
}

// Бейдж зоны персоны — тонирован акцентом персоны
function zoneBadge(accent: string): React.CSSProperties {
  return {
    display: 'inline-block', fontSize: 10.5, fontWeight: 600, letterSpacing: '0.02em',
    padding: '1px 7px', borderRadius: R.pill, width: 'fit-content', flexShrink: 0,
    background: `${accent}1F`, color: accent, whiteSpace: 'nowrap',
  };
}


