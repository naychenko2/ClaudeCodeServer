import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { ConfirmDialog, Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W, R } from '../../lib/design';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { useMcpData, plural } from './useMcpData';
import { McpServerList } from './McpServerList';
import { McpServerForm } from './McpServerForm';
import { McpAccessTab } from './McpAccessTab';
import { McpDiagnosticsTab } from './McpDiagnosticsTab';
import { McpCatalogPanel } from './McpCatalogPanel';
import type { McpCatalogServer, McpServer, McpServerCatalogDraft } from '../../types';

// Раздел «MCP-серверы» — модалка из меню аватара, рядом с «Поставщиками моделей»:
// настроечная поверхность низкой частоты без навигируемого содержимого. Раскладка
// (полоса вкладок, ширина MODAL_W.wide) повторяет сестринский диалог — два соседних
// пункта меню не должны выглядеть как из разных продуктов.

type TabKey = 'servers' | 'add' | 'catalog' | 'access' | 'diag';

export function McpServersModal({ onClose, isAdmin }: { onClose: () => void; isAdmin: boolean }) {
  const data = useMcpData();
  // Каталог MCP-серверов (волна 1, задача 9fa075ec) — за фич-флагом, чтобы кнопка
  // «Найти сервер» не светилась у тех, кому каталог ещё не включили. На бэке
  // соответствующий ключ в FeatureFlagCatalog.All (заводит Денис)
  const catalogOn = useFeature(FLAGS.mcpCatalog);
  const [tab, setTab] = useState<TabKey>('servers');
  // Правка существующей записи открывается на вкладке «Добавить» той же формой
  const [editing, setEditing] = useState<McpServer | null>(null);
  // Черновик из каталога: открывает ту же форму с предзаполнением, но фиксирует
  // режим и кладёт CatalogRef в Save
  const [catalogDraft, setCatalogDraft] = useState<McpServerCatalogDraft | null>(null);
  const [pendingDelete, setPendingDelete] = useState<McpServer | null>(null);

  const openAdd = () => { setEditing(null); setCatalogDraft(null); setTab('add'); };
  const openEdit = (server: McpServer) => { setEditing(server); setCatalogDraft(null); setTab('add'); };
  // Карточка каталога → форма с предзаполнением. source берётся целиком, чтобы
  // форма при желании показала превью строки запуска; поля черновика лежат в
  // prefill.fields (DTO McpCatalogPrefillDto) — на верхнем уровне их нет, иначе
  // любое нажатие «Настроить подключение» падало бы с TypeError на undefined.map
  const openCatalogDraft = (source: McpCatalogServer) => {
    setCatalogDraft({
      source,
      catalogRef: {
        name: source.name,
        version: source.version ?? '',
        publishedAt: source.publishedAt ?? null,
      },
      // Поля черновика: фильтр target='args' пойдёт в argv, target='header' — в
      // headers, target='env'/'url' — в env. У отказанных записей prefill=null,
      // тогда форма просто не получит полей (Connectable=false и так блокирует клик)
      fieldsDraft: source.prefill?.fields ?? [],
    });
    setEditing(null);
    setTab('add');
  };

  const tabs: { key: TabKey; label: string; count?: number; admin?: boolean }[] = [
    { key: 'servers', label: 'Серверы', count: data.servers?.length || undefined },
    { key: 'add', label: editing ? 'Правка' : (catalogDraft ? 'Каталог' : 'Добавить') },
    ...(catalogOn ? [{ key: 'catalog' as TabKey, label: 'Каталог' }] : []),
    { key: 'access', label: 'Доступ' },
    ...(isAdmin ? [{ key: 'diag' as TabKey, label: 'Диагностика', admin: true }] : []),
  ];

  // Реестровые имена уже подключённых каталожных серверов. По плану §4: «этот
  // сервер уже подключён» — по CatalogRef.name из DTO. Используем Set: на каждой
  // карточке каталога идёт .has() по имени, иначе было бы O(n×m) на каждое
  // открытие вкладки
  const installedCatalogNames = useMemo(() => {
    const s = new Set<string>();
    for (const srv of data.servers ?? []) {
      if (srv.catalogRef?.name) s.add(srv.catalogRef.name);
    }
    return s;
  }, [data.servers]);

  const tabBtnStyle = (active: boolean): CSSProperties => ({
    font: 'inherit', fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600,
    color: active ? C.accent : C.textSecondary, background: 'transparent',
    border: 'none', borderBottom: `2px solid ${active ? C.accent : 'transparent'}`,
    padding: '10px 12px', cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
    display: 'flex', alignItems: 'center', gap: 5,
  });

  const personasOn = pendingDelete ? data.personasOnCount(pendingDelete.key) : 0;

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
                onClick={() => {
                  // Переключение вкладки сбрасывает «черновик из каталога» и редактируемую запись
                  if (t.key !== 'add') { setEditing(null); setCatalogDraft(null); }
                  setTab(t.key);
                }}
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
                onCatalog={catalogOn ? () => setTab('catalog') : undefined}
                onOpenAccess={() => setTab('access')}
                onDelete={setPendingDelete}
              />
            )}
            {tab === 'add' && (
              <McpServerForm
                key={(editing?.id ?? 'new') + (catalogDraft?.catalogRef.name ?? '')}
                data={data}
                server={editing}
                catalogDraft={catalogDraft}
                onDone={() => { setEditing(null); setCatalogDraft(null); setTab('servers'); }}
                onCancel={() => { setEditing(null); setCatalogDraft(null); setTab('servers'); }}
              />
            )}
            {tab === 'catalog' && catalogOn && (
              <McpCatalogPanel
                installedNames={installedCatalogNames}
                onPick={openCatalogDraft}
                onManual={openAdd}
                onClose={() => setTab('servers')}
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
          subtitle={`Сервер отключится ${personasOn > 0
            ? `у ${personasOn} ${plural(personasOn, 'персоны', 'персон', 'персон')} и `
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
