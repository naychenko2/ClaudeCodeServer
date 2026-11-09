import { api } from './api';
import { agentStreamUrl } from './deviceAgent';
import { basename } from './paths';
import { readStoredToken } from './offline';

// Скачать файл проекта на устройство — общая точка для просмотрщика и дерева файлов.
// Сервер и ретранслятор отдают поток same-origin: ссылка с атрибутом download пишет его
// браузером прямо на диск, без буфера в памяти вкладки (большие файлы не вешают её).
// У локального проекта поток идёт с агента (другой origin, download игнорируется) —
// там байты забираем fetch'ем и отдаём blob'ом.
export async function downloadProjectFile(projectId: string, path: string, viaAgent: boolean): Promise<void> {
  const name = basename(path) || path;
  if (!viaAgent) {
    clickDownload(api.files.fileUrl(projectId, path), name);
    return;
  }
  const r = await fetch(await agentStreamUrl(projectId, path));
  if (!r.ok) throw new Error(`Не удалось скачать файл (код ${r.status})`);
  const url = URL.createObjectURL(await r.blob());
  // Отзыв — с задержкой: Safari и старый Firefox срывают загрузку blob, отозванного сразу после click
  try { clickDownload(url, name); } finally { setTimeout(() => URL.revokeObjectURL(url), 10_000); }
}

// Скачать папку серверного проекта zip-архивом (GET files/zip). Только для серверного
// маршрута: у локального проекта файлы на устройстве, а агент архивов не собирает.
// Токен — через access_token, как у потока: ссылка заголовков не шлёт
export function downloadProjectFolder(projectId: string, path: string) {
  const params = new URLSearchParams({ path });
  const token = readStoredToken();
  if (token) params.set('access_token', token);
  clickDownload(`/api/projects/${encodeURIComponent(projectId)}/files/zip?${params}`, '');
}

function clickDownload(href: string, name: string) {
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  a.click();
}
