// S2b-3b (часть B): текст телеметрии о выключенном разделе — с общего источника.
// «Раздел …» + причина — из SUBSYSTEM_DISABLED_REASON (lib/subsystems.ts, часть A);
// уточняющий хвост в IncidentsPanel («… — инциденты никто не собирает.») —
// свой, телеметрийный: он говорит, что именно выключено.
//
// Сторож по исходнику (образец — no-hardcoded-specialty-labels.test.ts):
// TelemetryPage рисует заглушку только после статуса с бэка, статический рендер
// в эту ветку не доезжает — поэтому держится составление текста: импорт константы,
// шаблон без литерала и наличие уточняющего хвоста.
//
// Мутация этого прогона (вывод — в отчёте задачи): вернуть в TelemetryPage.tsx
// литерал текста в обход константы → тест краснеет на проверке шаблона.
//
// Литерал причины собран в тесте из двух частей: чтобы grep «литерала вне
// константы в features/telemetry» оставался чистым даже внутри самого сторожа.

import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';

const DISABLED_LITERAL = 'выключен ' + 'администратором';

const pageSrc = readFileSync(new URL('./TelemetryPage.tsx', import.meta.url), 'utf8');
const panelSrc = readFileSync(new URL('./IncidentsPanel.tsx', import.meta.url), 'utf8');

describe('Тексты выключенного раздела телеметрии (S2b-3b, часть B)', () => {
  it('TelemetryPage: подзаголовок собран из SUBSYSTEM_DISABLED_REASON, литерала нет', () => {
    expect(pageSrc).toContain("from '../../lib/subsystems'");
    expect(pageSrc).toContain('Раздел ${SUBSYSTEM_DISABLED_REASON}.');
    expect(pageSrc).not.toContain(DISABLED_LITERAL);
  });

  it('IncidentsPanel: общая причина — из константы, хвост про инциденты на месте', () => {
    expect(panelSrc).toContain("from '../../lib/subsystems'");
    expect(panelSrc).toContain('Раздел ${SUBSYSTEM_DISABLED_REASON} — инциденты никто не собирает.');
    expect(panelSrc).not.toContain(DISABLED_LITERAL);
  });
});
