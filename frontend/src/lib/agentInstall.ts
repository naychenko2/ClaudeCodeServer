// Установка агента устройства одной командой и разрешение папки проекта (agent-distribution
// AD-7, решение Р10). Строку установки собирает веб-морда из window.location.origin, а не
// сервер: скрипты статичны, и подделать адрес заголовком Host нельзя.

export type AgentOs = 'windows' | 'linux';

// Канал, по которому можно раздавать установку агента: https, либо http на петле (дев-стенд).
// По смыслу совпадает с DeviceChannelGuard.IsSecure на сервере, ServerChannel в агенте и
// проверкой --server в скриптах установки: по открытому http из сети атакующий подменит
// скрипт или архив, и чужой бинарь выполнится раньше, чем сервер откажет в сопряжении
export const INSECURE_AGENT_INSTALL_TEXT =
  'Установка агента доступна только по HTTPS — откройте веб-интерфейс по https-адресу';

export function isSecureAgentOrigin(origin: string): boolean {
  let url: URL;
  try { url = new URL(origin); } catch { return false; }
  if (url.protocol === 'https:') return true;
  return url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
}

// Синтаксис аргументов сверен с deploy/agent-install/install.ps1 (-Server/-Code) и install.sh
// (--server/--code). Код — из алфавита без пробелов и кавычек, адрес — origin без хвоста
export function agentInstallCommand(os: AgentOs, origin: string, code: string): string {
  const server = origin.replace(/\/+$/, '');
  return os === 'windows'
    ? `& ([scriptblock]::Create((irm ${server}/agent/install.ps1))) -Server ${server} -Code ${code}`
    : `curl -fsSL ${server}/agent/install.sh | sh -s -- --server ${server} --code ${code}`;
}

// Вкладка по умолчанию — ОС браузера: чаще всего подключают тот компьютер, с которого смотрят
export function guessAgentOs(userAgent: string = typeof navigator !== 'undefined' ? navigator.userAgent : ''): AgentOs {
  return /Linux|X11/i.test(userAgent) && !/Android/i.test(userAgent) ? 'linux' : 'windows';
}

export type AgentManifestState =
  | { kind: 'loading' }
  | { kind: 'served'; version: string }
  // 503 или сбой: сервер не раздаёт агента, команду показывать бессмысленно
  | { kind: 'unavailable'; reason: string };

// Манифест анонимный и лежит вне /api — обычный fetch, без JWT
export async function fetchAgentManifest(): Promise<AgentManifestState> {
  try {
    const res = await fetch('/agent/manifest.json', { cache: 'no-store' });
    const body = await res.json().catch(() => null) as { version?: string; error?: string } | null;
    if (res.ok && body?.version) return { kind: 'served', version: body.version };
    return { kind: 'unavailable', reason: body?.error ?? `сервер ответил ${res.status}` };
  } catch {
    return { kind: 'unavailable', reason: 'сервер не ответил' };
  }
}

// Отказ агента «папка не под разрешёнными корнями» (AgentPathRefusedException в
// AgentPathPolicy): отдельного кода нет, узнаём по тексту причины
export function isRootNotAllowed(text: string | null | undefined): boolean {
  return !!text && /разрешённ\S*\s+корн|AgentPathRefused|roots add/i.test(text);
}

// Команда разрешения папки на машине проекта. Разрешает только человек на самой машине
// (модель угроз device-agent-local-api.md) — сервер лишь подсказывает строку.
// Кавычки двойные на обеих платформах; внутри экранируем то, что раскроет оболочка:
// в PowerShell — `$ и `` ` ``, в sh — \ " $ `
export function rootsAddCommand(path: string, platform: string | null | undefined): string {
  // Платформа неизвестна (старый агент) — узнаём Windows по букве диска
  const windows = platform ? platform === 'windows' : /^[A-Za-z]:[\\/]|^\\\\/.test(path);
  const quoted = windows
    ? path.replace(/[`$"]/g, m => '`' + m)
    : path.replace(/[\\"$`]/g, m => '\\' + m);
  return `ai-home-agent roots add "${quoted}"`;
}
