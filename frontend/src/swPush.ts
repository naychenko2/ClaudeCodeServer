// Чистые функции клика по web push (без self/window): их импортирует и service worker
// (sw.ts), и лента уведомлений приложения (App.tsx), а vitest проверяет без браузера.

// Относительный путь приложения: «/x» или «#/x», не «//» (protocol-relative), без обратных
// слэшей и управляющих символов — парсер URL вырезает таб и перевод строки, и «/\t/evil»
// превратился бы в «//evil». Та же форма, что пропускает сервер (NotificationUrls)
export function isAppRelativeUrl(url: string): boolean {
  return /^#?\/(?!\/)/.test(url) && !/[\\\u0000-\u001f\u007f]/.test(url);
}

// Куда вести клик по уведомлению. URL от бэка (/chats/{id}) превращается в абсолютный
// hash-URL SPA: https://host/#/chats/{id}. Открываем только свой origin: url пишет и модель
// (MCP notifications_create), и чужой адрес под видом «Одобрите шаг» был бы фишингом —
// всё, что не путь приложения и не наш абсолютный адрес, ведёт в корень приложения (S7)
export function notificationTargetUrl(data: { url?: string } | undefined, origin: string): string {
  const raw = data?.url ?? '/';
  const baseUrl = origin.replace(/\/+$/, '');
  if (isAppRelativeUrl(raw)) return raw.startsWith('#') ? `${baseUrl}/${raw}` : `${baseUrl}/#${raw.slice(1)}`;
  if (isSameOrigin(raw, baseUrl)) return raw;
  return baseUrl + '/#';
}

function isSameOrigin(url: string, baseUrl: string): boolean {
  try {
    const parsed = new URL(url);
    return /^https?:$/.test(parsed.protocol) && parsed.origin === new URL(baseUrl).origin;
  } catch {
    return false;
  }
}
