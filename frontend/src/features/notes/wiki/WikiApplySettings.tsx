// Блок «Применение» настроек вики (S7d-1, макет llm-wiki-v2 экран «Настройки»): общий
// выключатель «Применять без ревью», классы правок origin×op с долей откатов за 7 и 30 дней
// и состоянием; у выключенного класса — причина и «Снова применять само».
//
// Данные — снимок настроек стора (loadWikiSettings/useWikiSettings, та же ручка, что у блока
// «Ночной разбор»): доли показывает оценка последнего GET, а не память прошлого просмотра
// (шапка WikiSettingsController). Блок в wikiTabs.tsx не встроен — это шаг S7d-3.
//
// Состояния: загрузка (снимка ещё нет) — скелетон; отказ чтения — плашка с «Повторить»;
// пустой список классов — пустое состояние (классов нет, пока нечего было применять).
// Действия оптимистичны (стор), отказ контракта — откат и плашка с текстом по коду; кнопки
// заблокированы на время запроса. Ключ класса в UI — бэковая форма «origin×op» с пробелами
// для чтения; в запрос уходит ключ как есть. Тексты состояний — по контракту: «само» для
// включённого, «выключен» + причина бэка для выключенного (кто выключил — говорит причина,
// отдельного поля «кем» в контракте нет).

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, SlidersHorizontal } from 'lucide-react';
import {
  C, FONT, FS, R, SHADOW, SP,
  WikiError,
  loadWikiSettings, putWikiAutoApply, resumeWikiApplyClass, useWikiSettings,
  type WikiApplyClassDto,
} from 'aihome_shell/kit';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { Toggle } from '../../../components/ui/Toggle';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { countWord, wikiErrorText } from './wikiLabels';

// Доля — число уже в процентах (WikiClassHealth.ClassStat «доля, процент»); запятая —
// русская запись, как её читает человек
function pctText(v: number): string {
  const r = Math.round(v * 10) / 10;
  return `${Number.isInteger(r) ? r : r.toFixed(1).replace('.', ',')} %`;
}

// Дата выключения — из DateTimeOffset бэка в локальное dd.mm.yyyy; нечитаемое — без даты
function disabledDateText(iso: string): string {
  const d = new Date(iso);
  if (!Number.isFinite(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return ` · ${p(d.getDate())}.${p(d.getMonth() + 1)}.${d.getFullYear()}`;
}

// Класс показывается по бэковому ключу, но с пробелами вокруг «×» — слитное «session×append»
// человеку нечитабельно; ключ для запроса остаётся исходным
const classLabel = (key: string) => key.split('×').join(' × ');

function statsText(c: WikiApplyClassDto): string {
  if (!c.applied30 && !c.applied7) return 'операций в окне ещё не было';
  const part = (applied: number, retracted: number, share: number) =>
    `${countWord(applied, 'операция', 'операции', 'операций')} · откатов ${retracted} (${pctText(share)})`;
  return `за 30 дн: ${part(c.applied30, c.retracted30, c.share30)} · за 7 дн: ${part(c.applied7, c.retracted7, c.share7)}`;
}

function applyErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_settings_write_failed': return 'Файл настроек не записан';
    case 'wiki_nightly': return 'Настройки не приняты';
  }
  return wikiErrorText(e);
}

const GENERIC_ACTION_ERROR = 'Не удалось сохранить настройки';

