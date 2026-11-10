# OpsMind — архитектура MVP

**Статус:** implementation baseline
**Версия:** 1.0
**Связанные документы:** [Product requirements](product-requirements.md), [System requirements](system-requirements.md), [Evaluation dataset](evaluation-dataset.md)

## 1. Назначение документа

Документ фиксирует архитектурные решения, достаточные для начала разработки OpsMind. Он определяет границы компонентов, направления зависимостей, модель хранения, ключевые потоки, требования к безопасности и эксплуатации, а также последовательность реализации.

Product requirements отвечают на вопрос «что и зачем строим», system requirements — «каким требованиям должна соответствовать система», этот документ — «как именно организуем реализацию».

Архитектура рассчитана на MVP-профиль до 20 организаций, 200 workspace, 20 000 активных документов, 2 млн активных chunks и 25 одновременных вопросов. Решения за пределами этого профиля принимаются по результатам измерений, а не заранее.

## 2. Краткое решение

OpsMind строится как модульный монолит с тремя исполняемыми процессами из одной .NET solution:

- `OpsMind.Api` — HTTP API, авторизация, SSE и application orchestration;
- `OpsMind.Worker` — надёжная фоновая обработка документов и cleanup/reconciliation;
- `OpsMind.Migrator` — одноразовое применение миграций и инфраструктурных проверок.

PostgreSQL является system of record для бизнес-состояния, полнотекстового поиска, vectors и durable background commands. Исходные файлы хранятся в S3-compatible object storage. Внешние LLM, embedding и reranking providers подключаются через application ports.

```text
Client
  |
  v
OpsMind.Api --------------------------+
  | auth / upload / question / SSE    |
  |                                   v
  +----------------------------> PostgreSQL 18 + pgvector
  |                               state, outbox, FTS, vectors,
  |                               history, audit, retrieval runs
  |
  +----------------------------> Object Storage
  |                               immutable source files
  |
  +----------------------------> AI Providers
                                  embeddings, reranker, chat

OpsMind.Worker <---------------- PostgreSQL outbox/job leases
  |
  +--> Object Storage -> parse -> chunk -> embed -> stage -> publish

All processes --> OpenTelemetry Collector --> traces / metrics / logs
```

## 3. Архитектурные драйверы

Решения ниже имеют приоритет над удобством отдельных библиотек:

1. **Grounding:** ответ не содержит фактов вне переданного model context.
2. **Tenant isolation:** недоступный chunk не должен попасть даже в candidates, cache, reranker или prompt.
3. **Проверяемость:** цитата указывает на конкретный неизменяемый snapshot источника.
4. **Измеримость:** retrieval, reranking и generation оцениваются независимо.
5. **Воспроизводимость:** model, prompt, index profile и параметры retrieval фиксируются для каждого ответа.
6. **Атомарность публикации:** неполный индекс никогда не участвует в поиске.
7. **Идемпотентность:** повтор команды или доставка at-least-once не создаёт дубликаты.
8. **Заменяемость providers:** доменная и application-логика не зависит от SDK провайдера.
9. **Минимум инфраструктуры:** в MVP нет отдельного message broker, Redis или vector database без измеренной необходимости.
10. **Эволюция без подмены источника:** смена chunker/embedding model создаёт новый `DocumentIndex`, а не новую версию документа.

## 4. Зафиксированные технологические решения

| Область | Решение | Причина |
|---|---|---|
| Runtime | .NET 10 LTS, последняя servicing patch | Активный LTS; .NET 8/9 завершают поддержку на дату документа |
| Web | ASP.NET Core 10, Minimal APIs с route groups | Компактные contracts, встроенные Problem Details, rate limiting и SSE |
| Data access | EF Core 10 для transactional state; Npgsql/Dapper-style SQL для retrieval и bulk paths | Не усложнять CRUD, сохранив контроль над vector/FTS SQL и query plans |
| Database | PostgreSQL 18, последняя minor release | Текущий стабильный major; PostgreSQL 19 ещё не GA на дату документа |
| Vector search | pgvector 0.8.x, HNSW + iterative scans | Векторный поиск рядом с metadata/FTS и поддержка filtered ANN |
| AI abstractions | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) | Provider-neutral integration, DI, telemetry middleware |
| Reranker | Собственный application port `IReranker` | В MEAI нет обязательной общей reranking abstraction; providers различаются |
| Streaming | ASP.NET Core `ServerSentEvents` / `SseItem<T>` | Однонаправленный поток без состояния SignalR hub |
| Files | `IBlobStorage`; MinIO локально, S3-compatible storage в deployment | Файлы не раздувают PostgreSQL и имеют отдельный lifecycle |
| Background work | PostgreSQL transactional outbox-as-queue с lease | Нет dual write; достаточно для расчётной нагрузки; transport заменяем |
| Telemetry | OpenTelemetry traces, metrics и logs через Collector | Единая сквозная диагностика API, worker, DB и AI calls |
| API description | OpenAPI; SSE events описываются отдельными DTO | Проверяемые contracts и генерация клиентов |

Точные NuGet versions централизуются в `Directory.Packages.props`, SDK — в `global.json`, зависимости фиксируются lock-файлами. Preview-пакеты в основной ветке запрещены без ADR.

