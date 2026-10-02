import { useState, type ReactNode } from 'react';
import { Blend, Folder, ImageIcon, Layers, Mic, Music, Pencil, SlidersHorizontal, Sparkles, Upload, X } from 'lucide-react';
import { Island, IslandHeader, Toggle, InlineSegmented, IconButton } from '../components/ui';
import { C, FS, ISLAND, R, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { useIsMobile } from '../lib/breakpoints';
import { GenerationModeSwitch, type GenerationModeOption } from '../components/generation/GenerationModeSwitch';
import { GenerationPickMenu, type GenerationPickRow } from '../components/generation/GenerationPickMenu';
import { ExecutorList, ExecutorSummaryRow, type ExecutorRow } from '../components/generation/ExecutorList';
import { ReleaseNotice } from '../components/generation/ReleaseNotice';
import { useReleaseUndo } from '../components/generation/useReleaseUndo';
import { pickRows } from '../components/generation/pickSort';
import heroPng from '../assets/hero.png';
import aiHomePng from '../assets/ai-home.png';

// Витрина общего слоя панелей генерации (шаг Г1): зеркальный переключатель, меню
// «Что править?», «Исполнитель», плашка «Вернуть». Ширина 1440 / 360 переключается
// рамкой, тема — общим переключателем страницы.

type ImgMode = 'create' | 'edit';
type SndMode = 'voice' | 'music' | 'process';
type Width = 'wide' | 'narrow';

const NOW = Date.now();
const CHAT_IMAGES = [
  { id: 'cat', name: 'cat.png', sub: 'версия 1 · агент · 20 мин назад', thumb: aiHomePng, at: NOW - 20 * 60_000 },
  { id: 'hero', name: 'hero.png', sub: 'версия 3 · вы · 2 мин назад', thumb: heroPng, at: NOW - 2 * 60_000 },
  { id: 'logo', name: 'logo.png', sub: 'версия 2 · вы · 8 мин назад', at: NOW - 8 * 60_000 },
];

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

export function GenSharedKitSection() {
  const isMobile = useIsMobile();
  const [width, setWidth] = useState<Width>(isMobile ? 'narrow' : 'wide');
  const [mode, setMode] = useState<ImgMode>('create');
  const [focus, setFocus] = useState<string | null>(null);
  const [lastEdited, setLastEdited] = useState<string | null>('hero');
  const [emptyChat, setEmptyChat] = useState(false);
  const [menuAt, setMenuAt] = useState<DOMRect | 'inline' | null>(null);
  const [snd, setSnd] = useState<SndMode>('voice');
  const [exec, setExec] = useState('auto');
  const [execOpen, setExecOpen] = useState(true);
  const undo = useReleaseUndo<{ focus: string; mode: ImgMode }>();
  const narrow = width === 'narrow';

  const imgModes: GenerationModeOption<ImgMode>[] = [
    { value: 'create', label: 'Создать', icon: Sparkles },
    { value: 'edit', label: 'Править', icon: Pencil, muted: !focus, title: focus ? undefined : 'Править — сначала выберите картинку' },
  ];
  const sndModes: GenerationModeOption<SndMode>[] = [
    { value: 'voice', label: 'Голос', icon: Mic },
    { value: 'music', label: 'Музыка', icon: Music },
    { value: 'process', label: 'Обработка', icon: SlidersHorizontal, muted: true, title: 'Обработка — сначала выберите звук' },
  ];

  const rows: GenerationPickRow[] = emptyChat ? [] : pickRows(CHAT_IMAGES, { lastId: lastEdited, excludeId: focus })
    .map(r => ({ id: r.id, name: r.name, sub: r.sub, thumb: r.thumb, icon: <ImageIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, mark: r.last ? 'правили последней' : undefined }));
  const pick = (id: string) => { setFocus(id); setLastEdited(id); setMode('edit'); setMenuAt(null); undo.dismiss(); };
  const openMenu = (anchor: DOMRect) => setMenuAt(narrow ? 'inline' : anchor);
  const release = (byHuman: boolean) => {
    if (!focus) return;
    undo.release({ snapshot: { focus, mode }, text: 'Картинка снята — дальше рисуем новую' }, byHuman);
    setFocus(null);
    setMode('create');
  };
  const restore = () => {
    const s = undo.undo();
    if (s) { setFocus(s.focus); setMode(s.mode); }
  };
  const create = mode === 'create';
  const execRows = IMG_ROWS(create);
  const cur = execRows.find(r => r.id === exec && !r.disabled) ?? execRows[0];

  const menu = menuAt && (
    <GenerationPickMenu
      title="Что править?"
      subtitle="Картинки этого чата, свежие сверху"
      rows={rows}
      onPick={pick}
      emptyText="В этом чате пока нет картинок"
      emptyHint="Нарисуйте новую в «Создать» или прикрепите через «＋»"
      extras={[
        { key: 'upload', label: 'С компьютера…', icon: <Upload size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, onClick: () => setMenuAt(null) },
        { key: 'project', label: 'Из файлов проекта…', icon: <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, onClick: () => setMenuAt(null) },
      ]}
      footer="Или «Работать с этой» на карточке в ленте"
      onClose={() => setMenuAt(null)}
      anchor={menuAt === 'inline' ? undefined : menuAt}
      // В рамке витрины — вниз под полосой: вверх меню ушло бы под шторку соседней секции
      top={menuAt === 'inline' ? 0 : undefined}
      fullWidth={narrow}
      isMobile={narrow}
    />
  );

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
          <Block label={`GenerationModeSwitch — режим: ${mode}${focus ? ` · картинка ${focus}` : ' · картинки нет, «Править» спрашивает'}`}>
            {/* Полоса над полем ввода: плашка «Вернуть» встаёт над ней, меню — над полосой */}
            <div style={{ position: 'relative', display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              {undo.offer && <ReleaseNotice text={undo.offer.text} onUndo={restore} isMobile={narrow} />}
              <div style={{
                display: 'flex', alignItems: 'center', gap: SP.sm, padding: SP.sm, minWidth: 0,
                border: `1px solid ${C.borderLight}`, borderRadius: R.lg, background: C.bgCard,
              }}>
                <GenerationModeSwitch value={mode} options={imgModes} onChange={setMode} onMutedClick={(_, a) => openMenu(a)}
                  compact={narrow} quiet={narrow} isMobile={narrow} />
                <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {focus ? <>Работаем с: <b style={{ color: C.textHeading }}>{focus}.png</b></> : 'Новая картинка'}
                </span>
                {focus && <Chipx title="Снять выбор (человек)" onClick={() => release(true)} />}
                {focus && <Chipx title="Снять выбор (агент) — без плашки" agent onClick={() => release(false)} />}
              </div>
              {/* Нулевой якорь под полосой: меню absolute отсчитывается от него */}
              {menuAt === 'inline' && <div style={{ position: 'relative' }}>{menu}</div>}
            </div>
            {menuAt && menuAt !== 'inline' && menu}
          </Block>

          <Block label="Тот же переключатель у звука: «Обработка» приглушена без выбранного звука">
            <GenerationModeSwitch value={snd} options={sndModes} onChange={setSnd} onMutedClick={(_, a) => openMenu(a)}
              compact={narrow} isMobile={narrow} />
          </Block>

          <Block label={`ExecutorSummaryRow + ExecutorList — «${create ? 'Создать' : 'Править'}»: серые с причиной`}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
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

          <Block label="ReleaseNotice — статично (живая — над полосой выше, 4 с после ✕)">
            <ReleaseNotice text="Звук снят — вернулись к «Музыке»" onUndo={() => {}} isMobile={narrow} />
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

// ✕ на чипе «Работаем с»; вторая кнопка изображает снятие выбора агентом
function Chipx({ title, onClick, agent }: { title: string; onClick: () => void; agent?: boolean }) {
  const Icon = agent ? Blend : X;
  return <IconButton size="xs" title={title} onClick={onClick}><Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></IconButton>;
}
