// Принудительный переход на свежую версию приложения.
//
// Обычный путь обновления — плашка UpdatePrompt: новый service worker скачивается, встаёт в
// очередь (registerType: 'prompt') и ждёт, пока пользователь нажмёт «Обновить». Проверка идёт
// по таймеру раз в минуту, поэтому после выкатки старый бандл может крутиться ещё какое-то
// время — а в нём нет ровно того, что только что выкатили.
//
// Здесь тот же переход, но по требованию и без хука: useRegisterSW уже висит в UpdatePrompt,
// второй регистрации приложению не нужно.
//
// Зачем ждать installed. registration.update() лишь ЗАПУСКАЕТ проверку: свежий воркер сперва
// попадает в installing и только потом в waiting. Дёрнув postMessage сразу, легко промахнуться
// мимо ещё не готового воркера — и остаться на старой версии, показав пользователю, что всё
// обновилось.
export async function applyUpdateAndReload(): Promise<void> {
  const reload = () => window.location.reload();

  if (!('serviceWorker' in navigator)) { reload(); return; }

  try {
    const registration = await navigator.serviceWorker.getRegistration();
    // В DEV service worker выключен вовсе — там обычная перезагрузка и есть обновление.
    if (!registration) { reload(); return; }

    await registration.update().catch(() => { /* сеть могла не ответить — попробуем что есть */ });

    const waiting = registration.waiting ?? await waitForInstalled(registration);
    // Нового воркера нет — значит фронт не менялся (выкатывали бэкенд), и обновляться не от
    // чего. Всё равно перезагружаемся: пользователь нажал кнопку и ждёт свежую страницу.
    if (!waiting) { reload(); return; }

    // Перезагружаемся, когда новый воркер реально возьмёт управление, а не сразу после
    // postMessage: иначе успеваем перезагрузиться под старым и увидеть ту же версию.
    navigator.serviceWorker.addEventListener('controllerchange', reload, { once: true });
    waiting.postMessage({ type: 'SKIP_WAITING' });
  } catch {
    reload();
  }
}

// Ждём, пока установится воркер, который уже качается. Без ограничения по времени такое
// ожидание умеет висеть вечно — лучше перезагрузиться, чем оставить кнопку в задумчивости.
function waitForInstalled(registration: ServiceWorkerRegistration): Promise<ServiceWorker | null> {
  const installing = registration.installing;
  if (!installing) return Promise.resolve(null);

  return new Promise(resolve => {
    const timer = setTimeout(() => resolve(registration.waiting), 10_000);
    installing.addEventListener('statechange', () => {
      if (installing.state === 'installed') {
        clearTimeout(timer);
        resolve(registration.waiting);
      }
    });
  });
}
