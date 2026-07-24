import { useState, useEffect, useLayoutEffect, useRef } from 'react';
import { Check, ChevronDown, MessageCircle, Users } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import type { Persona, AgentInfo } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { personaLabel } from '../lib/personas';
import { modelProvider } from '../lib/models';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { agentDotColor } from './AgentSelector';

// Результат выбора: ровно одно из полей задано (либо оба null — «без собеседника»)
export interface CompanionSelection {
  persona?: Persona | null;
  agent?: AgentInfo | null;
}

interface Props {
  personas: Persona[];
  // .md-агенты Claude проекта (вне проекта — пустой список, группа скрыта)
  agents: AgentInfo[];
  selectedPersona: Persona | null;
  // Имя выбранного .md-агента (fileName без .md) — источник Session.agentName
  selectedAgentName: string | null;
  onSelect: (sel: CompanionSelection) => void;
  isMobile?: boolean;
  // Направление раскрытия: по умолчанию вверх (composer прижат к низу).
  // dropUp=false — раскрытие вниз (для триггера у верха панели, напр. список чатов).
  dropUp?: boolean;
  // Групповой чат (флаг persona-group-chats): мультивыбор 2-8 персон,
  // первая выбранная — ведущая; подтверждение создаёт новый групповой чат.
  onCreateGroup?: (personaIds: string[]) => void;
  // Просторный контекст (нижняя полоса разнесённого композера): ослабляем обрезку
  // длинного «Роль (Имя)» — места там достаточно
  wide?: boolean;
  // Схлопнуть триггер до квадрата с аватаром/иконкой (узкая полоса контролов):
  // подпись и шеврон убираются, «Роль (Имя)» остаётся в тултипе
  compact?: boolean;
}

