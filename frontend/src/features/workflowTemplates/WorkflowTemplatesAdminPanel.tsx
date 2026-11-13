// Админский раздел «Процессы фичи» (C1b): глобальные шаблоны рядом со специальностями.
// Правит только admin; MCP для сужения — из личного реестра владельца (уже выданные).
import { useEffect, useState } from 'react';
import { api } from '../../lib/api';
import { CONTENT_MAX_W, SP } from '../../lib/design';
import { useMe } from '../../lib/defaultPersona';
import { WorkflowTemplatesEditor } from './WorkflowTemplatesEditor';

export function WorkflowTemplatesAdminPanel(): React.ReactElement {
  const me = useMe();
  const isAdmin = me.role === 'admin';
  const [mcpKeys, setMcpKeys] = useState<string[]>([]);
  useEffect(() => {
    let cancelled = false;
    api.mcp.list()
      .then(list => { if (!cancelled) setMcpKeys(list.map(s => s.key)); })
      .catch(() => { /* реестр недоступен — без выбора MCP */ });
    return () => { cancelled = true; };
  }, []);
  return (
    <div style={{ maxWidth: CONTENT_MAX_W, margin: '0 auto', boxSizing: 'border-box', padding: SP.lg }}>
      <WorkflowTemplatesEditor canWrite={isAdmin} isAdmin={isAdmin} mcpChoices={mcpKeys} />
    </div>
  );
}
