// Маршруты файлов, git, сервисов и навыков, которые умеет localhost-API агента устройства (ADR-016, задача 4.4).
// Зеркало DeviceAgentRoutes из backend/ClaudeHomeServer.Core/Protocol/ProjectFilesApiContract.cs:
// рассинхрон ловит контракт-тест deviceAgentRoutes.contract.test.ts. Это ЕДИНСТВЕННЫЙ список
// на фронте — компоненты спрашивают agentSupports(), а не держат свои перечни.
//
// Шаблон — путь относительно api/projects/{projectId}/, параметры в фигурных скобках
// ({sha}, {index:int}) совпадают с любым одним сегментом.

export const DEVICE_AGENT_SHARED = [
  'GET files',
  'GET files/tree',
  'GET files/search',
  'GET files/content',
  'PUT files/content',
  'GET files/diff',
  'POST files/revert',
  'POST files/create',
  'POST files/mkdir',
  'POST files/rename',
  'DELETE files',
  'GET files/stream',

  'GET git/status',
  'GET git/diff',
  'GET git/log',
  'GET git/branches',
  'POST git/stage',
  'POST git/unstage',
  'POST git/discard',
  'POST git/commit',

  // Сервисы проекта и превью
  'GET services',
  'GET preview/status',
  'POST preview/start',
  'POST preview/stop',
  'POST preview/stop-external',
  'POST preview/active',
  'POST preview/active-external',
  'GET launch-config',
  'PUT launch-config',

  // Навыки и агенты проекта
  'GET skills',
  'GET agents/{agentName}',
  'PUT agents/{agentName}',
  'POST agents',
] as const;

// Маршруты, которых у сервера нет вовсе: билеты для тегов без заголовков и вложения чата.
// Зеркало DeviceAgentRoutes.AgentOnly
export const DEVICE_AGENT_ONLY = [
  'POST agent/stream-ticket',
  'POST agent/hub-ticket',
  'POST agent/preview-ticket',
  'POST agent/attachments',
] as const;

export const DEVICE_AGENT_UNSUPPORTED = [
  'POST files/upload',
  'POST files/save-from-url',
  'POST files/changed-by',
  'GET files/office-download',
  'GET files/office-config',
  'POST files/office-callback',
  'POST files/office-discard',
  'POST files/office-force-save',
  'GET files/office-version',
  'POST files/document/convert',
  'POST files/document/summary',
  'POST files/document/extract',
  'POST files/document/to-markdown',
  'POST files/document/tags',

  'GET git/unpushed',
  'GET git/commits/{sha}',
  'GET git/commits/{sha}/diff',
  'GET git/commits/{sha}/file',
  'POST git/commits/{sha}/restore-file',
  'POST git/commits/{sha}/revert',
  'GET git/file-log',
  'GET git/blame',
  'POST git/stage-all',
  'POST git/discard-all',
  'POST git/stage-hunk',
  'POST git/unstage-hunk',
  'POST git/save-now',
  'GET git/stash',
  'GET git/stash/{index:int}',
  'POST git/stash',
  'POST git/stash/{index:int}/pop',
  'DELETE git/stash/{index:int}',
  'POST git/checkout',
  'POST git/branches',
  'POST git/fetch',
  'POST git/pull',
  'POST git/push',
  'POST git/sync',
  'POST git/init',
  'GET git/remote',
  'POST git/remote',
  'POST git/remote/server',
  'GET git/forgejo-credentials',
  'POST git/forgejo-credentials/reset',
  'PUT git/auto-commit',
  'POST git/ai/commit-message',
  'POST git/ai/detect-commit-style',
  'POST git/ai/stash-name',
  'GET git/commit-prompt',
  'PUT git/commit-prompt',

  // Внешний доступ к дев-серверу — поддомен сервера, с машины проекта его нет по смыслу
  'POST preview/external-link',
] as const;

// Маршруты ретранслятора чтения (ADR-016 §5, задача 5.2): проект открыт не с его машины, запрос идёт
// на сервер под api/projects/{id}/relay/…, дальше — агенту устройства. Зеркало RelayProtocol.Routes из
// backend/ClaudeHomeServer.Core/Protocol/RelayProtocol.cs (контракт-тест тот же). Записи здесь нет
// по построению — и этот список ЕДИНСТВЕННЫЙ источник того, что панель прячет с другого устройства:
// контрол, маршрута которого тут нет, не рисуется
export const RELAY_ROUTES = [
  'GET files',
  'GET files/tree',
  'GET files/content',
  'GET files/stream',
  'GET files/stat',
  'GET files/search',
  'GET files/diff',
  'GET git/status',
  'GET git/diff',
  'GET git/log',
  'GET git/commits/{sha}',
  'GET git/commits/{sha}/diff',
  'GET git/commits/{sha}/file',
] as const;

export type DeviceAgentRoute = (typeof DEVICE_AGENT_SHARED)[number] | (typeof DEVICE_AGENT_UNSUPPORTED)[number];
// Любой маршрут файлов/git проекта, о котором панель спрашивает «есть ли он сейчас»
export type ProjectRoute = DeviceAgentRoute | (typeof RELAY_ROUTES)[number];

function templateRegex(route: string): RegExp {
  const [method, template] = route.split(' ');
  const body = template.split('/')
    .map(seg => (seg.startsWith('{') ? '[^/]+' : seg.replace(/[.*+?^$()|[\]\\]/g, '\\$&')))
    .join('/');
  return new RegExp(`^${method} ${body}$`);
}

const SHARED_RE = DEVICE_AGENT_SHARED.map(templateRegex);

// Есть ли у агента конкретный запрос: метод и путь относительно api/projects/{id}/ без query.
// Незнакомый маршрут — «нет»: новый серверный маршрут без решения по агенту не уедет в агента
// и не упадёт там невнятным 404.
export function agentServesPath(method: string, path: string): boolean {
  const key = `${method.toUpperCase()} ${path.replace(/^\/+|\/+$/g, '')}`;
  return SHARED_RE.some(re => re.test(key));
}

// Есть ли у агента маршрут по его шаблону из списков выше — для гейта кнопок в панелях.
export function agentSupports(route: ProjectRoute): boolean {
  return (DEVICE_AGENT_SHARED as readonly string[]).includes(route);
}

const RELAY_RE = RELAY_ROUTES.map(templateRegex);

// Есть ли у ретранслятора конкретный запрос (метод и путь без query). Незнакомое — «нет»
export function relayServesPath(method: string, path: string): boolean {
  const key = `${method.toUpperCase()} ${path.replace(/^\/+|\/+$/g, '')}`;
  return RELAY_RE.some(re => re.test(key));
}

export function relaySupports(route: ProjectRoute): boolean {
  return (RELAY_ROUTES as readonly string[]).includes(route);
}
