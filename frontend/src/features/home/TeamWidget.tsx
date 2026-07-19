import { useEffect, useMemo, useState } from 'react';
import { Users } from 'lucide-react';
import { C, FONT, R } from '../../lib/design';
import { usePersonas } from '../../lib/personas';
import { api } from '../../lib/api';
import type { Project } from '../../types';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import type { HubTab } from '../../components/HubTabs';
import { PENDING_PERSONA_CREATE_KEY } from './QuickActions';
import { WidgetCard, WidgetAction, WidgetEmpty, MiniSegment } from './WidgetCard';

type TeamMode = 'global' | 'all';

// «Команда»: персоны плитками аватарок; клик открывает профиль персоны (студию в разделе
// «Персоны»). Стор персон уже загружен HomePage (ensurePersonasLoaded).
export function TeamWidget({ onHubTab }: { onHubTab: (t: HubTab) => void }) {
  const personas = usePersonas();
  // Как и в разделе «Персоны»: по умолчанию только глобальные, проектные — по переключателю.
  // Ключ свой: витрина на дашборде и список в разделе переключаются независимо.
  const [mode, setMode] = useState<TeamMode>(() =>
    localStorage.getItem('cc_home_team_mode') === 'all' ? 'all' : 'global');
  useEffect(() => { localStorage.setItem('cc_home_team_mode', mode); }, [mode]);

  // Названия проектов нужны только для чипов у проектных персон — грузим лениво,
  // когда режим «Все» реально включили (на дашборде и так хватает запросов)
  const [projects, setProjects] = useState<Project[]>([]);
  useEffect(() => {
    if (mode !== 'all' || projects.length > 0) return;
    api.projects.list().then(setProjects).catch(() => { /* без названий обойдёмся */ });
  }, [mode, projects.length]);
  const projectName = useMemo(() => {
    const byId = new Map(projects.map(p => [p.id, p.name]));
    return (id?: string | null) => (id ? byId.get(id) : undefined);
  }, [projects]);

  // Глобальные впереди: при длинном списке проектных основные помощники не должны
  // уезжать вниз. Жёсткого капа нет — вместо него прокрутка (см. плитки ниже).
  const team = useMemo(() => {
    const globals = personas.filter(p => p.scope === 'global');
    if (mode !== 'all') return globals;
    return [...globals, ...personas.filter(p => p.scope === 'project')];
  }, [personas, mode]);

  const openProfile = (id: string) => {
    window.dispatchEvent(new CustomEvent('cc-open-url', {
      detail: { url: `#/personas/${encodeURIComponent(id)}` },
    }));
  };

  // Мастер создания живет в разделе «Персоны» — переход с хинтом автозапуска
  const createPersona = () => {
    sessionStorage.setItem(PENDING_PERSONA_CREATE_KEY, '1');
    onHubTab('personas');
  };

  return (
    <WidgetCard
      icon={<Users size={16} strokeWidth={2} />}
      title="Персоны"
      onCreate={createPersona}
      createTitle="Новая персона"
      action={
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <MiniSegment<TeamMode>
            value={mode} onChange={setMode}
            options={[
              { value: 'global', label: 'Глобальные', title: 'Только глобальные персоны' },
              { value: 'all', label: 'Все', title: 'Глобальные и проектные персоны' },
            ]}
          />
          <WidgetAction label="Все персоны →" onClick={() => onHubTab('personas')} />
        </span>
      }
    >
      {team.length === 0
        ? <WidgetEmpty text="Персон пока нет — создай первую в разделе «Персоны»." />
        : (
          // Прокрутка вместо обрезки списка: с проектными персонами плиток может быть
          // много, а виджет не должен растягивать дашборд
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, maxHeight: 216, overflowY: 'auto' }}>
            {team.map(p => (
              <button
                key={p.id}
                onClick={() => openProfile(p.id)}
                title={[p.role ? `${p.role} (${p.name})` : p.name, p.scope === 'project' ? projectName(p.projectId) : null].filter(Boolean).join(' · ')}
                style={{
                  display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5,
                  width: 76, padding: '8px 4px', borderRadius: 10, cursor: 'pointer',
                  background: 'none', border: 'none', minWidth: 0,
                }}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
              >
                <PersonaAvatar persona={p} size={44} />
                <span style={{
                  fontFamily: FONT.sans, fontSize: 11.5, color: C.textPrimary, maxWidth: '100%',
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {p.name}
                </span>
                {p.role && (
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted, maxWidth: '100%',
                    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', marginTop: -3,
                  }}>
                    {p.role}
                  </span>
                )}
                {/* Чип проекта — чтобы проектная персона не выглядела глобальной. Пока
                    список проектов не доехал, показываем нейтральное «Проект» */}
                {p.scope === 'project' && (
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 9.5, color: C.textSecondary, maxWidth: '100%',
                    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                    background: C.bgInset, borderRadius: R.pill, padding: '1px 6px', boxSizing: 'border-box',
                  }}>
                    {projectName(p.projectId) ?? 'Проект'}
                  </span>
                )}
              </button>
            ))}
          </div>
        )}
    </WidgetCard>
  );
}
