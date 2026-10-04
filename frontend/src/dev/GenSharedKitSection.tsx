import { useState, type ReactNode } from 'react';
import { Layers, Music } from 'lucide-react';
import { Island, IslandHeader, Toggle, InlineSegmented } from '../components/ui';
import { C, FS, ISLAND, R, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { useIsMobile } from '../lib/breakpoints';
import { ExecutorList, ExecutorSummaryRow, type ExecutorRow } from '../components/generation/ExecutorList';
import { GenerationFootView, GenerationPanel, type GenerationFoot } from '../components/generation/GenerationPanel';
import { ByClaude } from '../components/generation/ByClaude';
import { ReleaseNotice } from '../components/generation/ReleaseNotice';

// Витрина общего слоя панелей генерации (шаг Г1): зеркальный переключатель, меню
// «Что править?», «Исполнитель», плашка «Вернуть». Ширина 1440 / 360 переключается
// рамкой, тема — общим переключателем страницы.

type Width = 'wide' | 'narrow';

const IMG_ROWS = (create: boolean): ExecutorRow[] => [
  { id: 'auto', group: 'auto', name: 'Авто', sub: 'сначала своя видеокарта, при отказе — облако с вашего согласия', price: 'бесплатно',
    badges: [{ label: create ? 'сейчас Qwen-Image' : 'сейчас Qwen Edit', tone: 'success' }] },
  create
    ? { id: 'qwen', group: 'local', name: 'Qwen-Image 2.1', sub: 'новая картинка · понимает русский', price: 'бесплатно · ~40 с' }
    : { id: 'qwen-edit', group: 'local', name: 'Qwen-Image Edit', sub: 'правка по 1–16 образцам', price: 'бесплатно · ~50 с' },
  { id: 'faces', group: 'local', name: 'FaceDetailer', sub: 'только «Улучшить лица»', price: 'бесплатно', disabled: create, reason: 'только «Улучшить лица» — для новой картинки не годится' },
  { id: 'kontext', group: 'cloud', name: 'fal · FLUX Kontext', sub: create ? 'по тексту и образцам' : 'правка, до 4 образцов', price: '$0.04 / шт.' },
  { id: 'nano', group: 'cloud', name: 'fal · Nano Banana 2', price: '$0.04 / шт.', badges: [{ label: 'новая', tone: 'accent' }] },
  { id: 'soul', group: 'cloud', name: 'Higgsfield · Soul', sub: 'фото людей, персонаж Soul', price: '2 кр. / шт.',
    disabled: !create, reason: 'не умеет «Изменить» — только новая картинка' },
];

const noop = () => {};
// Низ панели в четырёх состояниях: покой, идёт работа, готово, «устарел»
const FOOT_STATES: [string, GenerationFoot][] = [
  ['покой — «Снять» с цифрой и ценой', { count: 2, maxCount: 4, onCountChange: noop, price: ['≈ $3.20', '2 × $1.60 · 8 с'], runLabel: 'Снять', onRun: noop }],
  ['идёт работа — полоса и «Отменить»', { progress: { label: 'Собираем фильм: сцена 3 из 4', p: 62, onCancel: noop }, price: ['бесплатно', 'сборка без ИИ'], runLabel: 'Собрать', onRun: noop }],
  ['готово — файл и действия', { result: { file: 'film.mp4', actions: [{ label: 'Открыть', onClick: noop }, { label: 'Показать в дереве', onClick: noop }] }, price: ['0:32 · бесплатно', '4 сцены · сборка без ИИ'], runLabel: 'Пересобрать', onRun: noop }],
  ['устарел — причины над кнопкой', { stale: ['порядок сцен', 'склейка 2'], result: { file: 'film.mp4', actions: [{ label: 'Открыть', onClick: noop }] }, price: ['0:32 · бесплатно', '4 сцены · сборка без ИИ'], runLabel: 'Пересобрать', onRun: noop }],
];

export function GenSharedKitSection() {
  const isMobile = useIsMobile();
  const [width, setWidth] = useState<Width>(isMobile ? 'narrow' : 'wide');
  const [emptyChat, setEmptyChat] = useState(false);
  const [second, setSecond] = useState(false);
  const [exec, setExec] = useState('auto');
  const [execOpen, setExecOpen] = useState(true);
  const narrow = width === 'narrow';

  const create = true;
  const execRows = IMG_ROWS(create);
  const cur = execRows.find(r => r.id === exec && !r.disabled) ?? execRows[0];

  return (
    <Island>
      <IslandHeader
        icon={<Layers size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />}
        title="Общий слой панелей генерации — Г1"
      />
      <div style={{ padding: ISLAND.pad, display: 'flex', flexDirection: 'column', gap: ISLAND.gap }}>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg, alignItems: 'center' }}>
          <InlineSegmented<Width> value={width} onChange={setWidth} options={[{ value: 'wide', label: '1440' }, { value: 'narrow', label: '360' }]} />
          <Row label="пустой чат"><Toggle checked={emptyChat} onChange={setEmptyChat} /></Row>
        </div>

        <div style={{ width: narrow ? 360 : '100%', maxWidth: '100%', display: 'flex', flexDirection: 'column', gap: ISLAND.gap }}>
          <Block label={`ExecutorSummaryRow + ExecutorList — «${create ? 'Создать' : 'Править'}»: серые с причиной`}>
            {/* В продукте «Исполнитель» живёт в колонке 380 — витрина показывает ту же ширину */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, width: 380, maxWidth: '100%' }}>
              <ExecutorSummaryRow
                name={cur.name}
                parts={cur.group === 'auto' ? ['локально', create ? 'Qwen-Image 2.1' : 'Qwen-Image Edit'] : [cur.group === 'local' ? 'локально' : 'облако']}
                price={{ label: cur.price.split(' · ')[0], tone: cur.price.startsWith('бесплатно') ? 'success' : 'neutral' }}
                open={execOpen}
                onToggle={() => setExecOpen(o => !o)}
                isMobile={narrow}
              />
              {execOpen && <ExecutorList rows={execRows} value={cur.id} onChange={id => { setExec(id); }} isMobile={narrow} />}
            </div>
          </Block>

          <Block label="Низ панели (GenerationFoot) — покой · идёт работа · готово · устарел; без «− N +» у «Фильма»">
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, width: 380, maxWidth: '100%' }}>
              {FOOT_STATES.map(([label, foot]) => (
                <div key={label} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>{label}</span>
                  <div style={{ padding: `${SP.sm}px ${SP.md}px`, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, background: C.bgCard }}>
                    <GenerationFootView foot={foot} />
                  </div>
                </div>
              ))}
            </div>
          </Block>

          <Block label="Панель «Звук» с заготовкой из «Видео»: строка «↩ К фильму», контекст нового звука, низ">
            <div style={{ width: narrow ? '100%' : 380, maxWidth: '100%', height: 420, display: 'flex' }}>
              <GenerationPanel<'settings'>
                title="Звук" subtitle="Музыка · Песня" icon={<Music size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                tabs={[{ value: 'settings', label: 'Настройки' }]} tab="settings" onTabChange={noop}
                layout="column" width={narrow ? 320 : 380} onWidthChange={noop}
                returnLink={{ label: 'К фильму «утро-в-горах»', onClick: noop }}
                context={<span>Новый звук · заготовка из «Видео»: под фильм «утро-в-горах»</span>}
                foot={{ count: 1, maxCount: 3, onCountChange: noop, price: ['≈ $0.10', '1 × $0.10 · 32 с'], runLabel: 'Сочинить', onRun: noop }}
              >
                <div style={{ fontSize: FS.sm, color: C.textSecondary, paddingTop: SP.sm }}>Длительность 32 с · «Инструментал» · стиль по текстам сцен</div>
              </GenerationPanel>
            </div>
          </Block>

          <Block label="ByClaude — «✦ Claude» у подписи секции и на строке списка">
            <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
              <b style={{ color: C.textHeading }}>Текст сцены</b><ByClaude />
              <span>Сцена 3 · ✂ 8 с</span><ByClaude />
            </div>
          </Block>

          <Block label="ReleaseNotice — статично (живая — над полосой выше, 4 с после ✕)">
            <ReleaseNotice text="Звук снят — вернулись к «Музыке»" onUndo={() => {}} isMobile={narrow} />
            {/* Вторая плашка с подъёмом круга AI: уход любой из двух не сбрасывает подъём, пока видна другая */}
            <Row label="вторая плашка с подъёмом круга AI"><Toggle checked={second} onChange={setSecond} /></Row>
            {second && <div data-kit-second-notice=""><ReleaseNotice text="Вторая плашка — подъём круга AI" onUndo={() => {}} isMobile={narrow} raiseFab /></div>}
          </Block>
        </div>
      </div>
    </Island>
  );
}

function Block({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{label}</div>
      {children}
    </div>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      {children}
      <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{label}</span>
    </div>
  );
}
