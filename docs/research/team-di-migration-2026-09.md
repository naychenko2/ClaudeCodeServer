# Разведка перед шагом 2г-4: перевод штаба на DI (сентябрь 2026)

Провёл Александр (тимлид) 2026-09-06, после закрытия шага 2г-3 и мержа в master.
Разведка заказана вместо прямого исполнения: шаг 2г-4 выглядит механическим
(«снять обёртки, перевести на DI»), но за ним три развилки, которые нельзя
решать на ходу. Предыдущие три разведки этой линии каждый раз сокращали объём.

Состояние на момент разведки: `SessionManager.cs` — 8 999 строк (было 10 124),
вертикаль `Services/Team` из семи сервисов, четыре шва в `TeamCoreSeams.cs`.

## 1. Обёртки: 27, из них снимаются 17

Обёрток-делегатов в вертикаль — **27** (не 28, как в отчёте волны Ж; счёт
уточнён обходом всех обращений к полям `_teamXxx` с подъёмом до объявления
метода). Разрез по вызывающим за пределами `SessionManager.cs` и `Services/Team/`:

**Фасад — снять нельзя, 10 методов.** Их зовёт внешний слой, и после перевода
на DI они останутся ровно теми же:

| Метод | Кто зовёт |
|---|---|
| `SetTeamImplementAsync` | `Controllers/ChatsController.cs`, `CoordinatorWriteGuard.cs` |
| `SetTeamImplementAutoAsync` | `Controllers/ChatsController.cs` |
| `StopTeamImplementAsync` | `Controllers/ChatsController.cs` |
| `RespondTeamPlanAsync` | `Hubs/SessionHub.cs` |
| `RespondTeamEscalationAsync` | `Hubs/SessionHub.cs` |
| `ReportBlockerAsync` | `Services/SessionMessagingService.cs` |
| `TryConsumeTeamWakeup` | `Services/SessionMessagingService.cs` |
| `RefundTeamWakeup` | `Services/SessionMessagingService.cs` |
| `TryConsumeTeamImplementRun` | `Filters/DenyOnDelegatedTurnAttribute.cs` |
| `RefundTeamImplementRun` | `Filters/DenyOnDelegatedTurnAttribute.cs` |

Это не долг. Внешний слой обязан звать вертикаль через какую-то точку входа;
сегодня ей служит `SessionManager`, после DI ей станет `TeamCoordinator` — но
сама точка входа никуда не денется, изменится только адресат инъекции.

**Снимаемые — 17 методов:** `BroadcastTeamImplementAsync`, `CloseTeamTalkAsync`,
`EnterPlanPhaseMode`, `GetOpenTeamEscalationsAsync`, `GetTeamPlanAsync`,
`MarkTeamEscalationRemindedAsync`, `NewTeamImplementBudget`,
`PublishTeamEscalationAsync`, `ResolveStalePlanCardAsync`, `ResolveTeamPlanRoot`,
`RestoreUserMode`, `RunTeamPlanningAsync`, `SaveTeamImplementStateAsync`,
`SaveTeamPlanCardAsync`, `StartTeamWorkAsync`, `SupersedeCurrentPlanCardAsync`,
`WithTeamState`. Ни одного вызова извне ядра и вертикали.

Упоминания `StartTeamWorkAsync` в `Models/Session.cs:264,272` — комментарии,
не вызовы. Проверено.

Сверх 27 обёрток есть четыре публичных метода ядра с телом в ядре, тоже не
зовущиеся снаружи: `ListOpenEscalationsAsync`, `ResolveEscalationAsync`,
`TeamImplementSetupError`, `TryAutoResolveTeamBlockerAsync`.

**Итог по вопросу 1: долг вдвое меньше заявленного** — 17 снимаемых против 10
фасадных, а не «28 обёрток к снятию».

## 2. Публичные методы ядра: честная цена четырёх швов

Из 31 публичного метода ядра, заведённого ради вертикали, после DI останутся
те, что не про штаб вовсе, а про сессию: `GetById`, `GetOwned`, `ResolveOwnerId`,
`ReportUpAsync`, `BroadcastAsync`. Вертикаль будет звать их как обычный
потребитель `SessionManager` — через DI, а не через owning-ссылку.

Цена решения «четыре шва вместо пятого»: **пять постоянных публичных методов
ядра плюс десять фасадных обёрток**. Это дешевле пятого—десятого интерфейса:
интерфейс пришлось бы держать в синхроне с реализацией и объяснять сторожу
границ, а публичный метод ядра review-абелен как есть.

## 3. Func-свойства: цикл не тот, что написан в комментариях