## 5. Архитектурный стиль и границы модулей

### 5.1 Почему модульный монолит

MVP не требует распределённых транзакций между собственными сервисами. Разделение на микросервисы увеличило бы стоимость tenant security, tracing, локального запуска и изменения контрактов. При этом API и worker уже являются отдельными процессами и масштабируются независимо.

Модули общаются через application interfaces и команды, но остаются в одной solution и одной базе. Выделение модуля в сервис допустимо позднее, если измеренная нагрузка или ownership этого потребуют.

### 5.2 Модули

| Модуль | Ответственность | Не отвечает за |
|---|---|---|
| `IdentityAccess` | Users, organizations, membership, roles, `AccessScope` | JWT issuance и корпоративный IdP |
| `Knowledge` | Documents, versions, blobs, parsing, chunking, index lifecycle | Ответы LLM и conversations |
| `Retrieval` | Query embedding, vector/FTS candidates, fusion, reranking, evidence gate | HTTP и хранение исходных файлов |
| `Answering` | Prompt assembly, generation, streaming, citation validation, confidence | Поиск недоступных chunks |
| `Conversations` | Questions, messages, answer/citation snapshots, history | Multi-turn query rewriting в MVP |
| `Operations` | Outbox, leases, idempotency, audit, quotas, usage, reconciliation | Бизнес-правила документов |
| `Observability` | Activity/Meter conventions, redaction, cost and token metrics | Хранение пользовательского контента |

### 5.3 Инварианты границ

- Application layer принимает `AccessScope`, а не отдельные необязательные `organizationId/workspaceId`.
- Retrieval нельзя вызвать без `AccessScope` и `IndexProfileId`.
- Answering принимает только готовый `GroundingContext`, а не repository или произвольный SQL.
- Provider SDK types не выходят из Infrastructure.
- HTTP DTO не используются как domain entities.
- Нет generic `IRepository<T>`; используются явные command stores и query services.
- Каждая внешняя операция принимает `CancellationToken`, timeout и telemetry context.

## 6. Структура solution

```text
OpsMind.sln
Directory.Build.props
Directory.Packages.props
global.json

src/
  OpsMind.Api/                 HTTP endpoints, auth, SSE, composition root
  OpsMind.Worker/              outbox polling, indexing, cleanup, reconciliation
  OpsMind.Migrator/            migrations and startup checks
  OpsMind.Domain/              entities, value objects, invariants, state machines
  OpsMind.Application/         use cases, ports, policies, DTO-independent models
  OpsMind.Infrastructure/      PostgreSQL, blobs, parsers, AI provider adapters
  OpsMind.Contracts/           versioned HTTP request/response/SSE contracts

tests/
  OpsMind.UnitTests/
  OpsMind.IntegrationTests/
  OpsMind.ApiTests/
  OpsMind.ArchitectureTests/
  OpsMind.EvaluationTests/
  OpsMind.LoadTests/
  TestData/

deploy/
  compose.yaml
  otel-collector.yaml
  grafana/
```

Направление project references:

```text
Domain <- Application <- Infrastructure
              ^              ^
              |              |
Contracts <- Api          Worker / Migrator
```

- `Domain` не имеет project/package dependencies на persistence, HTTP или AI.
- `Application` ссылается только на `Domain` и определяет ports/use cases.
- `Infrastructure` реализует application ports и ссылается на `Application`/`Domain`.
- `Contracts` не ссылается на Domain и содержит только versioned transport types.
- `Api`, `Worker` и `Migrator` являются composition roots; они подключают Infrastructure, но не содержат бизнес-правила.

## 7. Ключевые application ports

```csharp
public interface IAccessScopeResolver { }
public interface IBlobStorage { }
public interface IDocumentParser { }
public interface ITextChunker { }
public interface IEmbeddingService { }
public interface IHybridRetriever { }
public interface IReranker { }
public interface IAnswerGenerator { }
public interface ICitationValidator { }
public interface IConfidencePolicy { }
public interface IOutboxStore { }
public interface IJobLeaseStore { }
public interface IClock { }
```

Практические правила:

- `IDocumentParser` реализуется registry по allow-listed MIME, а не большим `switch` в worker.
- `IEmbeddingService` адаптирует MEAI `IEmbeddingGenerator`, проверяет dimensions и model key.
- `IAnswerGenerator` адаптирует MEAI `IChatClient`, но наружу отдаёт application events.
- `IHybridRetriever` возвращает диагностируемые результаты каждого stage, а не только финальные chunks.
- `IClock` используется в state machines, leases и тестах.
- Interfaces добавляются на реальной границе side effect/provider, а не для каждого класса.

## 8. Архитектура данных

### 8.1 Общие правила

- Идентификаторы создаются приложением как UUIDv7 для лучшей локальности B-tree.
- Время хранится как `timestamptz` в UTC.
- Core access/filter fields являются типизированными columns; JSONB используется только для расширяемых metadata snapshots.
- Все tenant-owned tables содержат `organization_id` и `workspace_id` напрямую либо имеют проверяемый composite FK.
- Mutable aggregates имеют explicit `row_version bigint` как optimistic concurrency token.
- Удаление сначала логическое (`deleted_at/state`), затем physical cleanup.
- Core schema изменяется только versioned migrations. API/Worker не запускают migrations при старте.

