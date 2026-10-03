import type { ReactNode } from 'react';
import { Island, IslandHeader } from '../components/ui';
import { C, FS, ISLAND, SP } from '../lib/design';
import { ContextPanel, type ContextPanelProps } from '../components/generation/ContextPanel';
import type { RowGit } from '../components/chat/ContextRowView';
import { stubActionRun } from '../lib/chatContext/actionRunStub';
import type { ChatContextPrimary, ChatContextRef, ContextAction } from '../lib/chatContext/types';
import heroPng from '../assets/hero.png';
import aiHomePng from '../assets/ai-home.png';

// Витрина панели «Контекст» (ADR-023 §Д1, макет composer-actions-v1 §2): все секции и пустые состояния.
// Панель рисуется по готовой модели без стора и сети; тема — общим переключателем страницы.
// Низ — заглушка на типе ActionRun (живой useActionRun приходит в 1ф-4).

const noop = () => {};
const git: RowGit = {
  label: 'feat/video-editor', changes: 3, ahead: 1, publishN: 1,
  onCommitOwn: noop, onCommitAll: noop, onPublish: noop, onShowChanges: noop,
};
const item = { ref: {}, addedAt: '', missing: false };
const primary = (over: Partial<ChatContextPrimary> = {}): ChatContextPrimary =>
  ({ ...item, id: 'p', kind: 'image', by: 'human', label: 'hero.png', version: 'версия 2 из 2 · 1024×768', thumb: heroPng, role: null, ...over });
const ref = (id: string, label: string, over: Partial<ChatContextRef> = {}): ChatContextRef =>
  ({ ...item, id, kind: 'image', by: 'human', label, version: null, thumb: aiHomePng, role: 'style', usedBy: ['edit'], ...over });
const REFS = [ref('anya', 'Аня', { role: 'char' }), ref('palette', 'palette.png', { usedBy: [] })];
const edit: ContextAction = { id: 'edit', kind: 'run', label: 'Изменить', hint: 'Изменить картинку', op: 'edit' };
const exec = {
  rows: [
    { id: 'auto', group: 'auto' as const, name: 'Qwen-Image Edit', sub: 'локально', price: 'бесплатно · ~40 с', free: true, amount: null, unit: 'free' as const, etaSeconds: 40, badges: [{ label: 'без RU', tone: 'warn' as const }] },
    { id: 'kontext', group: 'cloud' as const, name: 'FLUX Kontext', sub: 'fal', price: '$0.04 / шт.' },
  ],
  value: 'auto', onChange: noop,
};
const preview = <img src={heroPng} alt="" style={{ height: 150, objectFit: 'cover' }} />;
const addFrom = [
  { id: 'files', label: 'Из файлов проекта', hint: 'выберите файл в «Файлах»', run: noop },
  { id: 'chars', label: 'Из «Персонажей»', hint: 'ролью «персонаж»', run: noop },
];

const props = (over: Partial<ContextPanelProps>): ContextPanelProps => ({
  isMobile: false, git, primary: null, refs: [], iconOf: () => null, preview: null, editor: null, step: null, ret: null,
  onReturn: noop, action: null, exec: null, params: [], onParam: noop, addFrom, run: stubActionRun(null), flash: 0,
  onRelease: noop, onDetach: noop, onClear: noop, onClose: noop, layout: 'column', ...over,
});

const FULL: Partial<ContextPanelProps> = {
  primary: primary(), refs: REFS, preview, action: edit, run: stubActionRun(edit),
  editor: { label: 'Открыть редактор', hint: 'маска и «Без ИИ»: обрезать, повернуть, формат', open: noop },
  step: { prev: noop, next: null },
  exec,
  params: [{ kind: 'variants', min: 1, max: 4, value: 3 }],
};

function Frame({ caption, h = 760, w = 400, children }: { caption: string; h?: number; w?: number; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{caption}</div>
      <div style={{ position: 'relative', display: 'flex', width: w, height: h, maxWidth: '100%', overflow: 'hidden', background: C.bgMain }}>
        {children}
      </div>
    </div>
  );
}

export function ContextPanelKitSection() {
  return (
    <Island>
      <IslandHeader title="Панель «Контекст»" />
      <div style={{ padding: SP.md, display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(min(100%, 400px), 1fr))', gap: ISLAND.gap }}>
        <Frame caption="Все секции: Где, С чем (редактор, ‹ ›), Чем, Плюс с серым, Параметры, низ">
          <ContextPanel {...props(FULL)} />
        </Frame>
        <Frame caption="Пусто: ничего не выбрано, личный чат — «Где» нет">
          <ContextPanel {...props({ git: null })} />
        </Frame>
        <Frame caption="Объект в «Чате»: «Чем» и «Параметры» — пустые состояния Р2">
          <ContextPanel {...props({ primary: primary(), preview, refs: REFS.map(r => ({ ...r, usedBy: [] })) })} />
        </Frame>
        <Frame caption="Объект от агента ✦ и ссылка «назад» к сцене">
          <ContextPanel {...props({
            primary: primary({ by: 'agent', label: 'кот.png', version: 'версия 1', thumb: aiHomePng }), preview,
            ret: { prev: primary({ id: 'scene', kind: 'video-scene', label: 'Утро' }), label: 'К сцене «Утро»' },
          })} />
        </Frame>
        <Frame caption="Ход идёт: прогресс в низу, кнопка гаснет">
          <ContextPanel {...props({
            ...FULL, run: { ...stubActionRun(edit), label: '✦ Изменяем hero.png… 40 %', state: 'running', progress: 0.4, quote: { price: '3 × $0.04 = $0.12', detail: '~15 с' } },
          })} />
        </Frame>
        <Frame caption="Действие без параметров («Убрать фон») и серая кнопка с причиной">
          <ContextPanel {...props({
            ...FULL, params: [], action: { ...edit, id: 'removeBg', label: 'Убрать фон', disabledReason: 'Напишите текст: Что изменить на картинке…' },
            run: stubActionRun({ ...edit, id: 'removeBg', label: 'Убрать фон' }),
          })} />
        </Frame>
        <Frame caption="Телефон 360: шторка (поднята)" w={360} h={780}>
          <ContextPanel {...props({ ...FULL, isMobile: true, layout: 'sheet', contained: true })} />
        </Frame>
        <Frame caption="Телефон 360: шторка опущена до цены" w={360} h={780}>
          <ContextPanel {...props({ ...FULL, isMobile: true, layout: 'sheet', contained: true, peeked: true })} />
        </Frame>
      </div>
    </Island>
  );
}
