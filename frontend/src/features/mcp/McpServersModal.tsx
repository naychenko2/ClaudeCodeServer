import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ConfirmDialog, Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W, R } from '../../lib/design';
import { useMcpData, plural } from './useMcpData';
import { McpServerList } from './McpServerList';
import { McpServerForm } from './McpServerForm';
import { McpAccessTab } from './McpAccessTab';
import { McpDiagnosticsTab } from './McpDiagnosticsTab';
import type { McpServer } from '../../types';

// Раздел «MCP-серверы» — модалка из меню аватара, рядом с «Поставщиками моделей»:
// настроечная поверхность низкой частоты без навигируемого содержимого. Раскладка
// (полоса вкладок, ширина MODAL_W.wide) повторяет сестринский диалог — два соседних
// пункта меню не должны выглядеть как из разных продуктов.

type TabKey = 'servers' | 'add' | 'access' | 'diag';

export function McpServersModal({ onClose, isAdmin }: { onClose: () => void; isAdmin: boolean }) {
  const data = useMcpData();
  const [tab, setTab] = useState<TabKey>('servers');
  // Правка существующей записи открывается на вкладке «Добавить» той же формой
  const [editing, setEditing] = useState<McpServer | null>(null);
  const [pendingDelete, setPendingDelete] = useState<McpServer | null>(null);

  const openAdd = () => { setEditing(null); setTab('add'); };
  const openEdit = (server: McpServer) => { setEditing(server); setTab('add'); };

  const tabs: { key: TabKey; label: string; count?: number; admin?: boolean }[] = [
    { key: 'servers', label: 'Серверы', count: data.servers?.length || undefined },
    { key: 'add', label: editing ? 'Правка' : 'Добавить' },
    { key: 'access', label: 'Доступ' },
    ...(isAdmin ? [{ key: 'diag' as TabKey, label: 'Диагностика', admin: true }] : []),
  ];

  const tabBtnStyle = (active: boolean): CSSProperties => ({
    font: 'inherit', fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600,
    color: active ? C.accent : C.textSecondary, background: 'transparent',
    border: 'none', borderBottom: `2px solid ${active ? C.accent : 'transparent'}`,
    padding: '10px 12px', cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
    display: 'flex', alignItems: 'center', gap: 5,
  });

  const personasOff = pendingDelete ? data.personasOffCount(pendingDelete.key) : 0;

  return (
    <>
      <Modal
        title="MCP-серверы"
        subtitle="Внешние инструменты, которыми пользуются чаты и персоны: что подключено, живо ли оно и кому доступно."
        width={MODAL_W.wide}
        onClose={onClose}
      >
        <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div style={{
            display: 'flex', gap: 2, borderBottom: `1px solid ${C.borderLight}`,
            overflowX: 'auto', flexShrink: 0, margin: '0 -4px',
          }}>
            {tabs.map(t => (
              <button
                key={t.key}
                type="button"
                style={tabBtnStyle(tab === t.key)}
                onClick={() => { if (t.key !== 'add') setEditing(null); setTab(t.key); }}
              >
                {t.label}
                {t.count != null && (
                  <span style={{
                    fontSize: 10.5, fontWeight: 700,
                    color: tab === t.key ? C.accent : C.textMuted,
                  }}>{t.count}</span>
                )}
                {t.admin && (
                  <span style={{
                    fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: 9,
                    background: C.accentLight, color: C.accent,
                  }}>админ</span>
                )}
              </button>
            ))}
          </div>

          {data.error && (
            <div style={{
              margin: '10px 0 0', padding: '7px 10px', borderRadius: R.md, fontSize: FS.sm,
              color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
            }}>{data.error}</div>
          )}

          <div style={{ paddingTop: 12, display: 'flex', flexDirection: 'column', gap: 8 }}>
            {tab === 'servers' && (
              <McpServerList
                data={data}
                onAdd={openAdd}
                onEdit={openEdit}
                onOpenAccess={() => setTab('access')}
                onDelete={setPendingDelete}
              />
            )}
            {tab === 'add' && (
              <McpServerForm
                key={editing?.id ?? 'new'}
                data={data}
                server={editing}
                onDone={() => { setEditing(null); setTab('servers'); }}
                onCancel={() => { setEditing(null); setTab('servers'); }}
              />
            )}
            {tab === 'access' && <McpAccessTab data={data} onClose={onClose} onAdd={openAdd} onEdit={openEdit} />}
            {tab === 'diag' && isAdmin && <McpDiagnosticsTab />}
          </div>
        </div>
      </Modal>

      {pendingDelete && (
        <ConfirmDialog
          title={`Удалить «${pendingDelete.label || pendingDelete.key}»?`}
          subtitle={`Сервер отключится ${personasOff > 0
            ? `у ${personasOff} ${plural(personasOff, 'персоны', 'персон', 'персон')} и `
            : ''}во всех проектах. Секретные значения будут стёрты — чтобы подключить его снова, ключи придётся ввести заново.`}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={async () => {
            try { await data.remove(pendingDelete); }
            catch (e) { data.setError(e instanceof Error && e.message ? e.message : 'Не удалось удалить'); }
            setPendingDelete(null);
          }}
          onCancel={() => setPendingDelete(null)}
        />
      )}
    </>
  );
}