### 8.2 PostgreSQL schemas и таблицы

#### `iam`

| Таблица | Основные поля/ограничения |
|---|---|
| `users` | `id`, `issuer`, `subject`, display fields; unique `(issuer, subject)` |
| `organizations` | `id`, `name`, `state`, timestamps |
| `organization_members` | `(organization_id, user_id)`, state |
| `workspaces` | `id`, `organization_id`, `slug`, state; unique `(organization_id, slug)` |
| `workspace_members` | `(organization_id, workspace_id, user_id)`, role; composite FKs к organization/workspace membership |

#### `knowledge`

| Таблица | Основные поля/ограничения |
|---|---|
| `documents` | tenant/workspace, title, state, `active_version_id`, `row_version` |
| `document_versions` | tenant/workspace/document, revision, blob key, SHA-256, MIME, applicable metadata; unique `(document_id, revision)` |
| `index_profiles` | workspace, status, parser/chunker config, embedding model, FTS/fusion/reranker config, policy versions |
| `document_indexes` | version + profile, status/stage/error, counts; unique `(document_version_id, index_profile_id)` |
| `chunks` | document index, ordinal, heading path, normalized content, source coordinates, token count, content hash, FTS vectors |
| `chunk_embeddings` | chunk, embedding model key, tenant/workspace, unbounded `vector`; separated from text rows |

#### `chat`

| Таблица | Основные поля/ограничения |
|---|---|
| `conversations` | tenant/workspace, owner, title, state |
| `messages` | conversation, role, status, content, model/prompt versions, usage and trace ID |
| `answer_citations` | answer, source chunk/version, immutable quote/metadata snapshot, coordinates, scores |

#### `rag`

| Таблица | Основные поля/ограничения |
|---|---|
| `retrieval_runs` | question, profile, filters/config snapshot, outcome, timings, confidence inputs |
| `retrieval_candidates` | run, stage (`vector`, `fts`, `fusion`, `rerank`), chunk, rank, raw/normalized score |
| `embedding_models` | provider-neutral model key, dimensions, distance metric, state, index metadata |

#### `ops`

| Таблица | Основные поля/ограничения |
|---|---|
| `outbox_messages` | type, payload reference, deduplication key, available/locked/processed timestamps, attempts, error |
| `indexing_attempts` | document index, attempt, worker, stage timings, outcome, trace ID |
| `idempotency_records` | scope, key, request hash, resource/response reference, expiry; unique `(scope, key)` |
| `audit_events` | tenant/workspace, actor, operation, resource, result, timestamp; без document content |
| `provider_usage` | provider/model, tokens/units, latency, estimated cost, operation reference |

High-volume operational rows (`retrieval_candidates`, processed outbox, provider usage) не хранят document text и имеют отдельный retention, по умолчанию 30 дней. `retrieval_runs` сохраняет компактный configuration/outcome snapshot вместе с историей ответа. При приближении к расчётному объёму time-based partitioning вводится до появления больших монолитных таблиц.

### 8.3 Tenant isolation в БД

Application authorization является первым слоем. PostgreSQL Row-Level Security — вторым.

На каждой транзакции tenant-owned query приложение устанавливает локальные параметры:

```sql
SELECT set_config('app.organization_id', @organization_id, true);
SELECT set_config('app.workspace_id', @workspace_id, true);
SELECT set_config('app.user_id', @user_id, true);
```

Значения всегда передаются параметрами; SQL-конкатенация запрещена.

RLS policies сравнивают строки с `current_setting(..., true)`. Роли API и worker не имеют `BYPASSRLS` и не владеют таблицами; migrations выполняются отдельной ролью. Background job получает tenant scope из сохранённого `DocumentIndex`, повторно валидирует его и устанавливает параметры до чтения данных.

Composite foreign keys не позволяют связать workspace, document, chunk или citation разных организаций. Наличие RLS не отменяет predicates в SQL: явные filters нужны и для безопасности чтения кода, и для query planning.

### 8.4 Хранение vectors разных моделей

`chunk_embeddings.embedding` имеет тип `vector` без фиксированной размерности. `embedding_models` фиксирует dimensions и metric. Для каждой используемой model family создаётся expression partial HNSW index с явным cast:

```sql
CREATE INDEX CONCURRENTLY ix_embeddings_model_x_hnsw
ON knowledge.chunk_embeddings
USING hnsw ((embedding::vector(1536)) vector_cosine_ops)
WHERE embedding_model_key = 'model-x';
```

Это даёт один индекс на embedding model family, а не на workspace. Query обязательно содержит model key, tenant/workspace, active document version и captured profile. Создание индекса выполняется контролируемой operational migration; identifier никогда не строится из пользовательского ввода.

Если размер HNSW становится проблемой, сначала оцениваются `halfvec` или quantized expression indexes с reranking исходным vector. Это не включается без ablation по quality/latency.

### 8.5 Full-text search

Начальный профиль хранит:

- `fts_simple` для точных кодов ошибок, GUID, method/service names и смешанного текста;
- опциональные language-aware `fts_russian`/`fts_english`, если evaluation докажет улучшение;
- GIN indexes на используемые vectors.

