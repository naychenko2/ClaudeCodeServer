import { useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal } from '../../components/ui';
import { C, FONT, FS, MODAL_W } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';

// Каркас раздела «Модели и расход» (этап 1 редизайна, макет docs/mockups/models-spend-v3.html):
// одна модалка с тремя вкладками вместо прежних «Использование» + «Поставщики моделей».
// Тела вкладок — заглушки: «Квоты и деньги» и «Модели по умолчанию»/«Применение» наполняются
// отдельными задачами из кода UsageScreen.tsx и ModelProvidersTabsModal.tsx.

type TabKey = 'quotas' | 'slots' | 'apply';

export function ModelsSpendModal({ onClose }: { onClose: () => void }) {
  const isMobile = useIsMobile();
  const [tab, setTab] = useState<TabKey>('quotas');

  const tabs: { key: TabKey; label: string }[] = [
    { key: 'quotas', label: 'Квоты и деньги' },
    // На мобиле полное название не влезает в полосу вкладок — короткий вариант из макета
    { key: 'slots', label: isMobile ? 'Модели' : 'Модели по умолчанию' },
    { key: 'apply', label: 'Применение' },
  ];

  const tabBtnStyle = (active: boolean): CSSProperties => ({
    font: 'inherit', fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600,
    color: active ? C.accent : C.textSecondary, background: 'transparent',
    border: 'none', borderBottom: `2px solid ${active ? C.accent : 'transparent'}`,
    padding: '10px 12px', cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
  });

  const activeLabel = tabs.find(t => t.key === tab)?.label ?? '';

  return (
    <Modal title="Модели и расход" width={MODAL_W.wide} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {/* Полоса вкладок */}
        <div style={{
          display: 'flex', gap: 2, borderBottom: `1px solid ${C.borderLight}`,
          overflowX: 'auto', flexShrink: 0, margin: '0 -4px',
        }}>
          {tabs.map(t => (
            <button key={t.key} type="button" style={tabBtnStyle(tab === t.key)} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </div>

        {/* Тело активной вкладки — заглушка до задач наполнения */}
        <div style={{ paddingTop: 12 }}>
          <div style={{ fontSize: FS.md, color: C.textMuted, padding: '8px 0' }}>
            Вкладка «{activeLabel}» наполняется отдельной задачей
          </div>
        </div>
      </div>
    </Modal>
  );
}
