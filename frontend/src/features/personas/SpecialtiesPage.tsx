// Раздел «Специальности» — самостоятельный экран хаба (#/specialties). Раньше жил
// режимом внутри «Персон» (PillSwitch «Персоны | Специальности»); переключатель убран,
// вход — пункт «Специальности» меню аватара. Вкладки в таббаре у раздела нет (TABLESS),
// но верхняя навигация остаётся: HubHeader рисуется здесь, как в KnowledgePage.
//
// Экраны и под-адреса те же, что были: витрина списка (#/specialties), визитка роли
// (#/specialties/{roleKey}), настройка роли (#/specialties/{roleKey}/edit, только админу).
// Роутер под-адресов — PersonasSpecialties, здесь только состояние, история и шапка.

import { useCallback, useEffect, useState } from 'react';
import { ChevronLeft } from 'lucide-react';
import type { AuthState } from '../../types';
import type { HubTabValue } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PageCanvas } from '../../components/ui/PageCanvas';
import { C, FONT, FS, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { IconButton } from '../../components/ui/IconButton';
import { useIsMobile } from '../../lib/breakpoints';
import { useMe } from '../../lib/defaultPersona';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { parseHash, navPush, navReplace, type NavSnapshot } from '../../lib/nav';
import { PersonasSpecialties } from './PersonasSpecialties';

type ViewMode = 'list' | 'role' | 'edit';

export function SpecialtiesPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}) {
  const isMobile = useIsMobile();
  const me = useMe();
  const isAdmin = me.role === 'admin';

  // Стартовое положение — из адреса: #/specialties/{roleKey}[/edit]
  const [roleKey, setRoleKey] = useState<string | null>(() => {
    const t = parseHash();
    return t?.screen === 'specialties' ? (t.specialtyKey ?? null) : null;
  });
  const [viewMode, setViewMode] = useState<ViewMode>(() => {
    const t = parseHash();
    if (t?.screen !== 'specialties' || !t.specialtyKey) return 'list';
    return t.specialtyEdit ? 'edit' : 'role';
  });

  // Прямой заход по .../edit не-админом — даунгрейд до визитки (серверная правка
  // и без того закрыта, но экран формы показывать незачем). me приезжает асинхронно,
  // поэтому проверка живёт в эффекте, а не только в стартовом состоянии.
  useEffect(() => {
    if (viewMode === 'edit' && me.loaded && !isAdmin) {
      setViewMode('role');
      if (roleKey) navReplace(snapshot(roleKey, 'role'));
    }
  }, [viewMode, me.loaded, isAdmin, roleKey]);

  // Возврат/вперёд браузера: положение раздела восстанавливается из снимка истории.
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen !== 'specialties') return;
      setRoleKey(s.specialty ?? null);
      setViewMode(!s.specialty ? 'list' : (s.specialtyEdit ? 'edit' : 'role'));
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const goList = useCallback(() => {
    setRoleKey(null); setViewMode('list');
    navPush(snapshot(null, 'list'));
  }, []);
  const goRole = useCallback((key: string) => {
    setRoleKey(key); setViewMode('role');
    navPush(snapshot(key, 'role'));
  }, []);
  const goEdit = useCallback((key: string) => {
    setRoleKey(key); setViewMode('edit');
    navPush(snapshot(key, 'edit'));
  }, []);

  // Подпись мобильной шапки экрана: имя открытой роли (на визитке и в форме тулбар
  // текста не рисует — там его негде разместить), иначе имя раздела.
  const catalog = useSpecialtyCatalog();
  const roleLabel = roleKey
    ? catalog?.find(r => r.key === roleKey)?.label ?? null
    : null;

  return (
    <PageCanvas style={{ height: '100%' }}>
      <HubHeader value="specialties" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {isMobile && (
          <div style={{
            flex: 'none', display: 'flex', alignItems: 'center', gap: SP.sm,
            padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
          }}>
            {roleKey && (
              <IconButton onClick={goList} title="Назад" size="lg">
                <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            <div style={{
              fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 600,
              color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis',
              whiteSpace: 'nowrap', flex: 1, minWidth: 0,
            }}>
              {roleLabel ?? 'Специальности'}
            </div>
          </div>
        )}
        <div style={{ flex: 1, minHeight: 0 }}>
          <PersonasSpecialties
            roleKey={roleKey}
            viewMode={viewMode}
            onNavigateList={goList}
            onNavigateRole={goRole}
            onNavigateEdit={goEdit}
          />
        </div>
      </div>
    </PageCanvas>
  );
}

// Снимок навигации раздела — одна точка сборки: адрес (#/specialties/…) собирает
// toHash из lib/nav по этим же полям.
function snapshot(key: string | null, mode: ViewMode): NavSnapshot {
  return { screen: 'specialties', specialty: key, specialtyEdit: mode === 'edit' };
}