Query строится через безопасный `websearch_to_tsquery`/`plainto_tsquery`, а не через конкатенацию `to_tsquery`. Title и heading получают больший вес, чем body. `ts_headline` не используется в hot path: citation excerpt строится из сохранённых source coordinates.

## 9. Индексация документов

### 9.1 Upload transaction

```text
Authorize Editor/Admin
  -> validate quota, extension, MIME, magic bytes, size
  -> stream to temporary object while computing SHA-256
  -> validate extracted file metadata
  -> promote to tenant-scoped immutable content-addressed key
  -> DB transaction:
       idempotency record
       document/version
       document index for active profile
       outbox command
       audit event
  -> 202 Accepted + status URL
```

Object key включает organization/workspace. Cross-tenant blob deduplication запрещена. Если DB transaction не состоялась, verified blob становится orphan и удаляется после safety window.

### 9.2 Durable background command

В MVP `ops.outbox_messages` одновременно является transactional outbox и PostgreSQL-backed durable queue:

1. API вставляет command в одной транзакции с business state.
2. Worker claims batch через `FOR UPDATE SKIP LOCKED`, устанавливает `locked_by/locked_until`, commit.
3. Handler работает вне claim transaction.
4. Lease периодически продлевается. После crash сообщение становится доступным снова.
5. Completion/failure фиксируется короткой транзакцией.
6. Handler проверяет состояние `DocumentIndex`; повтор завершённого stage является no-op.

Это устраняет dual write и отдельный broker. `IBackgroundCommandTransport` остаётся внутренней границей: при росте нагрузки outbox relay можно переключить на broker без изменения use cases.

### 9.3 Stages

```text
Queued
  -> Extracting
  -> Chunking
  -> Embedding
  -> Staged
  -> Ready
  -> atomic publication
```

Каждый stage:

- имеет отдельный timeout и attempt count;
- пишет `Activity` и metrics;
- сохраняет checkpoint только после полного успеха stage;
- допускает безопасный повтор;
- проверяет, что document/profile не `Cancelled`, `Superseded` или `Deleted`;
- не делает active частичный набор chunks.

Временные chunks/embeddings привязаны к неактивному `DocumentIndex`. Публикация меняет `documents.active_version_id` только после `Ready` в одной транзакции. Retrieval дополнительно требует active `IndexProfile`.

### 9.4 Parsing и canonical representation

Parser возвращает не строку, а структурированный `ParsedDocument`:

```text
ParsedDocument
  metadata
  blocks[]
    kind: heading | paragraph | code | list | log | page-break
    text
    headingPath
    source: page / line range / character range
```

- Markdown: сохраняются heading hierarchy и code blocks.
- TXT/log: сохраняются line ranges; длинные stack traces не разрываются без необходимости.
- PDF: обрабатывается только text layer; сохраняются page и coordinates, scan возвращает `ocr_required`.
- Нормализация меняет whitespace предсказуемо и сохраняет mapping к исходным координатам.
- Parser version входит в `IndexProfile`.

### 9.5 Chunking baseline

Hierarchical chunker сначала соблюдает semantic boundaries, затем token budget:

| Параметр | Начальное значение |
|---|---:|
| Target | 500 tokens |
| Minimum | 150 tokens, кроме законченной короткой секции |
| Maximum | 700 tokens |
| Overlap | до 75 tokens внутри одной секции |
| Context | heading path добавляется как metadata, а не дублируется бесконтрольно в body |

Code block, stack trace и таблица считаются атомарными до maximum; после превышения режутся специализированной стратегией. Chunk ID/ordinal детерминированы внутри `DocumentIndex`. Любое изменение алгоритма создаёт новый `IndexProfile`.

### 9.6 Embedding batching

- Batch ограничивается provider token/item limits и application budget.
- Request содержит model key из profile; dimensions ответа проверяются до записи.
- NaN/Infinity, пустые и zero vectors отклоняются.
- Embeddings пишутся bulk-операцией Npgsql Binary COPY в staging transaction.
- Кэш embeddings допускается только внутри organization и по `(modelKey, normalizedContentHash)`; в первом milestone кэш не нужен.
- Provider retry применяется только к временным ошибкам и rate limits с jitter/`Retry-After`.

### 9.7 Blue/green reindex

Новый `IndexProfile` проходит `Draft -> Building -> Ready -> Active`:

1. Фиксируется snapshot active document versions.
2. Для каждой версии создаётся отдельный `DocumentIndex` нового profile.
3. Новые document versions во время build индексируются для active и building profiles либо задерживают activation.
4. После полноты выполняется offline evaluation.
5. В одной транзакции новый profile становится active, прежний — retired/rollback candidate.
6. Каждый retrieval run захватывает profile ID в начале и завершает запрос на нём.
7. После rollback window старые indexes очищаются background job.

## 10. Question answering pipeline

### 10.1 Последовательность

```text
Authorize Reader
  -> resolve AccessScope and active IndexProfile
  -> validate question / filters / quotas / idempotency
  -> persist question + RetrievalRun
  -> embed normalized question
  -> vector top 20 --------+
  -> FTS top 20 -----------+-> RRF -> deduplicate/diversify top 15
  -> rerank top 15 -> top 6
  -> evidence gate
       insufficient -> persist normal outcome
       sufficient   -> GroundingContext -> LLM -> buffered SSE
                     -> citation validation -> confidence -> persist final answer
```

