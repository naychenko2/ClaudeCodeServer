// Черновики поля ввода на каждый чат: недовведённый текст сохраняется per-session,
// поэтому при переключении между чатами он остаётся у своего чата, а не «переезжает».
// In-memory Map + write-through в sessionStorage (переживает перезагрузку вкладки).

const KEY = 'cc_chat_drafts';
const mem = new Map<string, string>();

// Загрузка сохранённых черновиков при старте модуля
try {
  const raw = sessionStorage.getItem(KEY);
  if (raw) {
    const obj = JSON.parse(raw) as Record<string, string>;
    for (const [k, v] of Object.entries(obj)) if (v) mem.set(k, v);
  }
} catch { /* noop */ }

function persist() {
  try {
    const obj: Record<string, string> = {};
    for (const [k, v] of mem) if (v) obj[k] = v;
    sessionStorage.setItem(KEY, JSON.stringify(obj));
  } catch { /* noop */ }
}

export function getDraft(sessionId: string): string {
  return mem.get(sessionId) ?? '';
}

export function setDraft(sessionId: string, text: string): void {
  if (text) mem.set(sessionId, text);
  else mem.delete(sessionId);
  persist();
}
