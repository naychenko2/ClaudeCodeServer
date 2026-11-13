// Сброс пользовательских стораций модулей при выходе из аккаунта: модуль (MF) не может быть
// импортирован в App.tsx, поэтому регистрирует свой сброс здесь, а App вызывает runLogoutResets
// в тех же двух местах, где чистит остальные сторы (cc-unauthorized и logout).

const _hooks = new Set<() => void>();

export function onLogoutReset(fn: () => void): () => void {
  _hooks.add(fn);
  return () => { _hooks.delete(fn); };
}

export function runLogoutResets() {
  _hooks.forEach(fn => { fn(); });
}
