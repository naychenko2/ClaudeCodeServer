import { useState } from 'react';
import { Book, CheckSquare, ChevronLeft, EllipsisVertical, Layers, Pencil, Trash2, User, X, Zap } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Menu, MenuItem, IconButton } from '../../components/ui';
import { Toolbar, PillSwitch, tbBtnPrimary, tbBtnGhost } from '../../components/Toolbar';
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
}

interface CreateProps extends CommonProps {
  mode: 'create';
  onCancel: () => void;
}

export function PersonaToolbar(props: EditProps | CreateProps) {
  const { accent, status, onSave, onBack, isMobile, hero, onClose } = props;
  const creating = props.mode === 'create';
  const viewOptions = VIEW_OPTIONS;

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

  const backBtn = onBack && (
    <IconButton onClick={onBack} title="Назад" size={isMobile ? 'lg' : 'md'}>
      <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
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
      <Toolbar isMobile={isMobile} noBorder={hero} bg={hero ? 'transparent' : undefined} style={rowOverride}>
        {backBtn}
        <div style={{ flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
          Новая персона
        </div>
        <button onClick={props.onCancel} style={tbBtnGhost}>Отмена</button>
        {saveArea}
      </Toolbar>
    );
  }

  const { persona, zoneLabel, view, onView, editing, onEdit, onCancelEdit, onDelete } = props;
  const lines = personaTitleLines(persona);

  return (
    <Toolbar isMobile={isMobile} noBorder={hero} bg={hero ? 'transparent' : undefined} style={rowOverride}>
      {backBtn}
      <PersonaAvatar persona={persona} size={hero ? 40 : 32} />

      {/* Идентичность: роль (serif, цвет персоны) + имя + бейдж зоны */}
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
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
          <span style={zoneBadge(accent)}>{zoneLabel}</span>
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
              <button onClick={onEdit} title="Редактировать"
                style={{ ...tbBtnGhost, display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
                <Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                Редактировать
              </button>
              <div style={{ position: 'relative', flexShrink: 0 }}>
                <IconButton onClick={() => setMenuOpen(o => !o)} title="Ещё" size="md">
                  <EllipsisVertical size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                </IconButton>
                {menuOpen && (
                  <Menu onClose={() => setMenuOpen(false)} align="right" top={38} minWidth={180}>
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

          {/* Сегмент Профиль | [Умения] | Память | Задачи (на мобиле — компактный, иконки) */}
          <PillSwitch<PersonaView>
            value={view}
            onChange={onView}
            options={viewOptions}
            compact={isMobile}
            isMobile={isMobile}
          />
        </>
      )}
      {/* Крестик закрытия (студия в центре воркспейса). В режиме правки скрыт —
          выход только через Отмена/Сохранить, чтобы не потерять несохранённое */}
      {!editing && onClose && (
        <IconButton onClick={onClose} title="Закрыть" size="md">
          <X size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        </IconButton>
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


