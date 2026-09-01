import type { Persona, Project } from '../../types';
import { Plus, Users } from 'lucide-react';
import { ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, R } from '../../lib/design';
import { personaTitleLines } from '../../lib/personas';
import { PillSwitch } from '../../components/Toolbar';
import { Button, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import { PersonaAvatar } from './PersonaAvatar';

// Что показывать в разделе: только глобальных или вообще всех (с проектными)
export type PersonaListMode = 'global' | 'all';

// Сайдбар раздела «Персоны» и панели «Команда» проекта: кнопка создания живёт
// в ЗАКРЕПЛЁННОМ слоте шапки карточки (PanelHeaderSlot pinned, как «+ Задача»
// у TasksPanel — accent primary, size="xs"): видна всегда, без наведения.
// Без шапки (мобильный стек, одноколоночная вкладка) — fallback той же кнопкой
// в полосе над списком. «Командный центр» рисуется первым пунктом списка
// (опция teamCenter) — это часть контента, а не тулбара.
export function PersonaList({ personas, selectedId, onSelect, onNew, mode, onModeChange, projects, teamCenter }: {
  personas: Persona[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
  // Переключатель зоны — только в глобальном разделе. В панели команды проекта список
  // и так ограничен проектом, поэтому пропсы опциональны: нет onModeChange — нет и тумблера.
  mode?: PersonaListMode;
  onModeChange?: (m: PersonaListMode) => void;
  projects?: Project[];
  // «Командный центр» — первый пункт списка (только панель «Команда»): та же строка-строка,
  // что и персоны, но с иконкой команды. active — открыт ли центр (персона не выбрана).
  teamCenter?: { active: boolean; onClick: () => void };
}) {
  // Панель в карточке с шапкой — кнопка создания уезжает в закреплённый слот шапки;
  // без шапки (мобила) остаётся в полосе над списком
  const inHeader = useHasPanelHeader();

  // Кнопка создания персоны — компактная accent (аналог TasksPanel:
  // variant="primary" size="xs" + Plus + короткая подпись). Подпись «Персона»
  // (а не «Новая персона») — место действия понятно из шапки и иконки плюс;
  // полное название остаётся в title для тултипа и доступности.
  const newBtn = (
    <Button variant="primary" size="xs" title="Новая персона"
      leftIcon={<Plus size={13} strokeWidth={ICON_STROKE} />}
      onClick={onNew}>
      Персона
    </Button>
  );

  // Полоса над списком остаётся только с содержимым: без шапки — кнопка (+ тумблер),
  // в карточке с шапкой — один тумблер зоны (кнопка уже в шапке). Пустой полосы
  // с бордером быть не должно — панель «Команда» проекта осталась бы с лишней чертой.
  const hasToolbar = !inHeader || !!onModeChange;

  return (
    <>
      {inHeader && <PanelHeaderSlot pinned>{newBtn}</PanelHeaderSlot>}
      {hasToolbar && (
        <div style={{ padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`, flex: 'none', display: 'flex', flexDirection: 'column', gap: 8 }}>
          {!inHeader && <div>{newBtn}</div>}
          {onModeChange && (
            <PillSwitch<PersonaListMode>
              value={mode ?? 'global'} onChange={onModeChange} fill
              options={[{ value: 'global', label: 'Глобальные' }, { value: 'all', label: 'Все' }]}
            />
          )}
        </div>
      )}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: 6 }}>
        {teamCenter && (
          <>
            <button
              onClick={teamCenter.onClick}
              onMouseEnter={e => { if (!teamCenter.active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
              onMouseLeave={e => { if (!teamCenter.active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
              style={{
                width: '100%', display: 'flex', alignItems: 'center', gap: 10,
                padding: '8px 10px', borderRadius: R.md, border: 'none', cursor: 'pointer',
                textAlign: 'left', background: teamCenter.active ? C.accentMuted : 'transparent',
              }}
            >
              {/* Иконка команды в кружке 32 — на месте аватара персоны, тот же ритм строки */}
              <span style={{
                width: 32, height: 32, borderRadius: '50%', background: `${C.accent}1F`,
                display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
              }}>
                <Users size={17} color={C.accent} strokeWidth={2} />
              </span>
              <span style={{ flex: 1, minWidth: 0 }}>
                <span style={{
                  display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>
                  Командный центр
                </span>
                <span style={{
                  display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>
                  Обзор · память · активность
                </span>
              </span>
            </button>
            {/* Тонкий разделитель: «домой раздела» отделён от списка персон */}
            <div style={{ height: 1, background: C.border, margin: '6px 4px' }} />
          </>
        )}
        {personas.length === 0 ? (
          <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Пока нет персон. Создай первую — задай ей имя, характер и аватар.
          </div>
        ) : (() => {
          // Пантеонные персоны (из каталога OmO — с templateKey) идут отдельной группой
          // внизу, под разделителем; обычные — выше.
          const own = personas.filter(p => !p.templateKey);
          const pantheon = personas.filter(p => p.templateKey);
          const row = (p: Persona) => {
            const active = p.id === selectedId;
            return (
              <button
                key={p.id}
                role="option"
                aria-selected={active}
                aria-label={`${personaTitleLines(p).primary}${personaTitleLines(p).secondary ? ' — ' + personaTitleLines(p).secondary : ''}`}
                onClick={() => onSelect(p.id)}
                onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
                onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
                style={{
                  width: '100%', display: 'flex', alignItems: 'center', gap: 10,
                  padding: '8px 10px', borderRadius: R.md, border: 'none', cursor: 'pointer',
                  textAlign: 'left', marginBottom: 2,
                  background: active ? C.accentMuted : 'transparent',
                }}
              >
                <PersonaAvatar persona={p} size={32} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  {/* Роль — главная строка, имя под ней (мельче, приглушённо) */}
                  <span style={{
                    display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {personaTitleLines(p).primary}
                  </span>
                  {personaTitleLines(p).secondary && (
                    <span style={{
                      display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}>
                      {personaTitleLines(p).secondary}
                    </span>
                  )}
                  {p.description && (
                    <span style={{
                      display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}>
                      {p.description}
                    </span>
                  )}
                </span>
              </button>
            );
          };
          // В режиме «Все» проектные персоны идут отдельными секциями под своим проектом:
          // плоским списком глобальные тонут среди проектных. Порядок проектов — как в
          // projects (там своя сортировка), персоны без живого проекта — в конец общей группой.
          // Группируем только там, где список смешанный (глобальный раздел в режиме «Все»).
          // В панели команды проекта персоны и так все из одного проекта — секции ни к чему.
          const grouped = mode === 'all' && !!projects;
          const ownGlobal = grouped ? own.filter(p => p.scope !== 'project') : own;
          const ownByProject = grouped
            ? (projects ?? [])
              .map(pr => ({ title: pr.name, rows: own.filter(p => p.scope === 'project' && p.projectId === pr.id) }))
              .filter(g => g.rows.length > 0)
            : [];
          const known = new Set((projects ?? []).map(pr => pr.id));
          const orphans = grouped
            ? own.filter(p => p.scope === 'project' && (!p.projectId || !known.has(p.projectId)))
            : [];

          return (
            <div role="listbox" aria-label="Список персон">
              {ownGlobal.map(row)}
              {ownByProject.map((g, i) => (
                <div key={g.title} role="group" aria-label={g.title}>
                  <div style={{ ...groupHeader, marginTop: i === 0 && ownGlobal.length === 0 ? 2 : 8 }}>{g.title}</div>
                  {g.rows.map(row)}
                </div>
              ))}
              {orphans.length > 0 && (
                <div>
                  <div style={{ ...groupHeader, marginTop: 8 }}>Проект удалён</div>
                  {orphans.map(row)}
                </div>
              )}
              {pantheon.length > 0 && (
                <>
                  {/* Разделитель + заголовок группы пантеона */}
                  <div style={{
                    margin: own.length > 0 ? '8px 8px 4px' : '2px 8px 4px',
                    borderTop: own.length > 0 ? `1px solid ${C.border}` : 'none',
                    paddingTop: own.length > 0 ? 8 : 0,
                    fontSize: 10.5, fontWeight: 700, color: C.textMuted,
                    textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
                  }}>
                    Пантеон OmO
                  </div>
                  {pantheon.map(row)}
                </>
              )}
            </div>
          );
        })()}
      </div>
    </>
  );
}

// Заголовок группы — тот же стиль, что у группы «Пантеон OmO» ниже по списку
const groupHeader: React.CSSProperties = {
  margin: '8px 8px 4px', paddingTop: 8, borderTop: `1px solid ${C.border}`,
  fontSize: 10.5, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
};
