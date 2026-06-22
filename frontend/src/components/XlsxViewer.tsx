import { useMemo, useState } from 'react';
import * as XLSX from 'xlsx';
import { base64ToBytes } from '../lib/binary';

interface Sheet { name: string; html: string }

export default function XlsxViewer({ base64 }: { base64: string }) {
  const { sheets, error } = useMemo(() => {
    try {
      const wb = XLSX.read(base64ToBytes(base64), { type: 'array' });
      const sheets: Sheet[] = wb.SheetNames.map(name => ({
        name,
        // sheet_to_html экранирует содержимое ячеек — XSS-безопасно для собственных файлов
        html: XLSX.utils.sheet_to_html(wb.Sheets[name], { id: 'xlsx-sheet' }),
      }));
      return { sheets, error: false };
    } catch {
      return { sheets: [] as Sheet[], error: true };
    }
  }, [base64]);

  const [active, setActive] = useState(0);

  if (error) {
    return <div style={{ color: '#8A8070', fontSize: 13, padding: 40, textAlign: 'center' }}>Не удалось отобразить таблицу</div>;
  }
  if (sheets.length === 0) {
    return <div style={{ color: '#8A8070', fontSize: 13, padding: 40, textAlign: 'center' }}>Пустая книга</div>;
  }

  const current = sheets[Math.min(active, sheets.length - 1)];

  return (
    <div style={{ width: '100%', display: 'flex', flexDirection: 'column', gap: 12 }}>
      {/* Стили сгенерированной таблицы */}
      <style>{`
        #xlsx-sheet { border-collapse: collapse; font-family: 'Hanken Grotesk', sans-serif; font-size: 13px; background: #FFFFFF; }
        #xlsx-sheet td, #xlsx-sheet th { border: 1px solid #E0D8CC; padding: 4px 9px; white-space: nowrap; color: #2A251F; }
        #xlsx-sheet tr:first-child td { background: #F4F0E8; font-weight: 600; }
      `}</style>

      {/* Вкладки листов */}
      {sheets.length > 1 && (
        <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
          {sheets.map((s, i) => (
            <button key={s.name} onClick={() => setActive(i)}
              style={{
                padding: '4px 12px', borderRadius: 7, border: 'none', cursor: 'pointer',
                fontSize: 12.5, fontWeight: 600,
                background: i === active ? '#D97757' : '#EDE7DC',
                color: i === active ? '#FBF8F2' : '#756B5E',
              }}>
              {s.name}
            </button>
          ))}
        </div>
      )}

      {/* Таблица текущего листа */}
      <div style={{ overflow: 'auto', border: '1px solid #E0D8CC', borderRadius: 8, background: '#FFFFFF' }}
        dangerouslySetInnerHTML={{ __html: current.html }} />
    </div>
  );
}
