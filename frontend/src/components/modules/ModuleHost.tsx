import { useEffect, useRef, useState } from 'react';
import type { ModuleInfo } from '../../lib/api';
import { loadModuleTab, type ModuleTabComponent, type AIHomeModuleContext } from '../../lib/modules';
import { ErrorBoundary } from '../ErrorBoundary';
import { C, FONT } from '../../lib/design';

// Плашка деградации (R9) — общая для ошибки загрузки remote и ошибки его рендера.
function ModuleFallback({ name, detail }: { name: string; detail: string }) {
  return (
    <div style={{ padding: 32, fontFamily: FONT.sans, color: C.textMuted, textAlign: 'center' }}>
      <p style={{ fontSize: 15, marginBottom: 8 }}>Модуль «{name}» временно недоступен</p>
      <p style={{ fontSize: 13, opacity: 0.7 }}>{detail}</p>
    </div>
  );
}

// Монтирует remote-компонент ./Tab модуля через Module Federation и передаёт ему
// AIHomeModuleContext (контракт §7, ТЗ R5). Деградация (R9): недоступный remote →
// сообщение об ошибке вместо падения оболочки; ошибка РЕНДЕРА remote-компонента
// локализуется ErrorBoundary — shell не размонтируется. Ремоунт при смене id модуля.
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
    return <ModuleFallback name={module.displayName} detail={error} />;
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
  return (
    <ErrorBoundary
      key={module.id}
      fallback={err => <ModuleFallback name={module.displayName} detail={String(err?.message ?? err)} />}
    >
      <Tab {...ctx} />
    </ErrorBoundary>
  );
}
