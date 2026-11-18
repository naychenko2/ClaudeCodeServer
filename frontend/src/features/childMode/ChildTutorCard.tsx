// Т4e: карточка репетитора профиля ребёнка (макет Д1, группа «Репетитор»,
// docs/mockups/kids-tutors-proposal.md). Все пути данных — админские ручки (И5):
// PUT child-tutors/{personaId}, …/avatar/generate|upload|select. Картинки карточки —
// только админские URL childTutorAvatarUrl/childTutorCandidateUrl: общие /api/personas/*
// ищут персону по арендатору, админу персона ребёнка — 404 (Т2c3).
// Форма ключуется personaId: смена — черновик и кандидаты сброшены; ответ проверяется
// id на момент прихода (тот же образец, что ChildProfileEditor).
// Голос после сохранения — значение формы, не из ответа: putTutor возвращает снимок
// персоны ДО применения голоса (ревью Т4a, ChildTutorsController.cs:123/152/161).

import { useRef, useState } from 'react';
import { AlertTriangle, Sparkles, Upload } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button } from '../../components/ui/Button';
import { Field, FieldLabel, TextArea, TextField } from '../../components/ui/Field';
import { Notice } from '../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { userAvatarColor } from '../../components/ui/UserAvatar';
import { useIsMobile } from '../../lib/breakpoints';
import { cachedToken } from '../../lib/offline';
import { personaInitials } from '../../lib/personas';
import { SectionLabel } from '../tasks/bits';
import { PersonaVoicePicker } from '../personas/PersonaVoicePicker';
import { AvatarCropDialog, type AvatarCropResult } from '../personas/AvatarCropDialog';
import { childApi, childTutorAvatarUrl, childTutorCandidateUrl } from './api';
import type { ChildTutorPatch } from './types';
import type { PersonaVoice } from '../../types';

// Предзаполнение формы: то, что знает вызывающий (из своего состояния профиля).
// Своего GET-чтения репетитора в контракте нет (И5) — карточка ничего не догружает.
export interface ChildTutorInitial {
  name?: string;
  character?: string;
  mustDo?: string[];
  mustNot?: string[];
  voice?: PersonaVoice | null;
}

interface Props {
  userId: string;
  // Пусто — репетитора ещё нет (несохранённый предмет): форма не показывается вовсе
  personaId: string;
  initial?: ChildTutorInitial | null;
  onSaved?: (name: string) => void;
}

// Черновик — строки полей ввода; списки — textarea «строка = пункт» (как mustDo/mustNot
// в PersonaForm.tsx:594 и PersonaWizard), parseLines при сохранении.
interface Draft { name: string; character: string; mustDo: string; mustNot: string; voice: PersonaVoice | null }
interface Snapshot { personaId: string; draft: Draft }

const draftOf = (initial?: ChildTutorInitial | null): Draft => ({
  name: initial?.name ?? '',
  character: initial?.character ?? '',
  mustDo: (initial?.mustDo ?? []).join('\n'),
  mustNot: (initial?.mustNot ?? []).join('\n'),
  voice: initial?.voice ?? null,
});
const snapOf = (personaId: string, initial?: ChildTutorInitial | null): Snapshot =>
  ({ personaId, draft: draftOf(initial) });

// «Строка = пункт» → массив правил (пустые строки — не пункты), как parseLines в PersonaForm
const parseLines = (s: string): string[] => s.split('\n').map((l) => l.trim()).filter(Boolean);
const lineCount = (s: string): number => parseLines(s).length;

const errText = (e: unknown): string =>
  (e instanceof Error && e.message.trim()) ? e.message.trim() : 'Не удалось сохранить репетитора';
// Отказ ручки несёт человеческий текст в body.message (ADR-041, «Общие правила») —
// показываем его как есть; message пустой (403/404 без тела) — общий текст.
function rejectText(e: unknown): string {
  const body = (e as { body?: { message?: string } } | null)?.body;
  const m = typeof body?.message === 'string' ? body.message.trim() : '';
  return m || errText(e);
}

