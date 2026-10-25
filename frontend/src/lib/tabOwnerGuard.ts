// Сторож «одна вкладка — один владелец» (docs/architecture/plugins.md).
//
// Вкладка показывает данные одного человека (снимок /auth/me). Если в другой вкладке
// того же профиля вошёл иной пользователь с «запомнить меня», localStorage подменяется, и
// без сторожа вкладка продолжила бы работать от чужого имени. Сверяем владельца там, где
// вкладка могла пропустить смену: событие storage, возврат из bfcache и возврат на вкладку.
// Расхождение — перезагрузка (вкладка поднимется уже под актуальным владельцем),
// пустой токен — обычный выход, как после 401.

import { isTabOwnerConsistent } from './defaultPersona';
import { readStoredToken } from './offline';

const OWNER_KEYS = new Set<string | null>(['cc_token', 'cc_user_id', null]);

export interface TabOwnerGuardActions {
  reload: () => void;
  signOut: () => void;
}

const defaultActions: TabOwnerGuardActions = {
  reload: () => window.location.reload(),
  signOut: () => window.dispatchEvent(new Event('cc-unauthorized')),
};

export function wireTabOwnerGuard(actions: TabOwnerGuardActions = defaultActions): () => void {
  const check = () => {
    if (!readStoredToken()) actions.signOut();
    else if (!isTabOwnerConsistent()) actions.reload();
  };
  const onStorage = (e: StorageEvent) => {
    if (OWNER_KEYS.has(e.key)) check();
  };
  const onPageShow = (e: PageTransitionEvent) => {
    if (e.persisted) check();
  };
  const onVisibility = () => {
    if (document.visibilityState === 'visible') check();
  };
  window.addEventListener('storage', onStorage);
  window.addEventListener('pageshow', onPageShow);
  document.addEventListener('visibilitychange', onVisibility);
  return () => {
    window.removeEventListener('storage', onStorage);
    window.removeEventListener('pageshow', onPageShow);
    document.removeEventListener('visibilitychange', onVisibility);
  };
}
