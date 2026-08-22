import { useState } from 'react';
import { Sparkles, RotateCcw, AlertTriangle, Image } from 'lucide-react';
import type { Project, ProjectBackground, BackgroundResult } from '../../../types';
import { api } from '../../../lib/api';
import { C, R, SP, FS, MODAL_W } from '../../../lib/design';
import { Button, Modal } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';
import { agentDotColor } from '../../../components/AgentSelector';
import { projectColor } from '../../../lib/tasks';
import { backgroundColorName } from '../backgroundColors';
import { invalidateProjectsCache } from '../useAllProjects';
import { AccordionSection, type AccordionSummaryTone } from './AccordionSection';

// Компактная маска стандартного дудла для превью настроек. CanvasBackdrop правит другая
// волна (и не экспортирует свой тайл) — тут своя мини-аппроксимация: несколько иконок,
// чтобы свайл читался как «нейтральный паттерн без цвета». Цвет линий задаёт background-color
// снаружи (маска работает по альфе), как в CanvasBackdrop.
const STD_SVG =
  // eslint-disable-next-line design/no-raw-color -- stroke SVG-тайла маски: значима только альфа, цвет линий задаёт background-color снаружи (как в CanvasBackdrop)
  '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120" fill="none" stroke="#000" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
  '<g transform="translate(14,16) rotate(-6)"><rect x="0" y="0" width="22" height="16" rx="3"/><path d="M5 6l3 3-3 3M12 12h7"/></g>' +
  '<g transform="translate(64,12) rotate(5)"><path d="M8 0L1 18h3l4-9 4 9h3z"/></g>' +
  '<g transform="translate(96,30) rotate(-4)"><circle cx="6" cy="6" r="5"/><circle cx="6" cy="22" r="5"/><circle cx="22" cy="14" r="5"/></g>' +
  '<g transform="translate(16,62) rotate(6)"><path d="M2 3a3 3 0 013-3h16a3 3 0 013 3v10a3 3 0 01-3 3H10l-6 5v-5H5a3 3 0 01-3-3z"/></g>' +
  '<g transform="translate(66,66) rotate(-8)"><circle cx="11" cy="11" r="10"/><path d="M7 11.5l3 3 6-6"/></g>' +
  '<g transform="translate(20,98) rotate(4)"><path d="M6 0L0 6l6 6M20 0l6 6-6 6"/></g>' +
  '<g transform="translate(80,96) rotate(-6)"><rect x="0" y="3" width="20" height="14" rx="2"/><path d="M6 3V1M14 3V1M0 9h20"/></g>' +
  '</svg>';
const STD_TILE = `url("data:image/svg+xml,${encodeURIComponent(STD_SVG)}")`;

// hex → rgb-триплет для rgba()-нимба; null у некорректного hex
function hexToRgb(hex: string): [number, number, number] | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

// Эффективный цвет проекта как hex: явный ключ палитры → agentDotColor; иначе авто-цвет
// из хеша id (ProjectIcon красит плашку так же при отсутствии icon.color).
function effectiveColorHex(project: Project, iconColor: string | null): string {
  const key = iconColor ?? project.icon?.color ?? null;
  if (key) return agentDotColor(key);
  return projectColor(project.id).main;
}

// Мини-превью холста в скруглённом прямоугольнике: два слоя — мягкий нимб цвета проекта
// и дудл-маска. Для сгенерированного фона маска — тайл проекта, для остальных — стандартная.
function BackdropPreview({
  width, height, project, iconColor, faded,
}: {
  width: number | string; height: number; project: Project; iconColor: string | null; faded?: boolean;
}) {
  const rgb = hexToRgb(effectiveColorHex(project, iconColor));
  const tileUrl = api.projects.backgroundTileUrl(project);
  const maskUrl = tileUrl ? `url("${tileUrl}")` : STD_TILE;
  const maskProps = {
    maskImage: maskUrl, WebkitMaskImage: maskUrl,
    maskSize: '120px', WebkitMaskSize: '120px',
    maskRepeat: 'repeat', WebkitMaskRepeat: 'repeat',
  } as const;
  return (
    <div aria-hidden style={{
      position: 'relative', width, height, flexShrink: 0, borderRadius: R.lg,
      overflow: 'hidden', isolation: 'isolate', background: C.bgMain,
      boxShadow: `inset 0 0 0 1px ${C.borderLight}`, opacity: faded ? 0.65 : 1,
    }}>
      {/* Нимб цвета проекта — мягкий радиальный градиент */}
      <div style={{
        position: 'absolute', inset: 0,
        background: rgb
          ? `radial-gradient(130% 75% at 50% 0%, rgba(${rgb[0]},${rgb[1]},${rgb[2]},0.20), transparent 55%),` +
            `radial-gradient(120% 60% at 50% 110%, rgba(${rgb[0]},${rgb[1]},${rgb[2]},0.12), transparent 60%)`
          : 'radial-gradient(130% 75% at 50% 0%, var(--canvas-glow), transparent 60%)',
      }} />
      {/* Дудл-тушь: цвет линий — нейтральная тушь темы, как в CanvasBackdrop */}
      <div style={{
        position: 'absolute', inset: 0,
        backgroundColor: 'rgba(var(--canvas-ink), var(--canvas-alpha))',
        ...maskProps,
      }} />
    </div>
  );
}

