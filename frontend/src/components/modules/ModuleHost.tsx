import { useEffect, useRef, useState } from 'react';
import type { ModuleInfo } from '../../lib/api';
import { loadModuleTab, type ModuleTabComponent, type AIHomeModuleContext } from '../../lib/modules';
import { C, FONT } from '../../lib/design';

// Монтирует remote-компонент ./Tab модуля через Module Federation и передаёт ему
// AIHomeModuleContext (контракт §7, ТЗ R5). Деградация (R9): недоступный remote →
// сообщение об ошибке вместо падения оболочки. Ремоунт при смене id модуля.
export function ModuleHost({ module, theme, user, onTitleChange }: {
  module: ModuleInfo;
  theme: 'light' | 'dark';
  user: { id: string; name: string };
  onTitleChange?: (t: string) => void;
}) {
  const [Tab, setTab] = useState<ModuleTabComponent | null>(null);
  const [error, setError] = useState<string | null>(null);
  const themeRef = useRef(theme);
  themeRef.current = theme;

  useEffect(() => {
    let alive = true;
    setTab(null);
    setError(null);
    loadModuleTab(module)
      .then(cmp => { if (alive) setTab(() => cmp); })
      .catch(e => { if (alive) setError(String(e?.message ?? e)); });
    return () => { alive = false; };
  }, [module.id, module.remoteEntry]);

  if (error) {
    return (
      <div style={{ padding: 32, fontFamily: FONT.sans, color: C.textMuted, textAlign: 'center' }}>
        <p style={{ fontSize: 15, marginBottom: 8 }}>Модуль «{module.displayName}» временно недоступен</p>
        <p style={{ fontSize: 13, opacity: 0.7 }}>{error}</p>
      </div>
    );
  }
  if (!Tab) {
    return <div style={{ padding: 32, fontFamily: FONT.sans, color: C.textMuted }}>Загрузка модуля…</div>;
  }

  const ctx: AIHomeModuleContext = {
    user,
    apiBase: module.apiBase,
    getToken: () => localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'),
    theme: { mode: theme },
    navigate: (hash: string) => { window.location.hash = hash; },
    onTitleChange,
    schemaVersion: module.schemaVersion,
  };
  return <Tab {...ctx} />;
}
