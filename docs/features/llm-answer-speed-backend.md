# Скорость ответа: шаг 1, бэкенд

Спека фичи и замороженный контракт REST: docs/features/llm-answer-speed-spec.md.
Дерево: /home/an/Sources/ClaudeCodeServer-wt/llm-answer-speed, ветка feat/llm-answer-speed.

## Сборщик

Новый файл backend/ClaudeHomeServer.Llm/LocalStats/LocalModelAnswerSpeed.cs
и интерфейс ILocalModelAnswerSpeed рядом (по образцу ILocalModelRequestSink).

- Синглтон, TimeProvider из DI, потокобезопасно (один lock).
- Методы, которые вызывает шлюз:
  - OnRequestStarted(requestId, track, startedAt) — запрос пошел вверх.
  - OnFirstToken(requestId, at) — первый токен.
  - OnRequestCompleted(requestId, outputTokens, completedAt) — запрос завершен.
- duration = конец минус старт; ttft = первый токен минус старт (null, если первого
  токена не было); tokens = outputTokens ответа (null — usage не отдали).
- track: main — основной стенд, иначе имя именованного движка
  (LocalModelStatsPoller.MainTrackName).

## Хранение

- Кольцо завершенных запросов за последний час + словарь открытых запросов.
- Старое вычищаем (по времени завершения), число записей ограничено потолком
  (например 50 000 на дорожку) — иначе память растет без границы.
- Открытые запросы без завершения тоже стареют: запись без активности дольше часа
  удаляется.

## Агрегаты

Считаем по окнам 5m и 1h (запросы, завершенные в окне):

- endToEndTps = сумма tokens делить на сумму duration. В суммы идут только запросы,
  у которых известны tokens (не null) и duration больше нуля. Нет таких — null (не 0).
- medianTps — медиана tokens/duration по запросам с tokens не меньше 32
  (порог-константа, отдается наружу вместе с агрегатами).
- medianTtftSec — медиана ttft по всем запросам с ttft.
- p90TtftSec — 90-й перцентиль ttft: ближайший ранг, индекс ceil(0.9*n)-1
  в отсортированном массиве; медиана — среднее двух средних при четном n.
- requests — число завершенных запросов в окне.

## Проводка в шлюзе

Файл backend/ClaudeHomeServer.Llm/Gateway/LlmGatewayEndpoints.cs.

- Момент старта снять непосредственно перед SendAsync запроса вверх.
- Первый токен — на первой content_block_delta (там же, где шлюз зовет
  sink.OnFirstToken).
- Конец — при завершении потока; токены — observer.Complete().OutputTokens.
- Учитывать только локальные маршруты: тот же признак, что у CreateUsageObserver
  (providers.ResolveByModel(route.Model)?.IsLocal == true), и только когда
  LocalModelStatsOptions.Enabled.
- Дорожку брать из LlmProviderRegistry.StatsTrackOf(route.ProviderKey);
  неизвестный ключ — main.
- Наблюдателя и сборщик не создавать без нужды: поведение шлюза без метрик
  не меняется ни на байт.

## REST

В LocalModelStatsResponse (backend/ClaudeHomeServer/Controllers/LocalModelStatsController.cs)
добавить блок answerSpeed по контракту из спеки фичи. Заполнять и для основного
стенда, и для именованного (NamedStatsAsync).

## Тесты

- Сборщик: суммы (endToEndTps), медиана, p90, порог токенов, окна (запрос,
  завершенный вне окна, не входит), пустые данные дают null.
- Шлюз: локальный маршрут попадает в учёт, облачный — нет.
- REST: маппинг блока в DTO.

## Приёмка

- dotnet build зелёный; затронутые тесты зелёные (dotnet test с фильтром по
  затронутым наборам). Полный прогон решения не запускать.
- В отчёте — вывод сборки и тестов, список тронутых файлов.
