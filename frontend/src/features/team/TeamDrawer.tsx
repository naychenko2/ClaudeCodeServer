// Раскрывашка «Обсудить с командой» над композером: карточки механик группами-колонками
// и зона настроек выбранной механики (по макету team-discuss-panel). Контролируемый
// компонент: выбранная механика и настройки живут у родителя (Composer), сюда приходят
// пропсами. Тему пользователь пишет в самом поле композера.
import { useState } from 'react';
import { C, FONT, R, SHADOW } from '../../lib/design';
import type { PantheonTemplate, Persona } from '../../types';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import { usePantheon, materializePantheon } from '../personas/usePantheon';
import { agentDotColor } from '../../components/AgentSelector';
import { showToast } from '../../lib/toast';
import {
  TEAM_MECHANICS, teamMechanic, costEstimate,
  type TeamMechanic, type TeamMechanicId, type TeamMechanicSettings, type TeamPersonaRef,
} from './teamMechanics';

export interface TeamDrawerProps {
  open: boolean;
  mech: TeamMechanicId | null;
  settings: TeamMechanicSettings;
  // Персоны-кандидаты в участники (Persona структурно расширяет TeamPersonaRef —
  // в настройки уходит минимальный ref, аватары рисуются по полной персоне)
  candidates: Persona[];
  // Имена установленных скиллов (из пропа skills композера): механики
  // с requiredSkill вне этого списка показываются задизейбленными
  availableSkills: string[];
  isMobile?: boolean;
  onPick: (id: TeamMechanicId) => void;
  onSettings: (s: TeamMechanicSettings) => void;
  onClose: () => void;
  // Сброс зависшего OMC-режима (ralph/autopilot/ultraqa) тихим ходом
  // /oh-my-claudecode:cancel; undefined — скилл недоступен, кнопка скрыта
  onResetModes?: () => void;
}

// Минимальный ref участника для настроек (буксируем только нужное buildTeamTurnText)
function toRef(p: Persona): TeamPersonaRef {
  return { id: p.id, handle: p.handle, name: p.name, role: p.role };
}

// Сегмент-переключатель (как в макете: дорожка bgSelected, активная кнопка bgWhite)
function Seg<T extends string | number>({ options, value, onChange, fmt }: {
  options: readonly T[];
  value: T;
  onChange: (v: T) => void;
  fmt?: (v: T) => string;
}) {
  return (
    <span style={{ display: 'inline-flex', background: C.bgSelected, borderRadius: R.pill, padding: 2 }}>
      {options.map(o => {
        const on = o === value;
        return (
          <button
            key={String(o)}
            type="button"
            onClick={() => onChange(o)}
            style={{
              border: 'none', background: on ? C.bgWhite : 'none', padding: '4px 12px',
              borderRadius: 7, cursor: 'pointer', fontSize: 11.5, fontWeight: 600,
              color: on ? C.textHeading : C.textSecondary, fontFamily: FONT.sans,
              boxShadow: on ? SHADOW.card : 'none', whiteSpace: 'nowrap',
            }}
          >
            {fmt ? fmt(o) : String(o)}
          </button>
        );
      })}
    </span>
  );
}

// Чекбокс настройки
function Chk({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label style={{
      display: 'inline-flex', alignItems: 'center', gap: 6, cursor: 'pointer',
      fontSize: 12, color: C.textPrimary, fontFamily: FONT.sans, userSelect: 'none',
    }}>
      <input
        type="checkbox"
        checked={checked}
        onChange={e => onChange(e.target.checked)}
        style={{ accentColor: C.accent, width: 14, height: 14, margin: 0, cursor: 'pointer' }}
      />
      {label}
    </label>
  );
}

// Метка настройки
function SLabel({ children }: { children: React.ReactNode }) {
  return (
    <span style={{ fontSize: 11, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans, marginRight: 6 }}>
      {children}
    </span>
  );
}

