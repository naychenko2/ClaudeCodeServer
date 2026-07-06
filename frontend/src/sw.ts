// Собственный service worker (vite-plugin-pwa strategies: 'injectManifest').
// Повторяет прежнее generateSW-поведение (precache + SPA-fallback с denylist)
// и добавляет web push: показ уведомлений и переход по диплинку по клику.

import { cleanupOutdatedCaches, createHandlerBoundToURL, precacheAndRoute } from 'workbox-precaching';
import type { PrecacheEntry } from 'workbox-precaching';
import { NavigationRoute, registerRoute } from 'workbox-routing';

declare let self: ServiceWorkerGlobalScope & {
  __WB_MANIFEST: Array<PrecacheEntry | string>;
};

// Обновление по кнопке в UpdatePrompt (registerType: 'prompt')
self.addEventListener('message', e => {
  if (e.data && e.data.type === 'SKIP_WAITING') void self.skipWaiting();
});

precacheAndRoute(self.__WB_MANIFEST);
cleanupOutdatedCaches();

// SPA-fallback. /api/* и OnlyOffice-пути (версионированные /X.Y.Z-hash/... и статика)
// не должны перехватываться SW — как в прежнем generateSW-конфиге.
registerRoute(new NavigationRoute(createHandlerBoundToURL('index.html'), {
  denylist: [/^\/api\//, /^\/\d/, /^\/web-apps\//, /^\/sdkjs\//, /^\/doceditor\//, /^\/doc\//, /^\/coauthoring\//, /^\/cache\//],
}));

// === Web push ===

interface PushPayload {
  title?: string;
  body?: string;
  url?: string;
  kind?: string;
  tag?: string;
}

self.addEventListener('push', e => {
  if (!e.data) return;
  let payload: PushPayload = {};
  try { payload = e.data.json() as PushPayload; }
  catch { payload = { body: e.data.text() }; }

  e.waitUntil(self.registration.showNotification(payload.title ?? 'AI Home', {
    body: payload.body ?? '',
    tag: payload.tag,           // одинаковый tag → уведомление заменяется, а не дублируется
    icon: '/pwa-192x192.png',
    badge: '/pwa-64x64.png',
    data: { url: payload.url },
  }));
});

// Клик по уведомлению: фокусируем открытое окно приложения (с переходом по диплинку)
// или открываем новое
self.addEventListener('notificationclick', e => {
  e.notification.close();
  const url: string = e.notification.data?.url ?? '/';
  e.waitUntil((async () => {
    const clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of clientList) {
      if ('focus' in client) {
        await client.focus();
        // Диплинк обрабатывается при загрузке страницы — навигируем окно целиком
        if ('navigate' in client) await (client as WindowClient).navigate(url);
        return;
      }
    }
    await self.clients.openWindow(url);
  })());
});