// Единый селектор «собеседника» чата: персоны (наша фича) и стандартные .md-агенты
// Claude в одном дропдауне. Заменяет пару PersonaSelector + AgentSelector в композере.
// Раскрытие/мобильное позиционирование — по образцу PersonaSelector.
export function CompanionSelector({ personas, agents, selectedPersona, selectedAgentName, onSelect, isMobile, dropUp = true, onCreateGroup, wide, compact }: Props) {
  const [open, setOpen] = useState(false);
  // Режим мультивыбора участников группового чата (внутри того же дропдауна)
  const [groupMode, setGroupMode] = useState(false);
  // Порядок выбора важен: первая выбранная — ведущая
  const [groupSelected, setGroupSelected] = useState<string[]>([]);
  const rootRef = useRef<HTMLDivElement>(null);
  // На мобиле dropdown позиционируется через fixed с вычисленными координатами
  const [fixedPos, setFixedPos] = useState<{ bottom: number; top: number; left: number; right: number } | null>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  // Закрытие дропдауна сбрасывает мультивыбор группы
  useEffect(() => {
    if (!open) { setGroupMode(false); setGroupSelected([]); }
  }, [open]);

  useLayoutEffect(() => {
    if (!open || !isMobile || !rootRef.current) return;
    const rect = rootRef.current.getBoundingClientRect();
    // Вверх — от верха триггера к верху экрана; вниз — от низа триггера к низу экрана
    setFixedPos(dropUp
      ? { bottom: window.innerHeight - rect.top + 6, top: 16, left: 16, right: 16 }
      : { bottom: 16, top: rect.bottom + 6, left: 16, right: 16 });
  }, [open, isMobile, dropUp]);

  // Файлы-агенты, сгенерированные из персон (PersonaAgentFileSync пишет {handle}.md),
  // в списке не дублируем — персона уже показана в своей группе (фильтр по ВСЕМ
  // персонам, включая пантеонные: их файлы тоже не должны всплывать как агенты)
  const personaHandles = new Set(personas.map(p => p.handle));
  const plainAgents = agents.filter(a => !personaHandles.has(a.fileName));

  // Подгруппы персон в порядке отображения: проектные → обычные глобальные.
  // Материализованный пантеон OmO (scope=global + templateKey) в выбор собеседника
  // не включаем — как и виртуальные роли; подключение только через раздел «Персоны».
  const projectPersonas = personas.filter(p => p.scope === 'project');
  const regularGlobals = personas.filter(p => p.scope === 'global' && !p.templateKey);
  const selectablePersonas = [...projectPersonas, ...regularGlobals];

  if (selectablePersonas.length === 0 && plainAgents.length === 0) return null;

  // Пункт-чекбокс персоны в групповом мультивыборе (общий для всех подгрупп)
  const groupCheckboxItem = (p: Persona) => {
    const idx = groupSelected.indexOf(p.id);
    const checked = idx >= 0;
    const disabled = !checked && groupSelected.length >= 8;
    return (
      <button
        key={`g-${p.id}`}
        onClick={() => setGroupSelected(prev => checked
          ? prev.filter(x => x !== p.id)
          : prev.length >= 8 ? prev : [...prev, p.id])}
        disabled={disabled}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '7px 10px',
          borderRadius: R.md, border: 'none', background: checked ? C.accentLight : 'transparent',
          cursor: disabled ? 'default' : 'pointer', textAlign: 'left', opacity: disabled ? 0.5 : 1,
        }}
      >
        <input type="checkbox" readOnly checked={checked} style={{ accentColor: C.accent, pointerEvents: 'none' }} />
        <PersonaAvatar persona={p} size={26} />
        <span style={{ flex: 1, minWidth: 0, fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: FONT.sans }}>
          {personaLabel(p)}
        </span>
        {idx === 0 && (
          <span style={{
            flexShrink: 0, fontSize: 10, fontWeight: 700, padding: '1px 7px',
            borderRadius: R.pill, background: C.accentLight, color: C.accent, fontFamily: FONT.sans,
          }}>
            ведущий
          </span>
        )}
      </button>
    );
  };

  // Выбранный .md-агент: резолвим по имени; если агент из списка пропал (файл удалили),
  // показываем имя как есть с нейтральной точкой
  const selectedAgent = selectedAgentName
    ? agents.find(a => a.fileName === selectedAgentName) ?? null
    : null;
  const agentDisplayName = selectedAgent?.name ?? selectedAgentName ?? '';
  const hasSelection = !!selectedPersona || !!selectedAgentName;

  const dropdownStyle: React.CSSProperties = isMobile && fixedPos
    ? {
        position: 'fixed',
        bottom: fixedPos.bottom, top: fixedPos.top, left: fixedPos.left, right: fixedPos.right,
        overflowY: 'auto', background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.dropdown, padding: 4, zIndex: Z.dropdown,
      }
    : {
        position: 'absolute', right: 0,
        ...(dropUp ? { bottom: 'calc(100% + 6px)' } : { top: 'calc(100% + 6px)' }),
        minWidth: 320, maxWidth: 'calc(100vw - 32px)', maxHeight: 'min(70vh, 480px)',
        overflowY: 'auto', background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.dropdown, padding: 4, zIndex: Z.dropdown,
      };

  // Заголовок группы/подгруппы в списке
  const groupHeader = (text: string) => (
    <div key={`h-${text}`} style={{
      padding: '7px 10px 3px', fontSize: 10.5, fontWeight: 700, color: C.textMuted,
      textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
    }}>{text}</div>
  );

  const personaItem = (persona: Persona) => {
    const active = selectedPersona?.id === persona.id;
    // Краткое описание второй строкой — как у .md-агентов
    const desc = (persona.description ?? '').trim();
    return (
      <button
        key={`p-${persona.id}`}
        onClick={() => { onSelect({ persona, agent: null }); setOpen(false); }}
        onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
        onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px',
          borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
          cursor: 'pointer', textAlign: 'left',
        }}
      >
        <PersonaAvatar persona={persona} size={28} />
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {personaLabel(persona)}
          </span>
          {desc && (
            <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35, overflow: 'hidden' }}>
              {desc.length > 80 ? desc.slice(0, 80) + '…' : desc}
            </span>
          )}
        </span>
        {active && (
          <Check size={15} strokeWidth={2.5} stroke={C.accent} style={{ flexShrink: 0 }} />
        )}
      </button>
    );
  };

  const agentItem = (agent: AgentInfo) => {
    const active = selectedAgentName === agent.fileName;
    const dot = agentDotColor(agent.color);
    return (
      <button
        key={`a-${agent.fileName}`}
        onClick={() => { onSelect({ persona: null, agent }); setOpen(false); }}
        onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
        onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
        style={{
          width: '100%', display: 'flex', alignItems: 'flex-start', gap: 8, padding: '8px 10px',
          borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
          cursor: 'pointer', textAlign: 'left',
        }}
      >
        <span style={{ width: 8, height: 8, borderRadius: '50%', background: dot, flexShrink: 0, marginTop: 5 }} />
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
            {agent.name}
          </span>
          {agent.description && (
            <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35, overflow: 'hidden' }}>
              {agent.description.length > 80 ? agent.description.slice(0, 80) + '…' : agent.description}
            </span>
          )}
        </span>
        {active && (
          <Check size={15} strokeWidth={2.5} stroke={C.accent} style={{ flexShrink: 0, marginTop: 2 }} />
        )}
      </button>
    );
  };

  // Схлопнутый триггер — аватар/иконка + шеврон без подписи (шеврон отличает список
  // выбора от кнопки-действия, поэтому остаётся и в узкой полосе)
  const compactStyle: React.CSSProperties = {
    height: isMobile ? 36 : 32, padding: '0 6px',
    borderRadius: R.md, border: 'none',
    background: open ? C.bgSelected : 'transparent', color: C.textSecondary,
    cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
    gap: 3, flexShrink: 0, transition: 'background 0.15s',
  };
  // Шеврон схлопнутого вида — общий для всех трёх веток триггера
  const compactChevron = (
    <ChevronDown size={10} strokeWidth={ICON_STROKE}
      style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
  );
  // Подпись собеседника уходит в тултип, когда плашка схлопнута
  const compactTitle = selectedPersona
    ? `Собеседник: ${personaLabel(selectedPersona)}`
    : selectedAgentName ? `Собеседник: ${agentDisplayName}` : 'Выбрать собеседника';

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, minWidth: 0 }}>
      {selectedPersona ? (
        // Выбрана персона — плашка с мини-аватаром и «Роль (Имя)»
        <button
          onClick={() => setOpen(o => !o)}
          title={compactTitle}
          // Фон только на наведении/открытии — иначе плашка режет тень карточки композера
          onMouseEnter={e => { if (!open) e.currentTarget.style.background = C.accentLight; }}
          onMouseLeave={e => { if (!open) e.currentTarget.style.background = 'transparent'; }}
          style={compact ? compactStyle : {
            height: isMobile ? 32 : 28, padding: '0 8px 0 4px', borderRadius: R.md, border: 'none',
            background: open ? C.bgSelected : 'transparent', color: C.textSecondary,
            transition: 'background 0.15s',
            fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
            display: 'flex', alignItems: 'center', gap: 6,
            maxWidth: isMobile ? 120 : wide ? 360 : 200, overflow: 'hidden',
          }}
        >
          <PersonaAvatar persona={selectedPersona} size={compact ? 24 : isMobile ? 24 : 20} />
          {compact ? compactChevron : (
            <>
              <span style={{ fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1 }}>
                {personaLabel(selectedPersona)}
              </span>
              <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
            </>
          )}
        </button>
      ) : selectedAgentName ? (
        // Выбран .md-агент — плашка с цветной точкой и именем (как триггер AgentSelector)
        <button
          onClick={() => setOpen(o => !o)}
          title={compactTitle}
          // Фон только на наведении/открытии — иначе плашка режет тень карточки композера
          onMouseEnter={e => { if (!open) e.currentTarget.style.background = C.accentLight; }}
          onMouseLeave={e => { if (!open) e.currentTarget.style.background = 'transparent'; }}
          style={compact ? compactStyle : {
            height: isMobile ? 32 : 28, padding: '0 8px', borderRadius: R.md, border: 'none',
            background: open ? C.bgSelected : 'transparent', color: C.textSecondary,
            transition: 'background 0.15s',
            fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
            display: 'flex', alignItems: 'center', gap: 5,
            maxWidth: isMobile ? 110 : wide ? 360 : 200, overflow: 'hidden',
          }}
        >
          <span style={{ width: compact ? 10 : 8, height: compact ? 10 : 8, borderRadius: '50%', flexShrink: 0, background: agentDotColor(selectedAgent?.color) }} />
          {compact ? compactChevron : (
            <>
              <span style={{ fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1 }}>
                {agentDisplayName}
              </span>
              <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
            </>
          )}
        </button>
      ) : (
        // Никто не выбран — плашка с подписью. Голая иконка читалась как декорация:
        // было не догадаться, что здесь выбирается персона-собеседник.
        <button
          onClick={() => setOpen(o => !o)}
          title="Выбрать собеседника"
          // Фон только на наведении/открытии — иначе плашка режет тень карточки композера
          onMouseEnter={e => { if (!open) e.currentTarget.style.background = C.accentLight; }}
          onMouseLeave={e => { if (!open) e.currentTarget.style.background = 'transparent'; }}
          style={compact ? compactStyle : {
            height: isMobile ? 32 : 28, padding: '0 8px', borderRadius: R.md, border: 'none',
            background: open ? C.bgSelected : 'transparent', color: C.textSecondary,
            transition: 'background 0.15s',
            fontSize: 12.5, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans,
            display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, whiteSpace: 'nowrap',
          }}
        >
          <MessageCircle size={compact ? 16 : 14} strokeWidth={2} style={{ flexShrink: 0 }} />
          {compact ? compactChevron : (
            <>
              Собеседник
              <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
            </>
          )}
        </button>
      )}

      {open && groupMode && (
        <div style={dropdownStyle}>
          <div style={{
            padding: '8px 10px 4px', fontSize: 13, fontWeight: 700, color: C.textHeading,
            fontFamily: FONT.sans,
          }}>
            Групповой чат
          </div>
          <div style={{ padding: '0 10px 6px', fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.4 }}>
            Выберите 2–8 участников. Первый выбранный — ведущий: его зона и модель задают чат.
          </div>
          {/* Подгруппы участников: проектные → глобальные (с разделителями) */}
          {(() => {
            const personaGroups = (projectPersonas.length > 0 ? 1 : 0)
              + (regularGlobals.length > 0 ? 1 : 0);
            const showHeaders = personaGroups > 1;
            return (
              <>
                {showHeaders && projectPersonas.length > 0 && groupHeader('Команда проекта')}
                {projectPersonas.map(groupCheckboxItem)}
                {showHeaders && regularGlobals.length > 0 && groupHeader('Глобальные')}
                {regularGlobals.map(groupCheckboxItem)}
              </>
            );
          })()}
          {/* Предупреждение о разных провайдерах моделей участников: транскрипт живёт
              у провайдера, чужая модель не применится при смене спикера */}
          {(() => {
            const providers = new Set(groupSelected
              .map(id => personas.find(p => p.id === id))
              .filter(Boolean)
              .map(p => modelProvider(p!.model)));
            return providers.size > 1 ? (
              <div style={{
                margin: '6px 8px', padding: '6px 9px', borderRadius: R.md,
                background: C.warningBg, border: `1px solid ${C.warning}`,
                fontSize: 11.5, color: C.warningText, fontFamily: FONT.sans, lineHeight: 1.4,
              }}>
                У участников модели разных провайдеров — чат пойдёт на провайдере ведущего,
                чужие модели участников применяться не будут.
              </div>
            ) : null;
          })()}
          <div style={{ display: 'flex', gap: 6, padding: '8px 8px 4px' }}>
            <button
              onClick={() => { setGroupMode(false); setGroupSelected([]); }}
              style={{
                padding: '6px 12px', borderRadius: R.md, border: `1px solid ${C.border}`,
                background: C.bgWhite, color: C.textSecondary, fontSize: 12.5, fontWeight: 600,
                cursor: 'pointer', fontFamily: FONT.sans,
              }}
            >
              Назад
            </button>
            <button
              onClick={() => {
                if (groupSelected.length < 2) return;
                onCreateGroup?.(groupSelected);
                setOpen(false);
              }}
              disabled={groupSelected.length < 2}
              style={{
                flex: 1, padding: '6px 12px', borderRadius: R.md, border: 'none',
                background: groupSelected.length >= 2 ? C.accent : C.bgSelected,
                color: groupSelected.length >= 2 ? C.onAccent : C.textMuted,
                fontSize: 12.5, fontWeight: 700, cursor: groupSelected.length >= 2 ? 'pointer' : 'default',
                fontFamily: FONT.sans,
              }}
            >
              Создать групповой чат{groupSelected.length > 0 ? ` (${groupSelected.length})` : ''}
            </button>
          </div>
        </div>
      )}

      {open && !groupMode && (
        <div style={dropdownStyle}>
          {hasSelection && (
            <button
              onClick={() => { onSelect({ persona: null, agent: null }); setOpen(false); }}
              onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.bgSelected; }}
              onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
              style={{
                width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px',
                borderRadius: R.md, border: 'none', background: 'transparent', cursor: 'pointer',
                textAlign: 'left', color: C.textMuted, fontSize: 12.5,
              }}
            >
              <span style={{ width: 20, height: 20, borderRadius: R.full, border: `1.5px dashed ${C.border}`, flexShrink: 0 }} />
              Без собеседника
            </button>
          )}

          {/* Персоны подгруппами: Команда проекта → Глобальные, затем .md-агенты.
              Заголовки-разделители показываем, когда групп больше одной. */}
          {(() => {
            const personaGroups = (projectPersonas.length > 0 ? 1 : 0)
              + (regularGlobals.length > 0 ? 1 : 0);
            const showHeaders = personaGroups > 1 || plainAgents.length > 0;
            return (
              <>
                {showHeaders && projectPersonas.length > 0 && groupHeader('Команда проекта')}
                {projectPersonas.map(personaItem)}
                {showHeaders && regularGlobals.length > 0 && groupHeader('Глобальные')}
                {regularGlobals.map(personaItem)}
                {plainAgents.length > 0 && groupHeader('Агенты AI')}
                {plainAgents.map(agentItem)}
              </>
            );
          })()}

          {/* Вход в мультивыбор группового чата (нужно ≥2 доступных персон) */}
          {onCreateGroup && selectablePersonas.length >= 2 && (
            <button
              onClick={() => setGroupMode(true)}
              onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
              onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
              style={{
                width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px',
                marginTop: 2, borderTop: `1px solid ${C.divider}`, borderRadius: R.md,
                border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
                color: C.textSecondary, fontSize: 12.5, fontWeight: 600, fontFamily: FONT.sans,
              }}
            >
              <Users size={16} strokeWidth={2} style={{ flexShrink: 0 }} />
              Групповой чат…
            </button>
          )}
        </div>
      )}
    </div>
  );
}
