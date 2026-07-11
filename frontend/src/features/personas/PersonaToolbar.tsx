import { useState } from 'react';
import type { Persona } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Menu, MenuItem, IconButton } from '../../components/ui';
import { Toolbar, PillSwitch, tbBtnPrimary, tbBtnGhost } from '../../components/Toolbar';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { PersonaAvatar } from './PersonaAvatar';
import { personaTitleLines } from '../../lib/personas';
import type { PersonaFormStatus } from './PersonaForm';

// Единый тулбар студии персоны — общий для глобальной студии (PersonasPage) и
// проектной панели (ProjectPersonaPane). Состав в режиме просмотра/редактирования:
// [полоса цвета] аватар + Роль(Имя) + бейдж зоны | сегмент Обзор|Профиль|Знания|Память
// (Знания — за флагом persona-bindings) | Поговорить | ⋯-меню (Удалить внутри) и
// Сохранить (+точка dirty) — только в «Профиль».
// В режиме создания: «Новая персона» + [Отмена] [Создать].

export type PersonaView = 'preview' | 'profile' | 'knowledge' | 'memory' | 'tasks';

// Иконки видов — на мобиле пилюли компактные (подпись только у активного)
const viewIcon = (d: React.ReactNode) => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    {d}
  </svg>
);
const VIEW_OPTIONS: { value: PersonaView; label: string; icon: React.ReactNode }[] = [
  // Обзор — глаз
  { value: 'preview', label: 'Обзор', icon: viewIcon(<><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" /><circle cx="12" cy="12" r="3" /></>) },
  // Профиль — карандаш
  { value: 'profile', label: 'Профиль', icon: viewIcon(<><path d="M12 20h9" /><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z" /></>) },
  // Знания — книга (фича persona-bindings)
  { value: 'knowledge', label: 'Знания', icon: viewIcon(<><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" /><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" /></>) },
  // Память — слои
  { value: 'memory', label: 'Память', icon: viewIcon(<><path d="M12 2 2 7l10 5 10-5-10-5z" /><path d="M2 17l10 5 10-5" /><path d="M2 12l10 5 10-5" /></>) },
  // Задачи — чек-лист (поручения персоне-исполнителю)
  { value: 'tasks', label: 'Задачи', icon: viewIcon(<><path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" /></>) },
];

interface CommonProps {
  accent: string;
  status: PersonaFormStatus;
  onSave: () => void;
  onBack?: () => void;
  isMobile?: boolean;
}

interface EditProps extends CommonProps {
  mode: 'edit';
  persona: Persona;
  zoneLabel: string;
  view: PersonaView;
  onView: (v: PersonaView) => void;
  talking?: boolean;
  onTalk: () => void;
  onDelete: () => void;
}

interface CreateProps extends CommonProps {
  mode: 'create';
  onCancel: () => void;
}

export function PersonaToolbar(props: EditProps | CreateProps) {
  const { accent, status, onSave, onBack, isMobile } = props;
  const creating = props.mode === 'create';
  // Вкладка «Знания» — только при включённом флаге привязок
  const bindingsEnabled = useFeature(FLAGS.personaBindings);
  const viewOptions = bindingsEnabled ? VIEW_OPTIONS : VIEW_OPTIONS.filter(o => o.value !== 'knowledge');

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
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M15 18l-6-6 6-6" />
      </svg>
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
      <Toolbar isMobile={isMobile} style={rowOverride}>
        {backBtn}
        <div style={{ flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
          Новая персона
        </div>
        <button onClick={props.onCancel} style={tbBtnGhost}>Отмена</button>
        {saveArea}
      </Toolbar>
    );
  }

  const { persona, zoneLabel, view, onView, talking, onTalk, onDelete } = props;
  const lines = personaTitleLines(persona);

  return (
    <Toolbar isMobile={isMobile} style={rowOverride}>
      {backBtn}
      <PersonaAvatar persona={persona} size={32} />

      {/* Идентичность: роль (serif, цвет персоны) + имя + бейдж зоны */}
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
        <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: accent, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
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

      {/* Сегмент Обзор | Профиль | [Знания] | Память (на мобиле — компактный, иконки) */}
      <PillSwitch<PersonaView>
        value={view}
        onChange={onView}
        options={viewOptions}
        compact={isMobile}
        isMobile={isMobile}
      />

      {/* Поговорить — доступно в обоих видах */}
      <button onClick={onTalk} disabled={talking} title="Поговорить"
        style={{ ...talkBtn(accent), opacity: talking ? 0.6 : 1, cursor: talking ? 'default' : 'pointer' }}>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
        </svg>
        {!isMobile && (talking ? 'Создаём…' : 'Поговорить')}
      </button>

      {/* Действия профиля — только в виде «Профиль» */}
      {view === 'profile' && (
        <>
          <div style={{ position: 'relative', flexShrink: 0 }}>
            <IconButton onClick={() => setMenuOpen(o => !o)} title="Ещё" size={isMobile ? 'lg' : 'md'}>
              <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                <circle cx="12" cy="5" r="1.6" /><circle cx="12" cy="12" r="1.6" /><circle cx="12" cy="19" r="1.6" />
              </svg>
            </IconButton>
            {menuOpen && (
              <Menu onClose={() => setMenuOpen(false)} align="right" top={38} minWidth={180}>
                <MenuItem
                  danger
                  icon={<><path d="M3 6h18" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></>}
                  label="Удалить персону"
                  onClick={() => { setMenuOpen(false); onDelete(); }}
                />
              </Menu>
            )}
          </div>
          {saveArea}
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

// Кнопка «Поговорить» — как tbBtnPrimary, но залита акцентом персоны
function talkBtn(accent: string): React.CSSProperties {
  return {
    ...tbBtnPrimary,
    display: 'inline-flex', gap: 7,
    background: accent, color: C.onAccent,
  };
}