### 10.2 Query normalization

MVP retrieval является single-turn. Нормализация:

- trim/Unicode normalization;
- сохранение error codes, versions, identifiers и quoted fragments;
- применение только явных metadata filters;
- отсутствие LLM query rewriting.

История conversation не добавляется к embedding query или prompt. Это исключает скрытое расширение access/context и упрощает evaluation. Multi-turn rewriting потребует отдельного ADR и набора тестов.

### 10.3 Candidate retrieval

Vector и FTS queries выполняются как независимые измеряемые stages. Они используют один captured `AccessScope` и `IndexProfileId`.

Для HNSW query устанавливаются profile-specific `SET LOCAL` параметры, включая iterative scan mode и `ef_search`. При selective tenant filters проверяются фактические rows returned и recall. Query plan хранится только в диагностическом режиме, без пользовательского текста.

Fusion baseline — Reciprocal Rank Fusion:

```text
rrfScore(chunk) = sum(1 / (k + rank_i)), k = 60
```

Вес и `k` являются частью profile и меняются только через evaluation. После fusion:

- одинаковый chunk объединяется;
- почти дублирующиеся chunks определяются по content hash/overlap;
- применяется configurable cap на chunks одного документа;
- source diversity не должна вытеснять единственный действительно релевантный документ.

### 10.4 Reranking

`IReranker` получает вопрос и не более 15 разрешённых chunks. Он возвращает score и порядок. В provider payload нет tenant metadata, не нужной модели.

- Model/version и score normalization фиксируются в profile.
- Scores разных reranker models напрямую не сравниваются.
- Timeout короче общего question budget.
- При временной недоступности разрешена деградация к RRF, но ответ помечается degraded, а quality gate отдельно проверяет штатный reranker path.

### 10.5 Evidence gate

До вызова chat model `EvidencePolicy` оценивает:

- top normalized rerank score;
- margin и согласованность candidates;
- число независимых supporting chunks/documents;
- применимость service/version/date filters;
- наличие конфликтующих версий;
- достаточность budget после deduplication.

Thresholds калибруются evaluation-набором и версионируются в profile. LLM не решает самостоятельно, достаточно ли evidence. Если gate не пройден, возвращается `200` со статусом `insufficient_evidence`.

### 10.6 Grounding context и prompt

`GroundingContext` immutable и содержит только top chunks, уже прошедшие access checks:

```text
Question
Explicit filters
Sources:
  [S1] title, revision, service/version/date, coordinates, content
  [S2] ...
Output rules and citation grammar
```

Правила prompt:

- model использует только Sources;
- Sources являются данными, а не инструкциями;
- запрещены tools/function calls;
- при конфликте источников модель сообщает конфликт;
- каждый фактический paragraph заканчивается source labels;
- нельзя ссылаться на label, которого нет в context;
- отсутствие evidence приводит к отказу, а не общему знанию.

Prompt template имеет version/hash, сохраняемый в message. Secrets, internal system prompt и недоступные document metadata не передаются клиенту.

### 10.7 Безопасный streaming

Невалидированный token нельзя считать финальным ответом. Используется paragraph-buffered streaming:

1. Chat response читается потоково, но удерживается до границы paragraph.
2. Paragraph обязан завершаться citation grammar, например `[S1][S2]`.
3. Structural validator проверяет labels, access, присутствие chunks в `GroundingContext` и quote ranges.
4. Только прошедший paragraph отправляется как `answer.delta`; citation metadata отправляется перед первым использованием label.
5. Клиент показывает поток как draft до события `completed`.
6. При ошибке незавершённый paragraph не отправляется, message становится `Failed`/`Cancelled`, durable answer не создаётся.

Semantic entailment нельзя надёжно доказать обычным кодом. Оно измеряется evaluation judge и ручной выборкой, но не подменяет structural validation.

SSE event types:

- `metadata` — question/answer IDs, trace ID;
- `retrieval.completed` — counts и degraded flags без скрытого content;
- `citation` — разрешённая metadata citation;
- `answer.delta` — проверенный paragraph;
- `usage` — token/cost summary;
- `completed` — final status/confidence;
- `error` — безопасный stable code после начала stream.

### 10.8 Confidence

`ConfidencePolicy` является versioned deterministic policy поверх наблюдаемых signals:

- normalized reranker scores;
- evidence gate outcome;
- citation coverage;
- число supporting sources;
- conflicts/degraded stages.

`high/medium/low` не является вероятностью истинности и не берётся из самооценки LLM. API возвращает label и безопасные signals. Пороги утверждаются только после calibration.

## 11. API и transport

