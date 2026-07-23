// MCP-сервер виджетов ClaudeHomeServer: stdio, JSON-RPC (newline-delimited),
// без внешних зависимостей — деплой не требует npm install.
//
// Единственный инструмент widget_show НЕ ходит в API и не требует окружения:
// он лишь валидирует input и возвращает подтверждение. Сам HTML рендерит фронт
// (WidgetView) из input вызова — sandbox-iframe в ленте чата.

import { createInterface } from 'node:readline';

// Лимит размера html: учит модель ретраиться компактнее. Input уже улетел в историю
// до валидации — от первого раздутого вызова историю лимит не спасает (фронт имеет
// свой защитный cap на рендер).
const MAX_HTML = 64 * 1024;
const MAX_TITLE = 120;
const MIN_HEIGHT = 120;
const MAX_HEIGHT = 1200;

const TOOLS = [
  {
    name: 'widget_show',
    description:
      'Показать пользователю интерактивный HTML-виджет прямо в ленте чата: дашборд, график, ' +
      'таблицу, калькулятор, мини-игру. HTML должен быть self-contained: все стили и скрипты ' +
      'inline, внешние ресурсы (CDN, картинки по URL, шрифты, fetch) заблокированы песочницей. ' +
      'Виджет отображается сразу — не дублируй его содержимое текстом.',
    inputSchema: {
      type: 'object',
      required: ['html'],
      properties: {
        html: {
          type: 'string',
          description:
            'Self-contained HTML-фрагмент (inline CSS/JS, без внешних ресурсов и без <html>/<head>/<body>). Лимит 64 КБ.',
        },
        title: {
          type: 'string',
          description: 'Короткий заголовок карточки виджета (до 120 символов)',
        },
        height: {
          type: 'integer',
          minimum: MIN_HEIGHT,
          maximum: MAX_HEIGHT,
          description: 'Желаемая высота в px (опционально; иначе подстроится автоматически)',
        },
      },
    },
  },
];

function json(text, isError = false) {
  return { content: [{ type: 'text', text }], ...(isError ? { isError: true } : {}) };
}

function callTool(name, args) {
  if (name !== 'widget_show') throw new Error(`Неизвестный инструмент: ${name}`);

  const html = typeof args.html === 'string' ? args.html : '';
  if (!html.trim())
    return json('Поле html пустое — передай self-contained HTML-фрагмент виджета.', true);
  if (html.length > MAX_HTML) {
    const kb = Math.round(html.length / 1024);
    return json(
      `HTML виджета слишком большой (${kb} КБ, лимит ${MAX_HTML / 1024} КБ) — упрости разметку или сократи данные.`,
      true,
    );
  }

  const title = typeof args.title === 'string' ? args.title.slice(0, MAX_TITLE).trim() : '';
  return json(
    `Виджет ${title ? `«${title}» ` : ''}показан пользователю в ленте чата. ` +
      'НЕ дублируй его содержимое текстом — при необходимости добавь 1-2 предложения комментария.',
  );
}

// --- JSON-RPC поверх stdio (newline-delimited) ---

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n');
}
function reply(id, result) {
  send({ jsonrpc: '2.0', id, result });
}
function replyError(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

const rl = createInterface({ input: process.stdin, terminal: false });

rl.on('line', line => {
  line = line.trim();
  if (!line) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }

  const { id, method, params } = msg;
  if (id === undefined || id === null) return;

  try {
    switch (method) {
      case 'initialize':
        reply(id, {
          protocolVersion: params?.protocolVersion ?? '2024-11-05',
          capabilities: { tools: {} },
          serverInfo: { name: 'widgets', version: '1.0.0' },
        });
        break;
      case 'tools/list':
        reply(id, { tools: TOOLS });
        break;
      case 'tools/call': {
        try {
          reply(id, callTool(params.name, params.arguments ?? {}));
        } catch (err) {
          reply(id, {
            content: [{ type: 'text', text: `Ошибка: ${err?.message ?? err}` }],
            isError: true,
          });
        }
        break;
      }
      case 'ping':
        reply(id, {});
        break;
      default:
        replyError(id, -32601, `Метод не поддерживается: ${method}`);
    }
  } catch (err) {
    replyError(id, -32603, String(err?.message ?? err));
  }
});
