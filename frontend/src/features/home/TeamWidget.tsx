import { Users } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { usePersonas } from '../../lib/personas';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import type { HubTab } from '../../components/HubTabs';
import { PENDING_PERSONA_CREATE_KEY } from './QuickActions';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

// «Команда»: глобальные персоны плитками аватарок; клик открывает профиль персоны
// (студию в разделе «Персоны»). Стор персон уже загружен HomePage (ensurePersonasLoaded).
export function TeamWidget({ onHubTab }: { onHubTab: (t: HubTab) => void }) {
  const personas = usePersonas();
  // Кап на два ряда плиток — как топ-5 в списочных виджетах; остальные за «Все персоны →»
  const team = personas.filter(p => p.scope === 'global').slice(0, 12);

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
      title="Команда"
      onCreate={createPersona}
      createTitle="Новая персона"
      action={<WidgetAction label="Все персоны →" onClick={() => onHubTab('personas')} />}
    >
      {team.length === 0
        ? <WidgetEmpty text="Персон пока нет — создай первую в разделе «Персоны»." />
        : (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
            {team.map(p => (
              <button
                key={p.id}
                onClick={() => openProfile(p.id)}
                title={p.role ? `${p.role} (${p.name})` : p.name}
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
              </button>
            ))}
          </div>
        )}
    </WidgetCard>
  );
}