interface Props {
  project: Project;
  // Текущий выбранный цвет иконки в EditDialog (локальный state): фон красится им, а при
  // предложенном цвете с ним сравнивается решение о диалоге
  iconColor: string | null;
  onColorChange: (c: string | null) => void;
  // Проброс обновлённого проекта в стор (фон записан, при colorApplied — и цвет)
  onProjectUpdated: (p: Project) => void;
}

// Секция «Фон рабочего пространства» в настройках проекта.
// Владелец может сгенерировать фон по смыслу и вернуть стандартный; состояние генерации
// и ошибку показывает по продуктовым текстам docs/features/project-backgrounds.md (они
// расходятся с макетом — спека источник правды). Цвет выбран руками, а генерация предлагает
// другой — предупреждаем диалогом, молча не перезаписываем (ADR-008 §5).
export function BackgroundSection({ project, iconColor, onColorChange, onProjectUpdated }: Props) {
  const isMobile = useIsMobile();
  const [busy, setBusy] = useState(false);               // идёт запрос generate/reset
  const [err, setErr] = useState('');                    // сетевая ошибка запроса
  const [confirm, setConfirm] = useState<{ suggested: string } | null>(null);

  const bg = project.background ?? null;
  const serverPending = bg?.kind === 'pending';          // фоновая генерация на бэке (массовый прогон)
  const loading = busy || serverPending;
  const isGenerated = bg?.kind === 'generated';
  const isFailed = bg?.kind === 'failed';
  // «Вернуть стандартный» есть смысл жать, только когда фон нестандартный
  const canReset = !loading && (isGenerated || isFailed);

  // Применить результат generate/reset к проекту локально и пробросить в стор. ADR-008 §5/§6:
  // операции меняют только Background и (при colorApplied) Icon.Color — ничего больше, поэтому
  // свежий проект собираем из ответа без перечитывания.
  const applyResult = (r: BackgroundResult) => {
    const newBg: ProjectBackground = {
      kind: r.kind,
      tileVersion: r.tileVersion ?? null,
      failReason: r.failReason ?? null,
    };
    const appliedColor = r.colorApplied && r.suggestedColorKey ? r.suggestedColorKey : null;
    const updated: Project = {
      ...project,
      background: newBg,
      icon: appliedColor
        ? { ...(project.icon ?? { kind: 'initials' }), color: appliedColor }
        : project.icon,
    };
    onProjectUpdated(updated);
    invalidateProjectsCache();
    // Сервер применил цвет молча (его не было) — тащим в локальный state EditDialog, иначе
    // «Сохранить» ушлёт прежнее (пустое) значение цвета и затёрло бы серверное.
    if (appliedColor) onColorChange(appliedColor);
  };

  const generate = async () => {
    setBusy(true); setErr('');
    try {
      const r = await api.projects.generateBackground(project.id);
      const currentKey = iconColor ?? project.icon?.color ?? null;
      applyResult(r);
      // Цвет выбран руками и генерация предложила другой — не перезаписываем молча:
      // предлагаем смену через диалог. colorApplied=false значит сервер цвет не трогал.
      if (!r.colorApplied && r.suggestedColorKey && r.suggestedColorKey !== currentKey) {
        setConfirm({ suggested: r.suggestedColorKey });
      }
    } catch (e: unknown) {
      setErr(e instanceof Error ? e.message : 'Не удалось сгенерировать фон');
    } finally {
      setBusy(false);
    }
  };

  const reset = async () => {
    setBusy(true); setErr('');
    try {
      const r = await api.projects.resetBackground(project.id);
      applyResult(r);
    } catch (e: unknown) {
      setErr(e instanceof Error ? e.message : 'Не удалось вернуть стандартный фон');
    } finally {
      setBusy(false);
    }
  };

  // Согласие сменить цвет в диалоге: меняем локальный цвет иконки (он же цвет фона),
  // на «Сохранить» уйдёт на сервер. Прежний цвет при отказе сохраняется молчанием —
  // onColorChange не зовём, iconColor не трогаем.
  const acceptColor = () => {
    if (confirm) onColorChange(confirm.suggested);
    setConfirm(null);
  };

  // Подзаголовок статуса под превью: цвет при generated, состояние при генерации/ошибке.
  const colorKey = iconColor ?? project.icon?.color ?? null;
  const statusLine = loading
    ? 'Рисуем фон под этот проект — обычно занимает несколько секунд.'
    : isFailed
      ? 'Не получилось нарисовать фон — оставили стандартный. Можно попробовать ещё раз.'
      : null;
  const isStatusErr = isFailed;

  // Сводка в заголовке аккордеона (docs/mockups/edit-project-compact-proposal.md,
  // словарь сводок). Цвет — только при сгенерированном фоне; иные состояния — текстом.
  const bgSummary: { text: string; tone: AccordionSummaryTone } = loading
    ? { text: 'Рисуем фон…', tone: 'neutral' }
    : isFailed
      ? { text: 'Не получилось', tone: 'err' }
      : isGenerated && colorKey
        ? { text: isMobile ? 'Свой' : `Свой · ${backgroundColorName(colorKey)}`, tone: 'neutral' }
        : { text: 'Стандартный', tone: 'neutral' };

  return (
    <>
      <AccordionSection
        icon={Image}
        title="Фон рабочего пространства"
        summary={bgSummary.text}
        summaryTone={bgSummary.tone}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5 }}>
            Рисунок и цвет подобраны по смыслу проекта. Фон еле заметен и не мешает работе.
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
            <BackdropPreview width={88} height={60} project={project} iconColor={iconColor} faded={loading} />
            <div style={{ minWidth: 0, flex: 1 }}>
              {/* При сгенерированном фоне показываем цвет; генерация/ошибка — статусной строкой */}
              {isGenerated && colorKey && (
                <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, display: 'flex', alignItems: 'center', gap: SP.sm }}>
                  <span style={{ width: 7, height: 7, borderRadius: '50%', background: agentDotColor(colorKey), flexShrink: 0 }} />
                  Цвет — <span style={{ color: C.textPrimary }}>{backgroundColorName(colorKey)}</span>
                </div>
              )}
              {statusLine && (
                <div style={{
                  fontSize: FS.base, fontWeight: isStatusErr ? 600 : 400,
                  color: isStatusErr ? C.dangerText : C.textPrimary, lineHeight: 1.45,
                  display: 'flex', alignItems: 'flex-start', gap: SP.sm,
                }}>
                  {isStatusErr && <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.danger, marginTop: 2 }} />}
                  <span>{statusLine}</span>
                </div>
              )}
              {err && (
                <div style={{ fontSize: FS.sm, color: C.dangerText, marginTop: 2 }}>{err}</div>
              )}
            </div>
          </div>

          <div style={{ display: 'flex', gap: SP.sm, flexDirection: isMobile ? 'column' : 'row' }}>
            <Button variant="primary" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              loading={loading} disabled={loading}
              leftIcon={!loading ? <Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : undefined}
              onClick={generate}>
              Сгенерировать заново
            </Button>
            <Button variant="ghost" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              disabled={!canReset}
              leftIcon={<RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              onClick={reset}>
              Вернуть стандартный
            </Button>
          </div>
        </div>
      </AccordionSection>

      {confirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setConfirm(null)}
          // Без title первым ребёнком идёт превью во всю ширину — встроенный
          // крестик встал бы над ним и отодвинул картинку вниз.
          hideCloseButton
          footer={
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: SP.sm }}>
              <Button variant="ghost" size="md" onClick={() => setConfirm(null)}>
                Оставить прежний
              </Button>
              <Button variant="primary" size="md" onClick={acceptColor}>
                Сменить на {backgroundColorName(confirm.suggested)}
              </Button>
            </div>
          }
        >
          <BackdropPreview width="100%" height={64} project={project} iconColor={confirm.suggested} />
          <div style={{ fontSize: FS.md, lineHeight: 1.55, color: C.textPrimary, marginTop: SP.md }}>
            Цвет проекта изменится на <strong style={{ color: C.accent }}>{backgroundColorName(confirm.suggested)}</strong>. Оставить прежний?
          </div>
        </Modal>
      )}
    </>
  );
}