**Главная находка разведки.** Комментарии в коде объясняют четыре
`Func`-свойства разрывом цикла `TaskExecutionService` / `NotificationService`
→ `SessionManager`. Факты этого не подтверждают: комментарии писались до
переезда и устарели.

Все четыре присваиваются **из самой вертикали**, одним методом:

```
Services/Team/TeamWaveService.cs:103  _sessions.TeamWaveStarter = (session, plan, trigger) => StartWaveAsync(...)
Services/Team/TeamWaveService.cs:105  _sessions.TeamEscalationRaiser = RaiseEscalationAsync;
Services/Team/TeamWaveService.cs:107  _sessions.TeamQuestionNotifier = OnStabQuestionAsync;
Services/Team/TeamWaveService.cs:110  _sessions.TeamSubtaskDropHandler = DropSubtaskAsync;
```

А читаются — преимущественно тоже из вертикали, через ядро:

```
Services/Team/TeamDecisionService.cs:229,383,602,623
Services/Team/TeamPlanService.cs:227,233
Services/Team/TeamBudgetService.cs:146
```

Против четырёх чтений в самом ядре (`SessionManager.cs:6826,6929,7215,7244`).

Это круговой маршрут: `TeamWaveService` кладёт обработчик в ядро →
`TeamDecisionService` читает его из ядра → зовёт `TeamWaveService`. Обе стороны
внутри одной вертикали, ядро посередине работает доской объявлений.

**Следствие для 2г-4:** семь из одиннадцати чтений снимаются переносом ссылок
внутрь вертикали (в `TeamCoordinator`) — это правка вертикали, ядра она не
касается вовсе. Настоящий вопрос остаётся только по четырём чтениям в ядре, и
он решается полем `_teamXxx`, которое там уже есть.

Проверить перед правкой: не зовёт ли `TeamWaveService.StartWaveAsync` ядро
обратно в обход — тогда цикл настоящий и `Func` остаётся.

## 4. Тесты: «116» — устаревшая цифра, реальная 308

Вызовов перенесённых методов в тестах — **308** в четырёх файлах:

| Файл | Тестов в файле | Вызовов |
|---|---|---|
| `Services/SessionManagerTests.cs` | 454 | 210 |
| `Services/TeamWaveServiceTests.cs` | 102 | 92 |
| `Controllers/TeamWaveSnapshotApiTests.cs` | 4 | 4 |
| `Controllers/TeamWaveRestartApiTests.cs` | 11 | 2 |

Цифра «116» гуляла по постановкам с начала линии и ни разу не перепроверялась —
она занижена почти втрое.

**Но переписывать их оптом не нужно.** Все 308 вызовов идут через обёртки, а
обёртки при переводе на DI сохраняются: либо как фасад из §1, либо как тонкий
делегат. Снятие 17 обёрток затронет тесты только там, где тест зовёт именно
снимаемый метод, — это подмножество, которое надо мерить перед каждой волной.

## 5. Отдельный `.csproj` для Team — НЕТ

Критерий ADR-014: новая подсистема рождается отдельным проектом, если её
потребности закрываются существующими швами `Core` плюс максимум 1–2 новыми.

**Критерий не проходит, и с большим запасом.** Факты:

- в файлах вертикали **73 упоминания типа `SessionManager`** и 11 — `SessionEntry`
  (внутренний тип ядра);
- `TeamCoreSeams.cs` тянет `ClaudeHomeServer.Models` и `ClaudeHomeServer.Protocol` —
  оба живут в Main, не в `Core`;
- в `Core` из штабного сегодня только `Models/TeamEscalation.cs` и
  `Services/TeamProtocolMarkers.cs`; переезд потребовал бы утащить туда `Session`,
  `SessionTeamImplement`, `TeamImplementPlan`, `TeamWaveTrigger`,
  `TeamImplementStage`, `TeamEscalationKind` и часть `Protocol`;
- `InternalsVisibleTo` объявлен в `ClaudeHomeServer.csproj:11` — на нём стоят все
  тесты вертикали.

Закрыть 73 обращения к ядру «одним-двумя швами» нельзя: это фасад на десятки
методов, то есть второй `LlmSessionContext`, которого разведка Session-ядра
велела избегать.

**Вывод: Team остаётся в Main.** Это не отменяет правила ADR-014 — оно про
подсистемы с узкими потребностями от спины. У штаба они широкие по природе: он
управляет жизненным циклом чата, а чат живёт в ядре.

## Предлагаемая разбивка шага 2г-4

