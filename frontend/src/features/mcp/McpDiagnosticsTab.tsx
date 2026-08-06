import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { relTime } from '../../lib/gitFormat';
import type { McpCallsResponse } from '../../types';

// Вкладка «Диагностика»: счётчики вызовов MCP-инструментов и последние сбои
// (GET /api/mcp/calls). Эндпоинт админский — данные охватывают всех владельцев,
// поэтому саму вкладку модалка не-админу не рисует. Счётчики живут в памяти
// процесса: рестарт сервера обнуляет таблицу — это диагностика, не аудит.

const thStyle: CSSProperties = {
  fontSize: 10.5, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase',
  letterSpacing: '0.05em', textAlign: 'left', padding: '8px 12px',
  borderBottom: `1px solid ${C.borderLight}`, whiteSpace: 'nowrap',
};

const tdStyle: CSSProperties = {
  padding: '8px 12px', borderTop: `1px solid ${C.borderLight}`, fontSize: FS.sm,
  verticalAlign: 'top',
};

const hintStyle: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px',
};

export function McpDiagnosticsTab() {
  const [data, setData] = useState<McpCallsResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.mcp.calls()
      .then(d => { if (!cancelled) setData(d); })
      .catch(e => {
        if (!cancelled) setError(e instanceof Error && e.message ? e.message : 'Не удалось загрузить счётчики');
      });
    return () => { cancelled = true; };
  }, []);

  if (error) {
    return (
      <div style={{
        padding: '7px 10px', borderRadius: R.md, fontSize: FS.sm,
        color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
      }}>{error}</div>
    );
  }
  if (!data) return <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>;

  // Последний сбой по каждому инструменту: список приходит свежими вперёд
  const lastFailure = new Map<string, McpCallsResponse['recentFailures'][number]>();
  for (const f of data.recentFailures) if (!lastFailure.has(f.tool)) lastFailure.set(f.tool, f);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={hintStyle}>
        Счётчики вызовов MCP-инструментов с момента запуска сервера и последние сбои.
        Вкладка видна только администратору — здесь данные всех владельцев.
      </div>
      {data.tools.length === 0 ? (
        <div style={hintStyle}>
          Вызовов пока не было: счётчики наполняются, как только инструменты позовут из хода.
        </div>
      ) : (
        <div style={{
          overflowX: 'auto', border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite,
        }}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 520 }}>
            <thead>
              <tr>
                <th style={thStyle}>Инструмент</th>
                <th style={{ ...thStyle, textAlign: 'right' }}>Вызовы</th>
                <th style={{ ...thStyle, textAlign: 'right' }}>Отказы</th>
                <th style={{ ...thStyle, textAlign: 'right' }}>Ср. время</th>
                <th style={thStyle}>Последний сбой</th>
              </tr>
            </thead>
            <tbody>
              {data.tools.map(tool => {
                const failure = lastFailure.get(tool.tool);
                return (
                  <tr key={tool.tool}>
                    <td style={{
                      ...tdStyle, fontFamily: FONT.mono, fontSize: 11.5,
                      color: C.textHeading, whiteSpace: 'nowrap',
                    }}>{tool.tool}</td>
                    <td style={{ ...tdStyle, textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                      {tool.calls.toLocaleString('ru-RU')}
                    </td>
                    <td style={{
                      ...tdStyle, textAlign: 'right', fontVariantNumeric: 'tabular-nums',
                      color: tool.failures > 0 ? C.dangerText : C.textSecondary,
                    }}>{tool.failures.toLocaleString('ru-RU')}</td>
                    <td style={{ ...tdStyle, textAlign: 'right', fontVariantNumeric: 'tabular-nums', color: C.textSecondary }}>
                      {tool.avgMs} мс
                    </td>
                    <td style={tdStyle}>
                      {failure ? (
                        <>
                          <span style={{ color: C.dangerText, fontSize: FS.xs }}>HTTP {failure.statusCode}</span>
                          <span style={{ color: C.textMuted, fontSize: FS.xs }}> · {relTime(failure.at)}</span>
                        </>
                      ) : <span style={{ color: C.textMuted }}>—</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <div style={hintStyle}>
        Строка с растущими отказами — повод нажать «Проверить» на вкладке «Серверы»:
        так «молчаливые» поломки авторизации перестают быть невидимыми.
      </div>
    </div>
  );
}
