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

// SPA-fallback. /api/*, OnlyOffice-пути (версионированные /X.Y.Z-hash/... и статика),
// /drawio/* (iframe self-hosted draw.io), /preview/* (iframe dev-сервера проекта) и
// /forgejo/* (веб-UI git-сервера через YARP) не должны перехватываться SW — иначе
// навигация получит index.html приложения вместо самого редактора/сервера.
registerRoute(new NavigationRoute(createHandlerBoundToURL('index.html'), {
  denylist: [/^\/api\//, /^\/\d/, /^\/web-apps\//, /^\/sdkjs\//, /^\/doceditor\//, /^\/doc\//, /^\/coauthoring\//, /^\/cache\//, /^\/drawio\//, /^\/preview\//, /^\/forgejo(\/|$)/],
}));

// === Web push ===

interface PushPayload {
  title?: string;
  body?: string;
  url?: string;
  kind?: string;
  tag?: string;
  icon?: string;       // абсолютный URL аватара персоны (фото) с access_token; иначе лого
  renotify?: boolean;  // повторно привлечь внимание при замене уведомления с тем же tag
}

self.addEventListener('push', e => {
  if (!e.data) return;
  let payload: PushPayload = {};
  try { payload = e.data.json() as PushPayload; }
  catch { payload = { body: e.data.text() }; }

  // renotify не описан в lib.dom NotificationOptions, но поддерживается браузерами
  const options: NotificationOptions & { renotify?: boolean } = {
    body: payload.body ?? '',
    tag: payload.tag,           // одинаковый tag → уведомление заменяется, а не дублируется
    renotify: payload.renotify, // при замене — снова просигналить (иначе тихо подменяется)
    icon: payload.icon || '/pwa-192x192.png',   // фото персоны или лого приложения
    badge: '/pwa-64x64.png',
    data: { url: payload.url },
  };
  e.waitUntil(self.registration.showNotification(payload.title ?? 'AI Home', options));
});

// Клик по уведомлению: фокусируем открытое окно приложения (с переходом по диплинку)
// или открываем новое
self.addEventListener('notificationclick', e => {
  e.notification.close();
  const raw = e.notification.data?.url ?? '/';
  // URL от бэка: /chats/{id} или /notes/{id}. Превращаем в абсолютный
  // hash-URL для SPA: https://naychenko.me/#/chats/{id}
  const baseUrl = self.location.origin.replace(/\/+$/, '');
  const url = raw.startsWith('/') ? baseUrl + '/#' + raw.slice(1) : raw;
  e.waitUntil((async () => {
    const clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of clientList) {
      if ('focus' in client) {
        await client.focus();
        // navigate с полным абсолютным URL корректно меняет хеш
        if ('navigate' in client) await (client as WindowClient).navigate(url);
        return;
      }
    }
    await self.clients.openWindow(url);
  })());
});