**Поправка от 2026-09-06, внесена после попытки исполнить волну 1.** Первая
редакция этого раздела предлагала отдельную волну «перенести обработчики внутрь
`TeamCoordinator`, ядро не трогаем». Так нельзя, и вот почему:

- `TeamWaveService`, который ставит все четыре обработчика, — синглтон DI
  (`Program.cs:353`, форсированное создание на `Program.cs:837`);
- три сервиса, которые их читают, создаёт сам `SessionManager` в конструкторе
  (`SessionManager.cs:726-771`), а `TeamCoordinator` там же рождается через `new`;
- общего адреса у этих двух групп, кроме самого `SessionManager`, сегодня нет.

Промежуточный вариант — выставить координатор публичным свойством ядра — даёт ту
же доску объявлений под другим именем: вертикаль по-прежнему ходит в ядро за
своей же частью. **Func-свойства снимаются только вместе с переводом вертикали
на DI**, отдельной дешёвой волны для них не существует.

Исполнимая разбивка:

- **Волна 1 — регистрация вертикали в DI.** `TeamCoordinator` зависит только от
  `IHubContext<SessionHub>` (`TeamCoordinator.cs:25`) — циклов нет, он идёт в DI
  первым. За ним остальные шесть сервисов; циклы `SessionManager ⇄ TeamXxx`
  развязываются фабрикой или `Lazy<T>`. Осторожно: конструктор `SessionManager`
  вызывается в 21 месте, из них 19 — тесты, поэтому новые параметры обязаны быть
  опциональными.
- **Волна 2 — снять четыре `Func`-свойства**, когда у вертикали появился общий
  адрес. 7 из 11 чтений уходят внутрь вертикали, 4 чтения ядра переходят на поле
  `_teamCoordinator`.
- **Волна 3 — снять 17 обёрток**, не зовущихся снаружи. Перед началом — точный
  замер, сколько тестов зовёт именно эти 17 (ожидание: заметно меньше 308).
- **Волна 4 (шаг 2д):** документация — ADR-013, ADR-014, CLAUDE.md.

## Чего делать НЕ надо

1. **Не снимать 10 фасадных обёрток** — они не долг, а точка входа внешнего слоя.
2. **Не переписывать `SessionManagerTests` оптом** (§4): 454 теста в файле — это
   тесты сессии, а не штаба; трогать их ради адресата вызова значит вносить риск
   в самый населённый тестовый файл проекта ради косметики.
3. **Не выделять Team отдельным `.csproj`** (§5).
4. **Не верить комментариям про циклы DI** у `Func`-свойств — они устарели (§3).

## Итог: как прошли волны (сверено с кодом 2026-09-22)

Разбивка выше исполнена, план разведки подтвердился по всем трём волнам.

**Обёрток в ядре 27 → 10.** Волна 3 (2026-09-11) сняла 11 снимаемых обёрток
и три метода, оказавшихся мёртвыми: `ResolveTeamPlanRoot`, `StartTeamWorkAsync`,
`CloseTeamTalkAsync` — их не звал никто (сегодня в `SessionManager.cs` от них
остались только упоминания в комментариях). Сервисы штаба зовут siblings
напрямую через DI; ядро перестало быть доской объявлений для штабной логики —
из штабного в нём остались базовые операции (`GetById`, `BroadcastAsync`,
`SaveSessions`). Оставшиеся 10 — фасад внешнего слоя (`ChatsController`,
`SessionHub`, `SessionMessagingService`, `DenyOnDelegatedTurnAttribute`),
и они не долг: точка входа нужна в любом случае.

**`HasLiveDelegatedTasks` осознанно остаётся `Func`-свойством** — единственным
из четырёх. Причина не в забывчивости: его ставит `TaskExecutionService`
(`_sessions.HasLiveDelegatedTasks = HasLiveDelegatedTask;`), то есть чужая
сторона, и цикл здесь настоящий. Прямая ссылка на `TaskManager` из спины
уронила бы сторож границ — `Tasks` вынесена в отдельную вертикаль и имеет
свою строку в `Boundaries`. Узкий `Func` дешевле шва ровно потому, что
потребитель один (`SessionManager`, единственное чтение —
`HasLiveDelegatedTasks?.Invoke(sessionId) == true`).

**Отдельным `.csproj` Team по-прежнему не выносится** (§5 в силе). Замер
обращений к типу `SessionManager` из файлов вертикали на 2026-09-22 — **74**
(в разведке было 73, в карте проекта фигурировало 67). Цифра не растёт по
смыслу: после волны 3 все эти обращения — базовые операции ядра, а не штабная
логика. Вынос потребовал бы фасада на десятки методов — второго
`LlmSessionContext`.
