// Два локальных static-сервера без зависимостей: 8801 (origin A) и 8802 (origin B).
// Разные порты = разные origin — cross-origin iframe без внешней сети.
// Маршруты: /... — fixtures/browser-spike, /uia/... — fixtures/uia-spike (те же страницы, что в части 1).
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('.', import.meta.url));
const uiaRoot = fileURLToPath(new URL('../../uia-spike/fixtures/', import.meta.url));
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png' };

function handler(roots) {
  return async (req, res) => {
    try {
      const url = decodeURIComponent(new URL(req.url, 'http://x').pathname);
      let file = null;
      if (url.startsWith('/uia/')) file = join(uiaRoot, normalize(url.slice('/uia/'.length)));
      else if (url !== '/') file = join(root, normalize(url.slice(1)));
      if (!file || url.includes('..')) { res.writeHead(400).end('bad path'); return; }
      const data = await readFile(file);
      res.writeHead(200, { 'content-type': MIME[extname(file)] || 'application/octet-stream' });
      res.end(data);
    } catch {
      res.writeHead(404).end('not found');
    }
  };
}

for (const port of [8801, 8802]) {
  createServer(handler()).listen(port, '127.0.0.1', () => console.log(`origin ${port}: http://127.0.0.1:${port}/`));
}
