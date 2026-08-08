// Свойства документа колонкой справа от текста: подпись сверху, контрол под ней, поля
// даты и текста раскрыты сразу — в колонке на это есть место.
//
// Обвязку (aside со своей границей, а на узком просмотрщике блок под текстом) даёт
// вызывающий — ровно как у связей заметки (NoteConnections). Ниже в той же колонке
// стоит панель комментариев к документу, поэтому заголовок секции общий (SidebarSection).

import { C, FS, SP } from '../../lib/design';
import { SidebarSection } from '../../components/ui';
import { badgeKeyOf } from '../../lib/docsTypes';
import { PropControl, useChipHeight } from './DocsProps';
import type { DocPropsState } from './useDocProps';

// Свёрнуты по умолчанию: документ открывают читать, а не править свойства. Ключ общий
// на все документы — это привычка чтения, а не данные конкретного файла
const OPEN_KEY = 'cc_doc_props_open';

export function DocPropsPanel({ state }: { state: DocPropsState }) {
  const { doc, type, index, savingKey, error, save } = state;
  const h = useChipHeight();
  // Главное свойство типа — то, что представляет документ плашкой в шапке превью и точкой
  // в дереве. В колонке оно среди равных, поэтому подпись выделена: сразу видно, какое
  // из полей «то самое»
  const mainKey = (badgeKeyOf(type) ?? '').toLowerCase();

  if (!doc || !type) return null;

  // Главное свойство в шапке свёрнутой секции — тем же контролом, что и внутри: значение
  // видно и меняется, не раскрывая панель. Только у выбора: поле ввода в строке заголовка
  // выглядело бы формой, а не значением
  const mainDef = type.properties.find(p => p.key.toLowerCase() === mainKey && p.kind === 'choice');

  return (
    <SidebarSection title="Свойства" count={type.properties.length}
      storageKey={OPEN_KEY} defaultOpen={false}
      collapsedActions={mainDef && (
        <PropControl def={mainDef} doc={doc} index={index} h={h}
          saving={savingKey === mainDef.key} onSave={save} />
      )}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {/* Показываем ВСЕ свойства схемы, включая пустые: в колонке незаполненное
            свойство не теснит соседей, а приглашает заполнить */}
        {type.properties.map(def => (
          <div key={def.key} style={{ display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0 }}>
            <span
              style={def.key.toLowerCase() === mainKey ? mainLabelStyle : labelStyle}
              title={def.key.toLowerCase() === mainKey ? `${def.key} — главное свойство типа` : def.key}
            >{def.title || def.key}</span>
            <PropControl def={def} doc={doc} index={index} h={h} expanded
              saving={savingKey === def.key} onSave={save} />
          </div>
        ))}
      </div>
      {error && <div style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.danger }}>{error}</div>}
    </SidebarSection>
  );
}

const labelStyle: React.CSSProperties = {
  fontSize: FS.xs, color: C.textMuted,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};

// Подпись главного свойства: вес и тон темнее — на бледной подписи один только жирный
// начертанием почти не читается
const mainLabelStyle: React.CSSProperties = {
  ...labelStyle, fontWeight: 700, color: C.textSecondary,
};
