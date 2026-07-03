// Web push: подписка/отписка текущего устройства (браузера) на уведомления.
// Требует HTTPS (или localhost) и зарегистрированный service worker (vite-plugin-pwa).

import { api } from './api';

export function isPushSupported(): boolean {
  return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

// applicationServerKey должен быть Uint8Array из base64url VAPID-ключа
function urlBase64ToUint8Array(base64Url: string): Uint8Array {
  const padding = '='.repeat((4 - (base64Url.length % 4)) % 4);
  const base64 = (base64Url + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(base64);
  return Uint8Array.from(raw, c => c.charCodeAt(0));
}

async function getRegistration(): Promise<ServiceWorkerRegistration> {
  const reg = await navigator.serviceWorker.getRegistration();
  if (reg) return reg;
  return navigator.serviceWorker.ready;
}

/** Подписано ли текущее устройство. */
export async function isPushEnabled(): Promise<boolean> {
  if (!isPushSupported()) return false;
  try {
    const reg = await getRegistration();
    return !!(await reg.pushManager.getSubscription());
  } catch {
    return false;
  }
}

/** Запросить разрешение, подписаться и зарегистрировать подписку на сервере. */
export async function enablePush(): Promise<void> {
  if (!isPushSupported()) throw new Error('Push не поддерживается этим браузером');

  const permission = await Notification.requestPermission();
  if (permission !== 'granted') throw new Error('Уведомления запрещены в браузере');

  const { publicKey } = await api.push.vapidPublicKey();
  const reg = await getRegistration();
  const sub = await reg.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(publicKey) as BufferSource,
  });

  const json = sub.toJSON();
  if (!json.endpoint || !json.keys?.p256dh || !json.keys?.auth)
    throw new Error('Браузер вернул неполную подписку');
  await api.push.subscribe({ endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth });
}

/** Отписать устройство и удалить подписку на сервере. */
export async function disablePush(): Promise<void> {
  if (!isPushSupported()) return;
  const reg = await getRegistration();
  const sub = await reg.pushManager.getSubscription();
  if (!sub) return;
  const endpoint = sub.endpoint;
  await sub.unsubscribe();
  await api.push.unsubscribe(endpoint).catch(() => {});
}
