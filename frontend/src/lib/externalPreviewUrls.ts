// Адреса выданных ссылок внешнего доступа — на клиенте, в sessionStorage.
//
// Почему их вообще приходится хранить: сервер адрес НЕ помнит. Токен живёт в самой ссылке,
// в реестре лежит только его идентификатор — то есть повторно тот же адрес не выдать.
// А центральной панели он нужен после каждой перезагрузки страницы, иначе она снова
// показывала бы сайт через путь-префикс, ради ухода от которого всё и делалось.
//
// sessionStorage, а не localStorage: ссылка умирает вместе с вкладкой, как и положено
// временному доступу наружу. И не «выдавать заново каждый раз» — так ссылки копились бы
// пачками и вытесняли друг друга по потолку.

const key = (projectId: string, serviceId: string) => `cc_extpreview_url_${projectId}_${serviceId}`;

export function saveExternalUrl(projectId: string, serviceId: string, url: string): void {
  try { sessionStorage.setItem(key(projectId, serviceId), url); } catch { /* приватный режим — переживём */ }
}

export function getExternalUrl(projectId: string, serviceId: string): string | null {
  try { return sessionStorage.getItem(key(projectId, serviceId)); } catch { return null; }
}

export function clearExternalUrl(projectId: string, serviceId: string): void {
  try { sessionStorage.removeItem(key(projectId, serviceId)); } catch { /* ignore */ }
}

/// Забыть все адреса — при «закрыть все» доступа больше нет ни у одного сервиса.
export function clearAllExternalUrls(): void {
  try {
    const dead = Object.keys(sessionStorage).filter(k => k.startsWith('cc_extpreview_url_'));
    dead.forEach(k => sessionStorage.removeItem(k));
  } catch { /* ignore */ }
}
