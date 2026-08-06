// Клиентское правило «какая ссылка открывается в панели „Чтение"» — ADR-005 §2/§3,
// ADR-006 §5. Фильтр ОБЩИЙ для обоих режимов (iframe и MD) и для обеих точек входа:
// клика по самой ссылке в ленте и кнопки-компаньона — второго списка не заводим.
// Это UX-фильтр (спрятать заведомо бесполезную/локальную ссылку), а не защита: реальный
// SSRF-периметр — на сервере (SsrfGuard + перепроверка на каждом хопе редиректа). Всё,
// что здесь отсекается, ведёт себя как раньше: клик уводит в новую вкладку браузера.

// Хосты, для которых сервер точно откажет local-address (ADR §2 п.1) — кнопку не рисуем,
// это чисто локальные адреса разработки/сети, читать их «рядом» нечем
const LOCAL_HOST_RE = /^(localhost|.*\.localhost|.*\.local|.*\.internal|home\.arpa)$/i;
// IPv4-литерал хоста (включая приватные/loopback — сервер их всё равно отсечёт, но кнопку
// незачем предлагать заранее) и голый IPv6-литерал в скобках
const IPV4_RE = /^\d{1,3}(\.\d{1,3}){3}$/;
const IPV6_LITERAL_RE = /^\[.*]$/;

// Чёрный список расширений (ADR §3): архивы, установщики, медиа, изображения, офисные
// и бинарные форматы — у них нет читаемого текста статьи. Белый список расширений
// не заводим: у подавляющего большинства настоящих статей расширения в пути нет вообще.
const BLOCKED_EXT = [
  '.zip', '.tar', '.gz', '.tgz', '.7z', '.rar',
  '.exe', '.msi', '.dmg', '.pkg', '.deb', '.rpm', '.apk',
  '.mp4', '.mkv', '.mov', '.avi', '.webm', '.mp3', '.wav', '.ogg', '.flac',
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg', '.ico', '.bmp', '.avif',
  '.xlsx', '.xls', '.docx', '.doc', '.pptx', '.ppt', '.iso', '.jar', '.whl',
];

function extensionOf(pathname: string): string {
  const slash = pathname.lastIndexOf('/');
  const name = slash >= 0 ? pathname.slice(slash + 1) : pathname;
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot).toLowerCase() : '';
}

// Домен для aria-label/подписи, если ссылка годится под кнопку-компаньон; null — не годится
// (не http(s), локальный/IP-адрес хоста, расширение из чёрного списка) — фича молчит,
// ссылка ведёт себя как обычно.
export function readerEligibleDomain(href: string): string | null {
  let url: URL;
  try { url = new URL(href); } catch { return null; }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
  const host = url.hostname;
  // url.hostname отдаёт IPv6-литерал уже в скобках ("[::1]")
  if (LOCAL_HOST_RE.test(host) || IPV4_RE.test(host) || IPV6_LITERAL_RE.test(host)) return null;
  const ext = extensionOf(url.pathname);
  if (ext && !['.html', '.htm', '.md', '.txt'].includes(ext) && BLOCKED_EXT.includes(ext)) return null;
  return host;
}