Canonical contracts перечислены в [system requirements](system-requirements.md#7-api-контракты-mvp). Дополнительные правила реализации:

- Prefix `/api/v1`; breaking changes создают новый major route/contract namespace.
- Mutating requests используют `Idempotency-Key` и canonical request hash.
- List endpoints используют opaque cursor, стабильный sort `(created_at, id)` и server-side limit.
- Errors до начала stream — RFC Problem Details; после начала SSE — event `error`.
- `insufficient_evidence` — успешный domain result, не Problem Details.
- IDOR policy возвращает единообразный `404` для недоступных resource IDs.
- OpenAPI генерируется на build; breaking contract diff проверяется CI.
- SSE не требует sticky sessions после открытия connection; ingress должен отключать buffering и иметь согласованный timeout.

Повтор вопроса с тем же idempotency key возвращает тот же run и не вызывает модель второй раз. Для `Failed/Cancelled` используется explicit retry с новым key и ссылкой на исходный question.

## 12. Identity, authorization и безопасность

### 12.1 Authentication

- ASP.NET Core JWT Bearer/OIDC.
- OpsMind не хранит пароли и не выпускает production access tokens.
- Локальная разработка использует development issuer/tooling, но тот же claims mapping.
- Identity определяется парой `(issuer, subject)`; email не является ключом.

### 12.2 Authorization

Endpoint policy проверяет membership и role. Затем `AccessScopeResolver` создаёт immutable scope:

```text
UserId
OrganizationId
WorkspaceId
Role
MembershipVersion
```

Scope передаётся до repository/retrieval. Organization ID не принимается как доверенный request field. Изменение membership действует со следующего запроса; role claims в долгоживущем token не являются источником истины.

### 12.3 Threat controls

| Угроза | Контроль |
|---|---|
| IDOR / tenant leak | AccessScope, explicit SQL predicates, composite FKs, RLS, negative tests |
| Indirect prompt injection | Нет tools, sources как data, allow-listed context, paragraph validation |
| Malicious file | Extension + MIME + magic bytes + size/page/character limits; parser sandbox boundary; allow-list |
| Zip/parser bomb | Streaming limits, timeouts, memory budgets, process isolation path для parser при необходимости |
| Secret leakage | Secret store/env, redaction, raw prompts off by default, provider policy |
| Cache leak | Нет answer cache в MVP; любой будущий key включает tenant/workspace/profile/access version |
| Cross-tenant dedup side channel | Blob/embedding dedup не выполняется между organizations |
| SQL injection | Parameters; generated identifiers только из trusted model registry |
| Excessive provider cost | Per-workspace quotas, concurrency limiters, token budgets, circuit breakers |
| Stale/deleted knowledge | Active version/profile predicates, tombstone, retention cleanup |

Перед production-пилотом parser workload рекомендуется вынести в ограниченный process/container без network access и с read-only filesystem, если выбранная PDF-библиотека не имеет достаточной security history.

## 13. Concurrency, idempotency и consistency

- Upload idempotency scope: `(workspace, endpoint, key)`; request hash mismatch → `idempotency_conflict`.
- Document publication uses optimistic concurrency on `documents.row_version`.
- Один `DocumentIndex` на `(version, profile)` обеспечивается unique constraint.
- Один active `IndexProfile` на workspace обеспечивается partial unique index.
- Outbox deduplication key соответствует semantic command, например `index:{documentIndexId}`.
- Lease claim не держит DB transaction во время network/AI calls.
- Worker completion проверяет lease owner/version, чтобы старый worker не перезаписал новый attempt.
- Query captures profile/version snapshot; переключение profile не меняет уже начатый run.
- Citation хранит snapshot, поэтому reindex не ломает историю. Privacy deletion имеет приоритет и redacts snapshot.

Consistency model:

- membership/document state — strong consistency в primary PostgreSQL;
- новый документ становится searchable только после atomic publication;
- history и answer — записываются до финального `completed` event;
- telemetry и audit delivery могут быть eventual, но audit business row пишется в основной транзакции;
- read replicas не используются для authorization или только что опубликованных знаний.

## 14. Resilience и деградация

| Зависимость/сбой | Поведение |
|---|---|
| Object storage upload | Request не создаёт version до verified object; orphan cleanup после DB failure |
| Embedding provider | Bounded retry; index остаётся inactive; остальные jobs продолжаются |
| Reranker | Degraded RRF path с flag и metric; configurable отказ для strict evaluation |
| Chat provider | `503` до stream или `error` event; retrieval run сохраняется |
| PostgreSQL | Request fail-fast после короткого retry только для safe transient cases |
| Worker crash | Lease истекает; idempotent stage повторяется |
| SSE disconnect | Cancellation отмечает run; durable partial answer не публикуется |
| Citation validation | Один bounded repair attempt либо удаление paragraph/безопасный отказ |
| Profile build failure | Старый profile остаётся active |
| Cleanup failure | Tombstone сохраняет exclusion; cleanup retry + alert |

Timeout budget для question распределяется между stages и не превышает общий deadline. Retry не должен переживать client deadline для синхронного ответа.

## 15. Quotas и resource governance

Первоначальные quotas конфигурируются, но имеют безопасные defaults:

- размер файла, pages и extracted characters;
- документы/chunks/storage на workspace;
- uploads/minute и concurrent indexing jobs;
- questions/minute и concurrent generations;
- input/output tokens на вопрос;
- provider daily cost budget.

ASP.NET Core rate limiting защищает ingress. Provider bulkheads находятся в application layer и разделяют question traffic и background indexing, чтобы массовая индексация не блокировала ответы.

## 16. Observability

### 16.1 Trace model

Корневые activities:

- `opsmind.document.upload`;
- `opsmind.document.index` для background command с link к upload trace;
- `opsmind.question.answer`.

Дочерние spans:

```text
blob.put
document.extract
document.chunk
embedding.generate
chunks.persist
index.publish
retrieval.vector
retrieval.fts
retrieval.fusion
rerank
evidence.evaluate
chat.generate
citation.validate
answer.persist
```

### 16.2 Metrics

- latency/error/rate по stage и provider;
- outbox available count, oldest age, leases, retries, dead jobs;
- extracted chars, chunks, tokens и indexing duration;
- candidate counts, score distributions, degraded retrieval;
- TTFT, paragraph release latency и total answer latency;
- input/output tokens, embedding units и estimated cost;
- insufficient-evidence, citation rejection и prompt-injection test results;
- DB pool saturation, slow queries, HNSW/GIN size, autovacuum and bloat indicators.

High-cardinality IDs не являются metric labels. Они допустимы в защищённых traces/logs. Raw document text, question, prompt и answer не экспортируются по умолчанию. Для content diagnostics нужен отдельный локальный режим с явным флагом и redaction warning.

### 16.3 Health checks

- Liveness: process event loop работает; без внешних dependency calls.
- Readiness API: PostgreSQL и обязательная configuration доступны.
- Readiness Worker: PostgreSQL, object storage и active model configuration доступны.
- Provider outage не делает API process unhealthy: отражается dependency metric и domain error.

## 17. Deployment

### 17.1 Local Docker Compose

```text
api
worker
migrator (one-shot)
postgres 18 + pinned pgvector 0.8.x image/digest
minio
otel-collector
jaeger or tempo
prometheus
grafana
```

LLM providers по умолчанию внешние. Для integration tests используется deterministic fake provider; реальные provider tests запускаются отдельной категорией и требуют secrets извне репозитория.

### 17.2 Production shape

- API и Worker имеют отдельные deployments и scaling policies.
- Migrator запускается один раз перед rollout.
- API stateless; SSE требует корректных proxy buffering/timeouts, но не sticky sessions.
- Worker replicas координируются leases в PostgreSQL.
- Object storage versioning/encryption и PostgreSQL backups включаются в один recovery plan.
- `CREATE INDEX CONCURRENTLY` выполняется operational migration вне обычной transactional EF migration.
- Secrets монтируются из platform secret store.

### 17.3 Release compatibility

Для rolling deployment:

1. Expand schema migration.
2. Deploy code, совместимый со старой и новой schema.
3. Background backfill/reindex при необходимости.
4. Switch profile/feature flag.
5. Contract schema только после rollback window.

Destructive migrations и удаление старого vector profile не входят в тот же release, что и переключение.

## 18. Масштабирование

### Этап A — MVP

- Один PostgreSQL primary.
- Общие HNSW indexes по embedding model family.
- Explicit tenant filters + iterative scan.
- Несколько API/Worker replicas при необходимости.
- Вертикальное масштабирование DB и provider batching.

### Этап B — оптимизация без смены архитектуры

- Настройка `ef_search`, iterative scan, `work_mem`, autovacuum и connection pools.
- `halfvec`/quantization только после ablation.
- Партиционирование больших history/telemetry tables по времени.
- Read replica для безопасных stale history reads, не authorization.
- Архивация retired document indexes.

### Этап C — tenant-aware scale-out

Триггер: SLO/recall не достигаются после тюнинга либо один tenant нарушает fair use.

- tenant placement table;
- группы tenant'ов по PostgreSQL clusters/partitions;
- dedicated database для крупных tenant'ов;
- routing внутри Infrastructure;
- API/Application contracts не меняются.

Отдельный vector service рассматривается только после профилирования. Это не исходная цель проекта.

## 19. Тестовая архитектура

### Unit

- state transitions;
- chunk boundaries/coordinates;
- RRF/dedup/diversity;
- evidence/confidence policies;
- prompt source-label grammar;
- citation structural validation;
- idempotency and lease decisions.

### Integration с реальным PostgreSQL/pgvector

- migrations/extensions;
- composite tenant constraints и RLS;
- vector exact/HNSW и FTS queries;
- filtered ANN recall;
- outbox claim/lease/retry;
- atomic document/profile publication;
- concurrent upload/delete/reindex;
- Binary COPY rollback.

### API

- OpenAPI/Problem Details/SSE event contracts;
- upload/status/question/source/history/membership;
- idempotency and cursor pagination;
- disconnect/retry semantics.

### Security

- каждый repository/query с двумя tenants;
- IDOR всех resource IDs;
- cache/provider/telemetry isolation;
- direct/indirect prompt injection;
- malicious/oversized file cases.

### Evaluation и load

- Dataset и gates из [evaluation-dataset.md](evaluation-dataset.md);
- ablation vector/FTS/hybrid/reranker;
- load profile из product requirements;
- provider throttling, DB saturation и queue backlog;
- backup/restore и physical deletion verification.

Architecture tests должны запрещать ссылки Domain/Application на ASP.NET, EF, Npgsql и provider SDK, а также прямое использование AI clients вне Infrastructure/Answering adapters.

## 20. Последовательность разработки

### Slice 0. Foundation

- solution structure, central package management, analyzers;
- Docker Compose: PostgreSQL/pgvector, MinIO, OTel;
- Migrator, health checks, CI build/test;
- base Activity/Meter conventions.

**Готово, когда:** чистый checkout запускается одной документированной командой, migrations и smoke tests проходят.

### Slice 1. Tenant и access

- identity claims mapping;
- organization/workspace/membership schema;
- `AccessScope`, policies, composite FKs и первые RLS tests.

**Готово, когда:** cross-workspace API/DB negative tests проходят до появления RAG-кода.

### Slice 2. Upload -> status

- blob storage;
- documents/versions/index profiles;
- idempotency + outbox lease worker;
- upload/status/retry endpoints;
- fake parser job и audit.

**Готово, когда:** crash/retry не создаёт дубликаты, один poison file не блокирует очередь.

### Slice 3. Parse -> chunks

- Markdown/TXT/PDF parsers;
- canonical representation and hierarchical chunker;
- chunk inspection/source endpoint;
- indexing fixtures I01–I08.

### Slice 4. Embeddings -> vector search

- MEAI embedding adapter and deterministic fake;
- chunk embeddings, HNSW operational migration;
- vector endpoint/query service;
- filtered recall and tenant security tests.

### Slice 5. Grounded answer

- retrieval run;
- evidence gate;
- prompt builder, chat adapter;
- citations, `insufficient_evidence`, history;
- сначала complete JSON без streaming.

### Slice 6. Hybrid + reranking

- FTS vectors/GIN;
- RRF/dedup/diversity;
- reranker adapter and degraded path;
- ablation evaluation.

### Slice 7. SSE и operational hardening

- paragraph-buffered SSE;
- quotas/bulkheads/circuit breakers;
- dashboards, cost metrics;
- deletion/retention/reconciliation;
- backup/restore and load tests.

### Slice 8. Blue/green reindex

- building profile catch-up;
- evaluation gate and atomic activation;
- rollback/cleanup.

Полная реализация каждого slice включает migrations, telemetry, negative access tests и documentation; эти задачи не откладываются в отдельную «hardening phase».

## 21. Architecture fitness functions

Следующие условия должны проверяться автоматически:

- ни один retrieval method не вызывается без `AccessScope`;
- provider SDK packages отсутствуют в Domain/Application;
- tenant-owned tables имеют tenant columns и approved RLS policy;
- все citations принадлежат chunks конкретного `RetrievalRun` context;
- active document/profile uniqueness обеспечена DB constraints;
- migration не создаёт blocking large index без явного operational step;
- OpenAPI breaking changes обнаруживаются CI;
- security evaluation имеет нулевой leakage;
- каждый AI call создаёт usage/trace record без raw sensitive content;
- все background handlers проходят idempotency/concurrent lease tests.

## 22. ADR backlog

Решения этого документа следует вынести в короткие ADR по мере реализации:

1. Modular monolith и три executable hosts.
2. PostgreSQL + pgvector вместо отдельной vector DB.
3. `DocumentVersion` отдельно от `DocumentIndex`.
4. PostgreSQL outbox-as-queue вместо Hangfire/Quartz/message broker в MVP.
5. Variable-dimension vector storage и partial expression indexes по model family.
6. Hybrid retrieval через RRF и provider-neutral reranker.
7. Single-turn retrieval MVP.
8. Paragraph-buffered SSE и citation grammar.
9. Application authorization + PostgreSQL RLS defense in depth.
10. Blue/green `IndexProfile` activation.

ADR фиксирует контекст, решение, альтернативы, последствия и условие пересмотра. Он не копирует весь этот документ.

## 23. Открытые решения перед соответствующим slice

Они не блокируют создание solution и первых вертикальных slices:

| Решение | Когда принять | Критерий |
|---|---|---|
| Конкретная PDF parser library | До Slice 3 | License, coordinates, malformed PDF behavior, memory/security tests |
| Конкретные embedding/chat/reranker models | До Slice 4/5/6 | Quality, language support, dimensions, latency, data policy, cost |
| Exact chunk parameters | В Slice 3–6 | Evaluation, а не предпочтение разработчика |
| HNSW parameters/halfvec | После representative data | Filtered recall, latency, memory |
| Production OIDC provider | До пилота | Организационный IdP и claims contract |
| Production object storage | До deployment | S3 compatibility, encryption, lifecycle, restore |

## 24. Официальные технические основания

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) — .NET 10 является активным LTS.
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/core/extensions/artificial-intelligence) — provider-neutral chat и embedding abstractions.
- [ASP.NET Core SSE](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0#support-for-server-sent-events-sse) — встроенный `ServerSentEvents` result.
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/) — major/minor support и upgrade model.
- [PostgreSQL Row Security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html) — DB-level tenant defense in depth.
- [pgvector](https://github.com/pgvector/pgvector) — HNSW, iterative scans, variable dimensions и partial expression indexes.
- [pgvector-dotnet](https://github.com/pgvector/pgvector-dotnet) — Npgsql/EF Core/Dapper integration.
- [Npgsql Binary COPY](https://www.npgsql.org/doc/copy.html) — bulk persistence с rollback при незавершённом import.
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/) — stable traces, metrics и logs.