function LoadingSkeleton() {
  const bar = (w: number) => (
    <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.md}px 0` }} />
  );
  return <div style={{ padding: SP.md, opacity: 0.7 }} aria-hidden>{bar(60)}{bar(85)}{bar(45)}</div>;
}

// Строка класса — разметка строки списка, как «Журнал» (JournalRow): слева класс и состояние,
// ниже — замер и (для выключенного) причина с датой, действие — своей кнопкой в строке
function ClassRow({ cls, busy, onResume }: { cls: WikiApplyClassDto; busy: boolean; onResume: () => void }) {
  return (
    <div
      data-testid="wiki-apply-row"
      style={{
        display: 'flex', flexDirection: 'column', gap: 4,
        padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
      }}
    >
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {classLabel(cls.class)}
        </span>
        {cls.disabled
          ? <Badge tone="warning">выключен</Badge>
          : <Badge tone="success">само</Badge>}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>{statsText(cls)}</div>
      {cls.disabled && (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'break-word' }}>
          {cls.reason ?? 'причина не названа'}{cls.disabledAt ? disabledDateText(cls.disabledAt) : ''}
        </div>
      )}
      {cls.disabled && (
        <div style={{ marginTop: SP.xs }}>
          <Button
            variant="secondary" size="xs"
            dataTestId={`wiki-apply-resume-${cls.class}`}
            ariaLabel={`Снова применять само: ${classLabel(cls.class)}`}
            disabled={busy}
            onClick={onResume}
          >
            Снова применять само
          </Button>
        </div>
      )}
    </div>
  );
}

export function WikiApplySettings() {
  const snapshot = useWikiSettings();
  const [loadError, setLoadError] = useState<unknown>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  // Одна идущая правка настроек (тумблер или класс): слот стора один, вторая правка
  // до ответа первой показала бы состояние, которое бэк ещё не подтвердил
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setLoadError(null);
    // Снимок и его поколение — у стора; здесь только старт и отказ для плашки
    loadWikiSettings().catch(e => setLoadError(e));
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании блока: другого места старта нет, снимок стора здесь не инициализируется
    load();
  }, [load]);

  const runAction = async (action: () => Promise<{ ok: boolean; error?: WikiError }>) => {
    if (busy) return;
    setBusy(true);
    setActionError(null);
    try {
      const r = await action();
      if (!r.ok) setActionError(applyErrorText(r.error as WikiError));
    } catch (e) {
      setActionError(e instanceof WikiError ? applyErrorText(e) : GENERIC_ACTION_ERROR);
    } finally {
      setBusy(false);
    }
  };

  if (loadError) {
    const text = loadError instanceof WikiError ? wikiErrorText(loadError) : 'Не удалось прочитать настройки';
    return (
      <div data-testid="wiki-apply-error" style={{ padding: `${SP.sm}px ${SP.md}px` }}>
        <Notice tone="danger" icon={AlertTriangle} title={text} action={<Button variant="ghost" size="sm" onClick={load}>Повторить</Button>} />
      </div>
    );
  }

  if (!snapshot) return <LoadingSkeleton />;

  const auto = snapshot.autoApplyEnabled;
  return (
    <div style={{ padding: `${SP.sm}px ${SP.md}px ${SP.md}px` }}>
      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>
        Применение без ревью
      </h2>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs }}>
        Неспорное — хроника, «дописать», связь и новая страница без дубля — применяется само после
        проверок кода. Спорное идёт во «Входящие».
      </div>

      <div
        data-testid="wiki-apply-switch"
        style={{
          display: 'flex', gap: SP.md, alignItems: 'center', marginTop: SP.md,
          padding: SP.md, border: `1px solid ${C.border}`, borderRadius: R.md,
          background: C.bgPanel, boxShadow: SHADOW.card,
        }}
      >
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: FS.base, fontWeight: 500, color: C.textHeading }}>Применять неспорное само</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
            Выключите — всё, включая хроники, пойдёт во «Входящие» на ваше решение.
          </div>
        </div>
        <Toggle
          checked={auto}
          disabled={busy}
          focusable
          ariaLabel="Применять без ревью"
          onChange={(v) => { void runAction(() => putWikiAutoApply(v)); }}
        />
      </div>

      {actionError && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title={actionError} />
        </div>
      )}

      <h3 style={{ margin: `${SP.lg}px 0 ${SP.xs}px`, fontSize: FS.md, color: C.textHeading }}>Классы правок</h3>
      <div style={{ fontSize: FS.sm, color: C.textMuted, marginBottom: SP.sm }}>
        Класс выключается сам, если в окне 50 последних операций откатов ≥ 10 % или за 7 дней ≥ 3.
        После «Снова применять само» окно начинается заново.
      </div>

      {snapshot.classes.length === 0 ? (
        <EmptyState
          icon={<SlidersHorizontal size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
          title="Классов правок ещё нет"
          subtitle={<span style={{ maxWidth: 360 }}>Здесь появятся классы «источник × операция» с их долей откатов, когда в вике начнутся применения.</span>}
        />
      ) : (
        <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgPanel, boxShadow: SHADOW.card, overflow: 'hidden' }}>
          {snapshot.classes.map(c => (
            <ClassRow
              key={c.class}
              cls={c}
              busy={busy}
              onResume={() => { void runAction(() => resumeWikiApplyClass(c.class)); }}
            />
          ))}
        </div>
      )}
    </div>
  );
}