export function TeamDrawer({ open, mech, settings, candidates, availableSkills, isMobile, onPick, onSettings, onClose, onResetModes }: TeamDrawerProps) {
  // Виртуальные роли пантеона — кандидатами в участники (материализуются при выборе,
  // как в старом диалоге дискуссии)
  const { virtual: virtualPantheon } = usePantheon();
  const [materializing, setMaterializing] = useState<string | null>(null);
  // Локально материализованные роли — показываем их как обычных кандидатов
  const [extraCandidates, setExtraCandidates] = useState<Persona[]>([]);
  const allCandidates = [...candidates, ...extraCandidates.filter(e => !candidates.some(c => c.id === e.id))];

  const m = mech ? teamMechanic(mech) : null;
  // Лимит участников: дискуссия — 2, панель экспертов — 4
  const maxParticipants = mech === 'panel' ? 4 : 2;
  const selectedIds = settings.participants.map(p => p.id);

  const toggleParticipant = (p: Persona) => {
    const next = selectedIds.includes(p.id)
      ? settings.participants.filter(x => x.id !== p.id)
      : settings.participants.length >= maxParticipants
        ? settings.participants
        : [...settings.participants, toRef(p)];
    onSettings({ ...settings, participants: next });
  };

  // Выбор виртуальной роли пантеона: тихо подключаем персону и добавляем в участники
  const pickVirtual = async (t: PantheonTemplate) => {
    if (materializing || settings.participants.length >= maxParticipants) return;
    setMaterializing(t.key);
    try {
      const persona = await materializePantheon(t.key);
      setExtraCandidates(prev => prev.some(p => p.id === persona.id) ? prev : [...prev, persona]);
      if (!selectedIds.includes(persona.id) && settings.participants.length < maxParticipants)
        onSettings({ ...settings, participants: [...settings.participants, toRef(persona)] });
    } catch (e) {
      showToast('Пантеон OmO', e instanceof Error ? e.message : 'Не удалось подключить роль', 'info');
    } finally {
      setMaterializing(null);
    }
  };

  // Пикер персон-чипов (дискуссия — до 2; панель в режиме «Мои персоны» — до 4)
  const personaChips = (
    <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
      {allCandidates.map(p => {
        const on = selectedIds.includes(p.id);
        const disabled = !on && settings.participants.length >= maxParticipants;
        return (
          <button
            key={p.id}
            type="button"
            onClick={() => toggleParticipant(p)}
            disabled={disabled}
            title={disabled ? `Не больше ${maxParticipants} участников` : undefined}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 6,
              border: `1px solid ${on ? C.accent : C.border}`,
              background: on ? C.accentLight : C.bgCard,
              borderRadius: R.max, padding: '3px 10px 3px 4px', cursor: disabled ? 'default' : 'pointer',
              fontSize: 11.5, color: C.textPrimary, fontFamily: FONT.sans,
              opacity: disabled ? 0.5 : 1,
            }}
          >
            <PersonaAvatar persona={p} size={18} />
            {p.role ? `${p.role} (${p.name})` : p.name}
          </button>
        );
      })}
      {virtualPantheon.map(t => (
        <button
          key={`v-${t.key}`}
          type="button"
          onClick={() => void pickVirtual(t)}
          disabled={materializing !== null || settings.participants.length >= maxParticipants}
          title={t.description}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 6,
            border: `1px dashed ${C.border}`, background: C.bgCard,
            borderRadius: R.max, padding: '3px 10px 3px 4px',
            cursor: materializing ? 'default' : 'pointer',
            fontSize: 11.5, color: C.textSecondary, fontFamily: FONT.sans,
            opacity: materializing !== null && materializing !== t.key ? 0.5 : 1,
          }}
        >
          <span style={{
            width: 18, height: 18, borderRadius: R.full, flexShrink: 0,
            background: agentDotColor(t.color), color: '#fff',
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            fontSize: 9, fontWeight: 700,
          }}>{t.role.slice(0, 1)}</span>
          {materializing === t.key ? 'Подключаю…' : t.role}
        </button>
      ))}
    </span>
  );

  // Зона настроек выбранной механики
  const renderSettings = () => {
    if (!m || !mech) {
      return (
        <span style={{ color: C.textMuted, fontSize: 12, fontFamily: FONT.sans }}>
          Выбери механику, чтобы настроить её.
        </span>
      );
    }
    const parts: React.ReactNode[] = [];
    switch (mech) {
      case 'discuss':
        parts.push(
          <span key="p" style={{ display: 'inline-flex', alignItems: 'center', flexWrap: 'wrap', gap: 6 }}>
            <SLabel>Участники (до 2):</SLabel>
            {personaChips}
          </span>,
        );
        if (settings.participants.length === 0) {
          parts.push(
            <span key="hint" style={{ fontSize: 11, color: C.warningText, fontFamily: FONT.sans }}>
              Отметь хотя бы одного участника — без этого дискуссию не начать.
            </span>,
          );
        }
        break;
      case 'panel':
        parts.push(
          <span key="r" style={{ display: 'inline-flex', alignItems: 'center' }}>
            <SLabel>Раунды:</SLabel>
            <Seg options={[1, 2, 3] as const} value={settings.rounds} onChange={v => onSettings({ ...settings, rounds: v })} />
          </span>,
          <span key="e" style={{ display: 'inline-flex', alignItems: 'center' }}>
            <SLabel>Эксперты:</SLabel>
            <Seg
              options={['roles', 'personas'] as const}
              value={settings.expertsMode}
              onChange={v => onSettings({ ...settings, expertsMode: v })}
              fmt={v => v === 'roles' ? 'Анонимные роли' : 'Мои персоны'}
            />
          </span>,
        );
        if (settings.expertsMode === 'personas') {
          parts.push(
            <span key="p" style={{ display: 'inline-flex', alignItems: 'center', flexWrap: 'wrap', gap: 6 }}>
              {personaChips}
              <span style={{ fontSize: 10.5, color: C.textMuted, fontFamily: FONT.sans }}>
                по порядку ролей: Генератор, Критик, Адвокат, Модератор
              </span>
            </span>,
          );
        }
        parts.push(
          <Chk key="ctx" checked={settings.attachContext} label="Приложить контекст чата"
            onChange={v => onSettings({ ...settings, attachContext: v })} />,
        );
        break;
      case 'consensus':
        parts.push(
          <Chk key="i" checked={settings.interviewFirst} label="Интервью перед планом"
            onChange={v => onSettings({ ...settings, interviewFirst: v })} />,
          <Chk key="d" checked={settings.deliberate} label="Тщательный режим (медленнее)"
            onChange={v => onSettings({ ...settings, deliberate: v })} />,
        );
        break;
      case 'interview':
        parts.push(
          <span key="d" style={{ display: 'inline-flex', alignItems: 'center' }}>
            <SLabel>Глубина:</SLabel>
            <Seg
              options={['quick', 'standard', 'deep'] as const}
              value={settings.depth}
              onChange={v => onSettings({ ...settings, depth: v })}
              fmt={v => ({ quick: 'Быстро', standard: 'Стандарт', deep: 'Глубоко' })[v]}
            />
          </span>,
        );
        break;
      case 'autopilot':
        parts.push(
          <span key="l" style={{ display: 'inline-flex', alignItems: 'center' }}>
            <SLabel>Завершение:</SLabel>
            <Seg
              options={['plan', 'done'] as const}
              value={settings.untilDone ? 'done' : 'plan'}
              onChange={v => onSettings({ ...settings, untilDone: v === 'done' })}
              fmt={v => v === 'plan' ? 'Остановиться на плане' : 'Цикл «до готово»'}
            />
          </span>,
        );
        break;
      case 'qa':
        parts.push(
          <span key="t" style={{ display: 'inline-flex', alignItems: 'center' }}>
            <SLabel>Цель:</SLabel>
            <Seg options={['tests', 'build', 'lint', 'typecheck'] as const} value={settings.qaTarget}
              onChange={v => onSettings({ ...settings, qaTarget: v })} />
          </span>,
        );
        break;
      case 'trace':
      case 'sci':
        parts.push(
          <span key="n" style={{ color: C.textMuted, fontSize: 12, fontFamily: FONT.sans }}>
            Без настроек — опиши задачу в поле сообщения.
          </span>,
        );
        break;
    }
    // «Остановиться на плане» подменяет автопилот на консенсус-план при сборке текста
    // (см. Composer) — оценку тяжести показываем от фактической механики
    const estId: TeamMechanicId = mech === 'autopilot' && !settings.untilDone ? 'consensus' : mech;
    return (
      <>
        {parts}
        <span style={{ marginLeft: 'auto', fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, whiteSpace: 'nowrap' }}>
          {costEstimate(estId, settings)}
        </span>
      </>
    );
  };

  // Карточка механики
  const card = (mc: TeamMechanic) => {
    const missingSkill = mc.requiredSkill !== null && !availableSkills.includes(mc.requiredSkill);
    const disabled = !!mc.soon || missingSkill;
    const selected = mech === mc.id;
    const Icon = mc.icon;
    return (
      <button
        key={mc.id}
        type="button"
        disabled={disabled}
        onClick={() => onPick(mc.id)}
        title={mc.soon ? 'В следующей итерации'
          : missingSkill ? `Скилл «${mc.requiredSkill}» не установлен в этом окружении`
          : undefined}
        onMouseEnter={e => { if (!disabled && !selected) e.currentTarget.style.borderColor = C.accent; }}
        onMouseLeave={e => { if (!disabled && !selected) e.currentTarget.style.borderColor = C.border; }}
        style={{
          width: '100%', textAlign: 'left', position: 'relative',
          display: 'flex', gap: 9, alignItems: 'flex-start',
          background: selected ? C.accentLight : C.bgCard,
          border: `1px solid ${selected ? C.accent : C.border}`,
          borderRadius: R.xl, padding: '8px 10px',
          cursor: disabled ? 'not-allowed' : 'pointer',
          opacity: disabled ? 0.45 : 1,
          boxShadow: selected ? SHADOW.focus : 'none',
          transition: 'border-color 0.12s, box-shadow 0.12s',
          fontFamily: FONT.sans, color: C.textPrimary,
        }}
      >
        {mc.soon ? (
          <span style={{
            position: 'absolute', top: 7, right: 8, fontSize: 9, fontWeight: 700,
            color: C.textMuted, border: `1px solid ${C.border}`, padding: '1px 5px',
            borderRadius: R.max, background: C.bgInset,
          }}>скоро</span>
        ) : (
          <span title="Ориентировочная стоимость" style={{
            position: 'absolute', top: 8, right: 9, fontSize: 9.5,
            color: C.textMuted, letterSpacing: '0.05em',
          }}>{'¢'.repeat(mc.cost)}</span>
        )}
        <span style={{ flexShrink: 0, marginTop: 1, display: 'flex', color: selected ? C.accent : C.textSecondary }}>
          <Icon size={17} strokeWidth={2} />
        </span>
        <span style={{ minWidth: 0 }}>
          <span style={{ display: 'block', fontWeight: 700, fontSize: 12, color: C.textHeading, lineHeight: 1.25, paddingRight: 20 }}>
            {mc.name}
          </span>
          <span style={{ display: 'block', fontSize: 10.5, color: C.textSecondary, marginTop: 2, lineHeight: 1.3 }}>
            {mc.desc}
          </span>
        </span>
      </button>
    );
  };

  // Группы в порядке первого появления в реестре
  const groups: string[] = [];
  for (const mc of TEAM_MECHANICS) if (!groups.includes(mc.group)) groups.push(mc.group);

  return (
    <div style={{
      overflow: 'hidden',
      maxHeight: open ? 560 : 0,
      opacity: open ? 1 : 0,
      marginBottom: open ? 8 : 0,
      transition: 'max-height 0.28s ease, opacity 0.22s ease, margin-bottom 0.28s ease',
      background: C.bgPanel,
      border: `1px solid ${C.border}`,
      borderRadius: '16px 16px 6px 6px',
      boxShadow: SHADOW.sheet,
      pointerEvents: open ? 'auto' : 'none',
    }}>
      {/* Шапка */}
      <div style={{ display: 'flex', alignItems: 'center', padding: '12px 16px 4px' }}>
        <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: 15, fontWeight: 700, color: C.textHeading }}>
          Обсудить с командой
        </h3>
        {!isMobile && (
          <span style={{ marginLeft: 10, color: C.textMuted, fontSize: 11, fontFamily: FONT.sans }}>
            выбери механику — тему пиши в поле сообщения
          </span>
        )}
        {onResetModes && (
          <button
            type="button"
            onClick={onResetModes}
            title="Остановить зависший командный режим (автопилот, QA-цикл): отправит /oh-my-claudecode:cancel и очистит его состояние"
            style={{
              marginLeft: 'auto', border: 'none', background: 'none', color: C.textMuted,
              cursor: 'pointer', fontSize: 11, fontFamily: FONT.sans, fontWeight: 600,
              padding: '4px 8px', borderRadius: R.md, whiteSpace: 'nowrap',
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
          >
            Сбросить режим
          </button>
        )}
        <button
          type="button"
          onClick={onClose}
          title="Закрыть"
          style={{
            marginLeft: onResetModes ? 4 : 'auto', border: 'none', background: 'none', color: C.textMuted,
            cursor: 'pointer', fontSize: 15, width: 28, height: 28, borderRadius: R.md,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
          onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
          onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
        >
          ✕
        </button>
      </div>

      {/* Карточки механик: группы-колонки, на узких экранах переносятся (auto-fit) */}
      <div style={{ padding: '8px 16px 2px', overflowY: 'auto', maxHeight: isMobile ? '38vh' : 330 }}>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(172px, 1fr))', gap: '12px 14px' }}>
          {groups.map(g => (
            <div key={g} style={{ display: 'flex', flexDirection: 'column', gap: 6, minWidth: 0 }}>
              <span style={{
                fontSize: 10, fontWeight: 700, letterSpacing: '0.08em', textTransform: 'uppercase',
                color: C.textMuted, paddingLeft: 2, fontFamily: FONT.sans,
              }}>{g}</span>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {TEAM_MECHANICS.filter(mc => mc.group === g).map(card)}
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Настройки выбранной механики */}
      <div style={{
        padding: '10px 16px 14px', borderTop: `1px dashed ${C.divider}`, marginTop: 8,
        display: 'flex', flexWrap: 'wrap', gap: '10px 22px', alignItems: 'center', minHeight: 56,
      }}>
        {renderSettings()}
      </div>
    </div>
  );
}
