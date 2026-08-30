import { describe, it, expect } from 'vitest';
import { rotationBadgeState } from '../rotation';

// Четыре честных состояния бейджа ротации: цель роутинга × в ротации.
// Сценарий прода 2026-07-25: primary исчерпан (неделя 100%), claude-2 выше порога —
// старый бейдж врал «выведен из ротации, чаты идут на свободные», хотя ВСЕ чаты шли на claude-2.
describe('rotationBadgeState', () => {
  it('цель роутинга и в ротации — зелёный «направляются сюда»', () => {
    const s = rotationBadgeState({ inRotation: true, isTarget: true, utilization: 0.3, threshold: 0.8 });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('новые чаты направляются сюда');
  });

  it('цель роутинга, но вне ротации (спилл) — жёлтый «свободных нет» с причиной по нагрузке', () => {
    const s = rotationBadgeState({ inRotation: false, isTarget: true, utilization: 0.91, threshold: 0.8 });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Принимает новые чаты');
    expect(s.reason).toBe('свободных аккаунтов нет — нагрузка 5ч 91% ≥ порога 80%');
  });

  it('цель роутинга, вне ротации, сам исчерпан — «все аккаунты исчерпаны»', () => {
    const s = rotationBadgeState({ inRotation: false, isTarget: true, exhausted: true, utilization: 1, threshold: 0.8 });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Принимает новые чаты');
    expect(s.reason).toBe('свободных аккаунтов нет — все аккаунты исчерпаны');
  });

  it('не цель, вне ротации, свободные есть — «идут на свободные аккаунты»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true, targetName: 'Claude 2',
    });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Выведен из ротации');
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты');
  });

  it('не цель, вне ротации, свободных нет — называет цель по имени, а не «свободные»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: false, targetName: 'Claude 2',
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на «Claude 2»');
  });

  it('не цель, но в ротации — зелёный «может принимать»', () => {
    const s = rotationBadgeState({ inRotation: true, isTarget: false, utilization: 0.5, threshold: 0.8 });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('может принимать новые чаты');
  });

  // Ограничения тарифа — третья ось бейджа: не ломают четыре состояния, но ВСЕГДА
  // подмешиваются в reason как «, кроме ходов Opus и 1M». Сценарий прода 2026-08-23:
  // claude-3 с SupportsOpus=false и Supports1M=false показывался «в ротации», а по
  // факту Pick его не отдавал ~2/3 чатов (opus/opus[1m]). Без оговорки бейдж
  // «направляются сюда» врал. Tone и label не двигаем — только reason.
  it('вне ротации + без Opus — reason упоминает ограничение', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true,
      supportsOpus: false,
    });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Выведен из ротации');
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты, кроме ходов Opus');
  });

  it('вне ротации + без Opus и без 1M — обе способности в суффиксе через «и»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true,
      supportsOpus: false, supports1M: false,
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты, кроме ходов Opus и 1M');
  });

  it('вне ротации + только без 1M — суффикс упоминает только 1M', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: false,
      targetName: 'Claude 2', supports1M: false,
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на «Claude 2», кроме ходов 1M');
  });

  it('спилл (цель, но вне ротации) + без Opus — суффикс тоже подмешивается', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: true, utilization: 0.91, threshold: 0.8,
      supportsOpus: false,
    });
    expect(s.reason).toBe('свободных аккаунтов нет — нагрузка 5ч 91% ≥ порога 80%, кроме ходов Opus');
  });

  it('в ротации + без Opus — reason ВСЕГДА упоминает ограничение, даже на зелёной ветке', () => {
    const s = rotationBadgeState({
      inRotation: true, isTarget: true, utilization: 0.3, threshold: 0.8,
      supportsOpus: false,
    });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('новые чаты направляются сюда, кроме ходов Opus');
  });

  it('в ротации, не цель + без Opus и 1M — оговорка перечисляет оба', () => {
    const s = rotationBadgeState({
      inRotation: true, isTarget: false, utilization: 0.5, threshold: 0.8,
      supportsOpus: false, supports1M: false,
    });
    expect(s.tone).toBe('ok');
    expect(s.reason).toBe('может принимать новые чаты, кроме ходов Opus и 1M');
  });

  it('supportsOpus=true/1M=true — оговорки нет, существующий текст без суффикса', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true,
      supportsOpus: true, supports1M: true,
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты');
  });

  // Поля флагов не пришли (старый бэкенд или бэкенд без конфигурации пула) — undefined
  // НЕ превращается в «не принимает»: отдавать false «по умолчанию» было бы враньём, бэк
  // шлёт null → фронт мапит в undefined и суффикс не подмешивается.
  it('supportsOpus/supports1M не заданы — оговорки нет ни в одной ветке', () => {
    const ok = rotationBadgeState({ inRotation: true, isTarget: true, utilization: 0.3, threshold: 0.8 });
    expect(ok.reason).toBe('новые чаты направляются сюда');
    const warn = rotationBadgeState({ inRotation: false, isTarget: false, exhausted: true, freeAvailable: true });
    expect(warn.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты');
  });

  // Недельное окно — вторая причина вывода из ротации (ClaudeSubscriptionPool.IsOverloaded).
  // Сценарий прода 2026-08-30: claude-2 с 5ч 35% и 7д 99% — старый бейдж объяснял вывод
  // фразой «нагрузка 5ч 35% ≥ порога 80%», то есть прямой неправдой.
  it('вне ротации по недельному окну — причина называет 7д, а не 5ч', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, utilization: 0.35, threshold: 0.8,
      weeklyUtilization: 0.99, weeklyThreshold: 0.95, freeAvailable: true,
    });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Выведен из ротации');
    expect(s.reason).toBe('нагрузка 7д 99% ≥ порога 95% — новые чаты идут на свободные аккаунты');
  });

  it('оба окна выше своих порогов — причина называет оба через «·»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: true, utilization: 0.9, threshold: 0.8,
      weeklyUtilization: 0.97, weeklyThreshold: 0.95,
    });
    expect(s.reason).toBe('свободных аккаунтов нет — нагрузка 5ч 90% ≥ порога 80% · нагрузка 7д 97% ≥ порога 95%');
  });

  it('недельное поле не пришло — причина прежняя, по пятичасовому окну', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, utilization: 0.91, threshold: 0.8, freeAvailable: true,
    });
    expect(s.reason).toBe('нагрузка 5ч 91% ≥ порога 80% — новые чаты идут на свободные аккаунты');
  });

  // Ни одно окно не перешло порог (расхождение снимка и предиката бэка, или аккаунт
  // вне ротации по другой причине — протухший OAuth) — НЕ выдумываем сравнение, а
  // говорим нейтрально. Сценарий прода 2026-08-13: аккаунт мёртв по auth, бейдж
  // показывал «нагрузка 5ч 3% ≥ порога 80%» — оператор читал «перегруз, само пройдёт»
  // и не шёл перелогиниваться. Честную причину (отдельный флаг authDead с бэка) сюда
  // НЕ тащим — это отдельная задача.
  it('вне ротации, но оба окна ниже порогов — фолбэк без выдуманных чисел', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, utilization: 0.2, threshold: 0.8,
      weeklyUtilization: 0.3, weeklyThreshold: 0.95, freeAvailable: true,
    });
    expect(s.reason).toBe('аккаунт недоступен для новых чатов — новые чаты идут на свободные аккаунты');
  });

  // Сырые дроби на границе: 0.796 < 0.8 — бэк аккаунт НЕ выводит, фронт НЕ должен
  // выдумывать «5ч 80% ≥ порога 80%». Сравнение идёт по сырым долям.
  it('сырая доля 0.796 при пороге 0.8 — НЕ считается перегрузом по 5ч', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, utilization: 0.796, threshold: 0.8, freeAvailable: true,
    });
    expect(s.reason).toBe('аккаунт недоступен для новых чатов — новые чаты идут на свободные аккаунты');
  });

  // Симметрично для недельного: 0.947 < 0.95 — сравнение по сырой доле.
  it('сырая доля 0.947 при недельном пороге 0.95 — НЕ считается перегрузом по 7д', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, weeklyUtilization: 0.947, weeklyThreshold: 0.95, freeAvailable: true,
    });
    expect(s.reason).toBe('аккаунт недоступен для новых чатов — новые чаты идут на свободные аккаунты');
  });

  // Ранг тарифа — четвёртая ось: TopTier срезает всё, кроме высшего тарифа набора,
  // поэтому свободный Max 5× при живом Max 20× не получает ни одного нового чата.
  // Тон и label не двигаем — аккаунт не выключен, он резерв.
  it('в ротации, но тариф ниже цели — зелёный «резерв», а не «может принимать»', () => {
    const s = rotationBadgeState({
      inRotation: true, isTarget: false, utilization: 0.1, threshold: 0.8, tierBelowTarget: true,
    });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('резерв — новые чаты идут на аккаунты старшего тарифа');
  });

  it('резерв по тарифу + без Opus — суффикс ограничений сохраняется', () => {
    const s = rotationBadgeState({
      inRotation: true, isTarget: false, utilization: 0.1, threshold: 0.8,
      tierBelowTarget: true, supportsOpus: false,
    });
    expect(s.reason).toBe('резерв — новые чаты идут на аккаунты старшего тарифа, кроме ходов Opus');
  });

  it('тариф ниже цели, но аккаунт вне ротации — причина прежняя, про нагрузку', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, utilization: 0.91, threshold: 0.8,
      tierBelowTarget: true, freeAvailable: true,
    });
    expect(s.label).toBe('Выведен из ротации');
    expect(s.reason).toBe('нагрузка 5ч 91% ≥ порога 80% — новые чаты идут на свободные аккаунты');
  });
});