export function ChildTutorCard({ userId, personaId, initial, onSaved }: Props) {
  const isMobile = useIsMobile();
  const [snap, setSnap] = useState<Snapshot>(() => snapOf(personaId, initial));
  // Кандидаты генерации: живут до выбора/ухода с карточки; смена репетитора — пусты.
  const [candidates, setCandidates] = useState<string[]>([]);
  // Пропал файл аватара с диска (404 на GET) — карточка возвращается к инициалам
  // (тот приём, что у TutorMark/PersonaAvatar: onError, не предпроверка).
  const [imgFailed, setImgFailed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [selecting, setSelecting] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [crop, setCrop] = useState<{ src: string; file: File } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  // Текущий репетитор на момент прихода ответа: по нему отбрасываем устаревший ответ
  // (тот же механизм, что activeIdRef в ChildProfileEditor).
  const activeIdRef = useRef(personaId);
  activeIdRef.current = personaId;
  // Занятость каждой операции — ref, а не state: два клика до перерисовки видят одно
  // замкнутое состояние, и второй запрос не стартует (тот же приём, что pendingReturnRef
  // в PersonaForm).
  const saveRef = useRef(false);
  const genRef = useRef(false);
  const selectRef = useRef(false);
  const uploadRef = useRef(false);

  // Смена репетитора — новый снимок по новому id: черновик и кандидаты сброшены,
  // ошибка прежнего и его открытое окно кропа не переносятся.
  if (snap.personaId !== personaId) {
    setSnap(snapOf(personaId, initial));
    setCandidates([]);
    setImgFailed(false);
    setError(null);
    setCrop(null);
  }
  const d = snap.personaId === personaId ? snap.draft : draftOf(initial);

  const patch = (fn: (prev: Draft) => Draft) =>
    setSnap((s) => (s.personaId === personaId ? { ...s, draft: fn(s.draft) } : s));

  const save = async () => {
    if (!personaId || saveRef.current) return;
    const name = d.name.trim();
    if (!name) return; // имени нет — сохранять нечего: «Сохранить» гасится, запроса нет
    saveRef.current = true;
    setSaving(true);
    setError(null);
    const forId = personaId;
    const body: ChildTutorPatch = {
      name,
      // Пустой характер — явная очистка (''), отсутствие — «не менять»: поле всегда в теле,
      // чтобы стирание поля в карточке сохранилось.
      character: d.character.trim(),
      // Пустое поле — явная очистка списка ([]), не «не менять» (контракт ChildTutorPatch)
      mustDo: parseLines(d.mustDo),
      mustNot: parseLines(d.mustNot),
      // Голос из формы; не задан — поля нет (null на бэке = «не трогать»)
      voice: d.voice ?? undefined,
    };
    try {
      const saved = await childApi.putTutor(userId, forId, body);
      // Ответ прежнего репетитора: не применяется и не держит кнопку в загрузке —
      // у нового своего запроса нет (второй не стартует, пока занят).
      if (activeIdRef.current !== forId) { setSaving(false); return; }
      // Снимок персоны в ответе — ДО применения голоса (ревью Т4a): в пикере остаётся
      // выбранное значение формы; из ответа не берём ничего, кроме имени для родителя.
      onSaved?.(saved?.name || name);
    } catch (e) {
      if (activeIdRef.current === forId) setError(rejectText(e));
    } finally {
      setSaving(false);
      saveRef.current = false;
    }
  };

  const generate = async () => {
    if (!personaId || genRef.current) return;
    genRef.current = true;
    setGenerating(true);
    setError(null);
    const forId = personaId;
    try {
      const r = await childApi.tutorAvatar.generate(userId, forId);
      // Кандидаты прежнего репетитора новому не показываются
      if (activeIdRef.current !== forId) return;
      setCandidates(r.candidates ?? []);
    } catch (e) {
      if (activeIdRef.current === forId) setError(rejectText(e));
    } finally {
      setGenerating(false);
      genRef.current = false;
    }
  };

  const chooseCandidate = async (file: string) => {
    if (selectRef.current) return; // выбор идёт — повторный клик игнорируется
    selectRef.current = true;
    setSelecting(file);
    setError(null);
    const forId = personaId;
    try {
      await childApi.tutorAvatar.select(userId, forId, file);
      if (activeIdRef.current !== forId) return;
      // Выбранный кандидат — аватар: сетка больше не нужна, инициалы уходят
      setCandidates([]);
      setImgFailed(false);
    } catch (e) {
      if (activeIdRef.current === forId) setError(rejectText(e));
    } finally {
      setSelecting(null);
      selectRef.current = false;
    }
  };

  const onFileChosen = (file: File | null) => {
    if (!file) return;
    setCrop({ src: URL.createObjectURL(file), file });
  };

  const applyUpload = async (result: AvatarCropResult) => {
    if (!personaId || !crop?.file || uploadRef.current) return;
    uploadRef.current = true;
    setUploading(true);
    setError(null);
    const forId = personaId;
    try {
      await childApi.tutorAvatar.upload(userId, forId, crop.file, result.blob, result.crop);
      if (activeIdRef.current !== forId) return;
      if (crop.src) URL.revokeObjectURL(crop.src);
      setCrop(null);
      setImgFailed(false);
    } catch (e) {
      if (activeIdRef.current === forId) setError(rejectText(e));
    } finally {
      setUploading(false);
      uploadRef.current = false;
    }
  };

  const busy = saving || generating || !!selecting || uploading;
  const btnSize = isMobile ? 'md' : 'sm';
  const name = d.name.trim();

  const avatar = imgFailed
    ? (
      <span
        aria-hidden
        style={{
          width: 64, height: 64, borderRadius: R.full, flexShrink: 0,
          background: userAvatarColor(personaId), color: C.onDark,
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          fontFamily: FONT.sans, fontWeight: 700, fontSize: 26, lineHeight: 1, userSelect: 'none',
        }}
      >
        {personaInitials(name || 'Репетитор')}
      </span>
    )
    : (
      <img
        src={childTutorAvatarUrl(userId, personaId, 64, cachedToken() ?? undefined)}
        alt=""
        aria-hidden
        onError={() => setImgFailed(true)}
        style={{ width: 64, height: 64, borderRadius: R.full, flexShrink: 0, objectFit: 'cover', display: 'block' }}
      />
    );

  const rulesField = (label: string, value: string, setValue: (v: string) => void, placeholder: string) => {
    const n = lineCount(value);
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: SP.sm }}>
          <FieldLabel>{label}</FieldLabel>
          {/* счётчик «n/12» — как в макете (.cnt-l .muted, 12px); за пределом — красным */}
          <span style={{ fontSize: 12, color: n > 12 ? C.dangerText : C.textMuted, flexShrink: 0 }}>
            {n}/12
          </span>
        </div>
        <TextArea
          value={value}
          onChange={setValue}
          autoGrow
          minHeight={72}
          voice
          disabled={busy}
          ariaLabel={label}
          placeholder={placeholder}
        />
      </div>
    );
  };

  if (!personaId) {
    return (
      <div data-testid="child-tutor-card">
        <SectionLabel>Репетитор</SectionLabel>
        <div style={{
          marginTop: SP.sm, padding: `${SP.md}px ${SP.md}px`, border: `1.5px dashed ${C.dashed}`,
          borderRadius: R.lg, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45,
        }}>
          Репетитор появится после сохранения профиля
        </div>
      </div>
    );
  }

  return (
    <div data-testid="child-tutor-card">
      <SectionLabel>Репетитор</SectionLabel>

      {error && (
        <Notice tone="danger" icon={AlertTriangle} title="Сохранить не удалось" style={{ marginTop: SP.sm }}>
          {error}
        </Notice>
      )}

      {/* Аватар и действия (макет .tutor): на мобиле — колонка */}
      <div style={{
        display: 'flex', gap: SP.md, alignItems: 'flex-start',
        flexDirection: isMobile ? 'column' : 'row', marginTop: SP.sm,
      }}>
        {avatar}
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
            <Button
              variant="ghostAccent"
              size={btnSize}
              loading={generating}
              disabled={busy}
              leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              onClick={() => void generate()}
            >
              {generating ? 'Генерирую…' : 'Сгенерировать'}
            </Button>
            <Button
              variant="ghostAccent"
              size={btnSize}
              loading={uploading}
              disabled={busy}
              leftIcon={<Upload size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              onClick={() => fileInputRef.current?.click()}
            >
              Загрузить
            </Button>
          </div>
          <span style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.45 }}>
            Без аватара на плитке будут инициалы. Генерация списывается с бюджета администратора, не ребёнка.
          </span>
        </div>
      </div>

      {/* Скрытый input выбора файла аватара → диалог кропа (тот путь, что у персон) */}
      <input
        ref={fileInputRef}
        type="file"
        accept="image/jpeg,image/png,image/webp"
        style={{ display: 'none' }}
        onChange={(e) => { onFileChosen(e.target.files?.[0] ?? null); e.target.value = ''; }}
      />
      {crop && (
        <AvatarCropDialog
          src={crop.src}
          title="Аватар репетитора"
          onApply={(r) => void applyUpload(r)}
          onClose={() => { URL.revokeObjectURL(crop.src); setCrop(null); }}
        />
      )}

      {/* Кандидаты генерания: сетка, клик — выбор (аватар меняется только выбором) */}
      {candidates.length > 0 && !generating && (
        <div
          role="group"
          aria-label="Кандидаты аватара"
          style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: SP.sm, maxWidth: 240, marginTop: SP.sm }}
        >
          {candidates.map((file) => {
            const itemBusy = selecting === file;
            return (
              <button
                key={file}
                type="button"
                onClick={() => void chooseCandidate(file)}
                disabled={!!selecting || busy}
                title="Выбрать этот аватар"
                style={{
                  position: 'relative', padding: 0, border: `2px solid ${C.border}`, background: C.bgWhite,
                  borderRadius: R.lg, cursor: selecting ? 'default' : 'pointer', aspectRatio: '1 / 1',
                  overflow: 'hidden', opacity: selecting && !itemBusy ? 0.5 : 1,
                }}
              >
                <img
                  src={childTutorCandidateUrl(userId, personaId, file, cachedToken() ?? undefined)}
                  alt="Вариант аватара"
                  style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
                />
                {itemBusy && (
                  <span style={{
                    position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
                    background: 'rgba(0,0,0,0.35)', color: C.onDark, fontSize: FS.xs, fontFamily: FONT.sans,
                  }}>Применяю…</span>
                )}
              </button>
            );
          })}
        </div>
      )}

      <div style={{ marginTop: SP.md }}>
        <Field label="Имя">
          <TextField value={d.name} onChange={(v) => patch((p) => ({ ...p, name: v }))} placeholder="Сова Ульяна" disabled={busy} voice />
        </Field>
      </div>

      <div style={{ marginTop: SP.md }}>
        <Field label="Голос" hint="Как репетитор звучит в голосовом режиме">
          <PersonaVoicePicker
            value={d.voice}
            onChange={(v) => patch((p) => ({ ...p, voice: v }))}
          />
        </Field>
      </div>

      <div style={{ marginTop: SP.md }}>
        <Field
          label="Характер"
          hint="Правила безопасности детского режима добавятся сами — этим полем их не отменить"
        >
          <TextArea
            value={d.character}
            onChange={(v) => patch((p) => ({ ...p, character: v }))}
            autoGrow
            minHeight={88}
            voice
            disabled={busy}
            ariaLabel="Характер"
            placeholder="Как репетитор говорит и объясняет: «добрый лесовичок, рассказывает про природу на примерах из леса»"
          />
        </Field>
      </div>

      <div style={{
        marginTop: SP.md, display: 'grid',
        gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: SP.md,
      }}>
        {rulesField(
          'Обязательно', d.mustDo, (v) => patch((p) => ({ ...p, mustDo: v })),
          'Хвали за попытку, даже если ответ неверный\nПроси рассказать, как получилось',
        )}
        {rulesField(
          'Никогда', d.mustNot, (v) => patch((p) => ({ ...p, mustNot: v })),
          'Не торопи с ответом\nНе сравнивай с другими детьми',
        )}
      </div>
      <div style={{ marginTop: SP.xs, fontSize: 11.5, color: C.textMuted, lineHeight: 1.45 }}>
        Правила безопасности и «не решать за ученицу» действуют поверх этих списков
      </div>

      <div style={{ marginTop: SP.md }}>
        <Button
          variant="primary"
          size={isMobile ? 'md' : 'sm'}
          loading={saving}
          disabled={!name || busy}
          onClick={() => void save()}
        >
          {saving ? 'Сохраняю…' : 'Сохранить'}
        </Button>
      </div>
    </div>
  );
}
