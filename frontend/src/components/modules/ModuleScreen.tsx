import { useEffect, useState } from 'react';
import type { AuthState } from '../../types';
import { displayNameOf } from '../../types';
import type { HubTabValue } from '../HubTabs';
import { HubHeader } from '../HubHeader';
import { C } from '../../lib/design';
import { getModule, useModules } from '../../lib/modules';
import { useThemeMode, getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { ModuleHost } from './ModuleHost';

// Generic-раздел внешнего модуля (контракт §7, ТЗ R6): шапка-хаб + ModuleHost
// с remote ./Tab. НЕ частный случай под конкретный модуль — любой модуль из реестра.
export function ModuleScreen({ moduleId, auth, onLogout, onHubTab }: {
  moduleId: string;
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}) {
  useModules();                    // ре-рендер при загрузке списка модулей
  useThemeMode();
  const [theme, setTheme] = useState(getEffectiveTheme());
  useEffect(() => subscribeThemeMode(() => setTheme(getEffectiveTheme())), []);

  const module = getModule(moduleId);

  return (
    <div style={{ minHeight: '100vh', background: C.bgMain, display: 'flex', flexDirection: 'column' }}>
      <HubHeader value={`module:${moduleId}`} onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>
        {module
          ? <ModuleHost
              module={module}
              theme={theme}
              user={{ id: auth.id ?? '', name: displayNameOf(auth) }}
            />
          : <div style={{ padding: 32, color: C.textMuted }}>Модуль не найден или отключён.</div>}
      </div>
    </div>
  );
}
