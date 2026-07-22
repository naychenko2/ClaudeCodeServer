import type { Persona, Project } from '../../types';
import { Plus, Users } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, R } from '../../lib/design';
import { personaTitleLines } from '../../lib/personas';
import { PillSwitch } from '../../components/Toolbar';
import { Button } from '../../components/ui';
import { PersonaAvatar } from './PersonaAvatar';

// Что показывать в разделе: только глобальных или вообще всех (с проектными)
export type PersonaListMode = 'global' | 'all';

// Сайдбар раздела «Персоны»: кнопка создания сверху, ниже — список персон.
export function PersonaList({ personas, selectedId, onSelect, onNew, mode, onModeChange, projects, dashedNewButton, teamCenter }: {
  personas: Persona[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
  // Переключатель зоны — только в глобальном разделе. В панели команды проекта список
  // и так ограничен проектом, поэтому пропсы опциональны: нет onModeChange — нет и тумблера.
  mode?: PersonaListMode;
  onModeChange?: (m: PersonaListMode) => void;
  projects?: Project[];
  // Пунктирная кнопка создания «как Новый чат» — включается в панели «Команда» проекта
  // (единый вид с сайдбаром чатов). Хаб «Персоны» оставляет прежнюю залитую кнопку.
  dashedNewButton?: boolean;
  // «Командный центр» — первый пункт списка (только панель «Команда»): та же строка-строка,
  // что и персоны, но с иконкой команды. active — открыт ли центр (персона не выбрана).
  teamCenter?: { active: boolean; onClick: () => void };
}) {
  return (
    <>
      <div style={{ padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`, flex: 'none', display: 'flex', flexDirection: 'column', gap: 8 }}>
        {dashedNewButton ? (
          <Button variant="dashed" size="md" fullWidth onClick={onNew}
            leftIcon={<Plus size={15} strokeWidth={2.2} />}>
            Новая персона
          </Button>
        ) : (
          <button onClick={onNew} style={newBtn}>
            <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />Новая персона
          </button>
        )}
        {onModeChange && (
          <PillSwitch<PersonaListMode>
            value={mode ?? 'global'} onChange={onModeChange} fill
            options={[{ value: 'global', label: 'Глобальные' }, { value: 'all', label: 'Все' }]}
          />
        )}
      </div>
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
            <>
              {ownGlobal.map(row)}
              {ownByProject.map((g, i) => (
                <div key={g.title}>
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
            </>
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

const newBtn: React.CSSProperties = {
  width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 5,
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '8px 12px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
