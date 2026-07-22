// Общие хелперы секций артефактов (вынесены из ArtifactsPanel при разбиении на секции).

export function basename(p: string): string {
  const norm = p.replace(/\\/g, '/');
  return norm.slice(norm.lastIndexOf('/') + 1);
}

export function dirname(p: string): string {
  const norm = p.replace(/\\/g, '/');
  const i = norm.lastIndexOf('/');
  return i > 0 ? norm.slice(0, i) : '';
}

// Имя инструмента для мини-ленты: MCP → «server · tool», остальные — как есть
export function callName(name: string): string {
  return name.startsWith('mcp__') ? name.slice(5).replace(/__/g, ' · ') : name;
}
