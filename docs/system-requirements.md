# OpsMind — системные требования и архитектура MVP

**Статус:** утверждённый baseline MVP  
**Версия:** 0.2

## 1. Текущее состояние проекта

На дату документа репозиторий не содержит кода, схемы данных, инфраструктурной конфигурации или истории коммитов. Требования ниже задают целевую архитектуру MVP, а не фиксируют уже реализованное поведение.

## 2. Контекст и компоненты

```text
Web client / API client
          |
          v
ASP.NET Core API ---------------------> PostgreSQL + pgvector
  |  auth, documents, questions,        metadata, chunks, FTS,
  |  history, sources, SSE               vectors, conversations,
  |                                      outbox and jobs
  |
  +----> Object storage
  |       original files
  |
  +----> Outbox dispatcher / job queue ----> Background indexing worker
  |                                         parse -> normalize -> chunk
  |                                         -> embed -> stage -> publish
  |
  +----> Embedding provider
  |
  +----> Chat completion provider
  |
  +----> Optional reranker provider
  |
  +----> OpenTelemetry Collector -> tracing/metrics/log backend
```

Для MVP API и worker могут быть двумя host-процессами из одной solution и общей application/domain-библиотеки. Индексация должна оставаться асинхронной, даже если локально оба процесса запускаются вместе. Hangfire, Quartz или собственный dispatcher являются инфраструктурной деталью при условии соблюдения outbox, lease, retry и idempotency semantics.

## 3. Архитектурные правила

1. `TenantId` и `WorkspaceId` являются частью авторизационного контекста и запросов к данным, а не фильтром после retrieval.
2. Active index публикуется атомарно: пользователь не должен видеть половину chunks новой версии.
3. Retrieval, reranking и generation — отдельные измеряемые этапы с заменяемыми интерфейсами.
4. Провайдеры LLM и embeddings доступны через адаптеры; доменная логика не зависит от SDK конкретного провайдера.
5. Citation может ссылаться только на chunk из контекста конкретного ответа.
6. Текст документа и вопрос пользователя считаются недоверенным вводом.
7. Confidence не берётся из свободного текста модели; он вычисляется приложением из retrieval/citation сигналов.
8. Запись бизнес-состояния и публикация фоновой команды согласуются через transactional outbox.
9. Любая повторяемая команда имеет idempotency key; at-least-once доставка не должна создавать дубликаты.
10. Смена parser/chunker/embedding profile выполняется через параллельное построение и атомарное переключение, а не изменением активных chunks на месте.
11. `insufficient_evidence` является штатным результатом вопроса, а не технической ошибкой.

## 4. Основные сущности

| Сущность | Ключевые поля и назначение |
|---|---|
| `Organization` | `Id`, `Name`, `CreatedAt`; верхняя граница tenant isolation |
| `User` | Глобальная identity: `Id`, issuer, внешний subject, display name, status; unique `(issuer, subject)` |
| `OrganizationMember` | `OrganizationId`, `UserId`, organization-level status; разрешает членство пользователя в tenant |
| `Workspace` | `Id`, `OrganizationId`, `Name`, `Slug` |
| `WorkspaceMember` | `OrganizationId`, `WorkspaceId`, `UserId`, `Role` (`Admin`, `Editor`, `Reader`); членство допустимо только через `OrganizationMember` той же организации |
| `Document` | Стабильная логическая сущность: `Id`, tenant/workspace, title, source type, state, `ActiveVersionId` |
| `DocumentVersion` | Неизменяемый источник: `Id`, `DocumentId`, internal revision, applicable service/version/date, object key, checksum, MIME, timestamps; не принадлежит конкретной embedding model |
| `DocumentIndex` | Материализация одной `DocumentVersion` для одного `IndexProfile`: `Id`, version/profile, processing status, parser/chunker metadata, timestamps, failure code |
| `Chunk` | `Id`, `DocumentIndexId`, tenant/workspace, ordinal, section path, text, token count, page/line coordinates, metadata JSON, FTS vector, embedding |
| `IndexProfile` | `Id`, workspace, status, provider/model, embedding dimensions, distance metric, chunker/parser versions, FTS/fusion/reranker configuration |
| `Conversation` | `Id`, tenant/workspace, owner, title, created/updated timestamps |
| `Message` | `Id`, conversation, role, content, status, model usage, trace ID, timestamp |
| `AnswerCitation` | `Id`, answer message, chunk/version reference, immutable quote snapshot, coordinates, rank, retrieval/rerank score |
| `RetrievalRun` | Question, filters, profile, candidates, timings, decision/confidence signals |
| `IndexingJob` | `DocumentIndexId`, attempt, status, lease/retry data, trace ID |
| `OutboxMessage` | Тип фоновой команды, payload/reference, occurred/published timestamps, attempt/error; создаётся в транзакции с бизнес-состоянием |
| `AuditEvent` | Tenant/workspace, actor, operation, resource reference, result, timestamp; без содержимого документа |

Все таблицы, содержащие пользовательские знания, должны иметь прямой либо гарантированно проверяемый путь к `OrganizationId` и `WorkspaceId`. Для горячего retrieval-пути эти поля хранятся непосредственно в `Chunk`. Composite foreign keys должны доказывать, что workspace, membership, document, version и chunk принадлежат одной организации. Проверка application layer обязательна; PostgreSQL RLS может использоваться как второй рубеж.

`DocumentVersion.revision` — внутренняя последовательная ревизия документа. Метаданные `applicableServiceVersion` описывают версию сервиса и не должны называться просто `version`, чтобы не смешиваться с ревизией документа.

## 5. Состояния и жизненный цикл

### Документ и версия

```text
DocumentIndex: Queued -> Extracting -> Chunking -> Embedding -> Staged -> Ready
            |             |             |            |         |
            +-------------+-------------+------------+---------+-> Failed
            +---------------------------------------------------> Cancelled
```

- После upload создаются неизменяемая `DocumentVersion` и `DocumentIndex` для активного `IndexProfile`.
- `Ready` присваивается `DocumentIndex` только после сохранения всех chunks и успешного создания индексов.
- Новая версия становится active атомарно только после готовности соответствующего `DocumentIndex` активного profile.
- Старая версия может оставаться доступной для аудита, но не участвует в обычном retrieval.
- Повтор файла с тем же checksum в том же документе не должен повторно индексироваться без явного запроса.
- `DocumentIndex`, ставший ненужным до публикации версии/profile, получает `Cancelled`/`Superseded`; worker обязан прекратить его обработку в безопасной точке. Сама immutable `DocumentVersion` остаётся источником аудита до удаления по retention policy.
- Деактивированный документ немедленно исключается из retrieval. Физическое удаление blob/chunks выполняется отдельной идемпотентной job после retention window.

Для каждого документа `ActiveVersionId` указывает не более чем на одну версию того же документа/workspace. Публикация выполняется одной транзакцией: проверить готовность `DocumentIndex` для активного profile → заменить `ActiveVersionId` → записать audit event. Старые версии и их indexes не участвуют в обычном retrieval, но доступны в пределах retention/rollback policy.

### Надёжная постановка indexing job

1. API принимает upload с `Idempotency-Key`, проверяет квоты и потоково записывает blob во временный object key, одновременно считая checksum и лимиты.
2. После валидации blob копируется/продвигается в immutable content-addressed key; существование и checksum окончательного объекта проверяются до транзакции БД.
3. API одной транзакцией создаёт `DocumentVersion` со ссылкой на окончательный key, `DocumentIndex(Queued)` для active profile и `OutboxMessage(IndexDocumentIndex)`.
4. Outbox dispatcher публикует команду at-least-once и отмечает публикацию. Reconciliation повторяет зависшие сообщения.
5. Worker получает команду, захватывает lease и проверяет текущее состояние `DocumentIndex`. Каждый stage имеет детерминированный ключ и допускает безопасный повтор.
6. Временный объект удаляется после успеха; если транзакция БД не состоялась, окончательный object становится orphan и удаляется cleanup job после safety window. Orphaned blobs и зависшие `Queued` indexes регулярно обнаруживаются метриками и reconciliation.

### Вопрос и ответ

```text
Question accepted -> Retrieving -> Reranking -> Generating -> Completed
                            |             |             |
                            +-------------+-------------+-> Failed/InsufficientEvidence
```

Сначала создаётся сообщение вопроса и `RetrievalRun`. При streaming частичный текст не считается завершённым ответом до сохранения финального сообщения, usage и citations.

## 6. Функциональные требования

### 6.1 Документы и индексирование

| ID | Требование |
|---|---|
| FR-DOC-01 | Принимать `.md`, `.txt`, `.pdf` с проверкой расширения, MIME, magic bytes и лимита размера |
| FR-DOC-02 | Считать SHA-256 исходного файла и хранить оригинал вне БД через `IBlobStorage` |
| FR-DOC-03 | Извлекать текст и структуру: заголовки Markdown, номера страниц PDF, строки TXT |
| FR-DOC-04 | Нормализовать переносы/пробелы, сохраняя координаты для citation |
| FR-DOC-05 | Создавать chunks с overlap и метаданными `document`, `section`, `documentDate`, `service`, `applicableVersion`, `page/lines` |
| FR-DOC-06 | Строить embedding пакетами, сохранять модель и размерность |
| FR-DOC-07 | Создавать PostgreSQL `tsvector` для полнотекстового поиска |
| FR-DOC-08 | Поддерживать идемпотентный retry и изолировать ошибку одного файла |
| FR-DOC-09 | Возвращать статус, прогресс, число chunks и безопасное описание ошибки |
| FR-DOC-10 | Создавать business state и indexing command через transactional outbox |
| FR-DOC-11 | Поддерживать деактивацию, retention-aware deletion и очистку orphaned blobs/chunks |
| FR-DOC-12 | Ограничивать upload/indexing квотами организации и workspace |

Начальная гипотеза chunking: 400–600 токенов, overlap 10–15%, предпочтительное разбиение по заголовкам и абзацам. Это конфигурация `IndexProfile`, которую следует подобрать по evaluation-набору.

Для смешанных русско-английских технических данных FTS-конфигурация также входит в `IndexProfile`. Начальный baseline — нормализованный `simple` vector для точных идентификаторов плюс экспериментально проверяемые языковые vectors/поля. Коды ошибок, GUID, имена методов и составные идентификаторы должны сохраняться без потери и отдельно проверяться evaluation-набором.

### 6.2 Retrieval и reranking

| ID | Требование |
|---|---|
| FR-RET-01 | Векторный поиск использует cosine distance и только active versions доступного workspace |
| FR-RET-02 | Полнотекстовый поиск учитывает точные идентификаторы, коды ошибок и названия сервисов |
| FR-RET-03 | Hybrid retrieval объединяет списки устойчивым методом, например Reciprocal Rank Fusion |
| FR-RET-04 | Поддерживать фильтры `service`, `applicableVersion`, `documentDate`, `documentId` |
| FR-RET-05 | Убирать дубликаты и ограничивать доминирование одного документа |
| FR-RET-06 | Reranker переставляет ограниченный набор кандидатов и возвращает score |
| FR-RET-07 | Порог достаточности определяется конфигурацией и калибруется на evaluation-наборе |
| FR-RET-08 | В трассе сохраняются идентификаторы candidates, ranks, scores и применённые фильтры; полный чувствительный текст не экспортируется по умолчанию |
| FR-RET-09 | Все retrieval-запросы используют активный `IndexProfile` workspace и не смешивают embeddings разных моделей/размерностей |
| FR-RET-10 | ANN recall и latency измеряются с реальными tenant/workspace filters, а не только на нефильтрованном индексе |

Базовый pipeline для эксперимента: vector top 20 + FTS top 20 → RRF → deduplication → top 15 → reranker → top 5–8 в контекст LLM.

#### 6.2.1 Lifecycle `IndexProfile`

```text
Draft -> Building -> Ready -> Active -> Retired
                    |          |
                    +----------+-> Failed
```

- В workspace существует ровно один active profile для новых retrieval.
- Профиль immutable после начала `Building`.
- Новая модель/размерность строит отдельный набор chunks/embeddings рядом с активным (blue/green reindex).
- Для каждой пары `(DocumentVersionId, IndexProfileId)` существует не более одного `DocumentIndex`.
- Building profile создаёт indexes для snapshot всех active document versions. Версии, опубликованные во время build, индексируются и для active, и для building profile либо блокируют activation до catch-up.
- После evaluation и проверки, что каждый активный документ имеет `Ready DocumentIndex`, новый профиль переключается атомарно. Старый сохраняется на rollback window, затем очищается.
- Физическая схема embedding должна обеспечивать фиксированную размерность на индекс: отдельная таблица/partition/index collection для profile family либо другой явно протестированный вариант. Полиморфный `vector` без подходящего dimension-specific index не допускается как скрытое решение.
- При росте данных сначала используются настройка HNSW/iterative scan и измерение filtered recall; затем — partitioning по tenant-группам или выделенная БД для крупных tenant'ов. Порог перехода определяется load test и SLO, а не только числом строк.

### 6.3 Generation, citations и confidence

| ID | Требование |
|---|---|
| FR-GEN-01 | System prompt запрещает использовать знания вне переданного контекста и исполнять инструкции из документов |
| FR-GEN-02 | Контекст содержит стабильные source labels, например `[S1]`, но не системные секреты |
| FR-GEN-03 | LLM возвращает структурированный draft: текст и ссылки на source labels |
| FR-GEN-04 | Приложение структурно валидирует source label, доступ, присутствие chunk в generation context и точное вхождение quote в source snapshot |
| FR-GEN-05 | Невалидная citation удаляется/исправляется безопасным повтором; неподтверждённый тезис не выдаётся как факт |
| FR-GEN-06 | При недостатке evidence возвращается специальный результат, а не общий ответ модели |
| FR-GEN-07 | Ответ можно получить обычным JSON или SSE-событиями |

Начальная модель confidence:

- `high`: достаточный rerank score, минимум один сильный источник, все существенные тезисы процитированы;
- `medium`: evidence релевантен, но неполон, конфликтует или требует сопоставления нескольких фрагментов;
- `low`: используется только для ответа «недостаточно информации» или показа кандидатов, но не для категоричного совета.

Числовые пороги не фиксируются до калибровки. В API вместе с label возвращаются сигналы, например `evidenceCoverage`, `topRerankScore`, `sourceCount`.

Структурная citation validation детерминирована. Проверка того, что источник семантически подтверждает утверждение, относится к entailment/evaluation: она выполняется отдельным зафиксированным judge-процессом и выборочной ручной проверкой. Reranker scores нормализуются и калибруются отдельно для каждого `IndexProfile`; абсолютные значения разных моделей не сравниваются.

### 6.4 История и источники

- Сохранять вопрос, завершённый ответ, citations, usage, модель, параметры retrieval и trace ID.
- Показывать только историю текущего пользователя в разрешённом workspace; Admin не получает скрытого доступа к личной истории без отдельного продуктового решения.
- Для воспроизводимости citation хранит процитированный текст-снимок и ссылку на версию/chunk.
- Просмотр источника возвращает chunk и ограниченный соседний контекст, но повторно проверяет доступ.
- Историческая citation открывается по собственному immutable ID и остаётся проверяемой после переиндексации. Если исходный документ удалён по privacy/retention policy, API возвращает разрешённый snapshot либо факт удаления в соответствии с политикой, но не восстанавливает запрещённое содержимое.
- Conversation в MVP группирует сообщения для истории. Retrieval каждого вопроса single-turn: предыдущие сообщения не участвуют в query rewriting или generation context. Multi-turn rewriting потребует отдельного требования, evaluation и защиты от накопленной prompt injection.

## 7. API-контракты MVP

Префикс: `/api/v1`. Все идентификаторы — UUID. Workspace задаётся в route, а его доступ проверяется по identity claims и данным членства. Mutating endpoints принимают `Idempotency-Key`; повтор с тем же ключом и другим payload возвращает conflict. List endpoints используют cursor pagination с серверным maximum page size.

### Документы

```http
POST /api/v1/workspaces/{workspaceId}/documents
Content-Type: multipart/form-data
Idempotency-Key: uuid

file=<binary>
title=Orders API runbook
service=orders-api
applicableVersion=2.4
documentDate=2026-10-15
```

```json
{
  "documentId": "uuid",
  "versionId": "uuid",
  "indexingStatus": "queued",
  "statusUrl": "/api/v1/workspaces/{workspaceId}/documents/{documentId}/versions/{versionId}"
}
```

```http
POST   /api/v1/workspaces/{workspaceId}/documents/{documentId}/versions
GET    /api/v1/workspaces/{workspaceId}/documents?cursor=...&limit=...
GET    /api/v1/workspaces/{workspaceId}/documents/{documentId}/versions?cursor=...
GET    /api/v1/workspaces/{workspaceId}/documents/{documentId}/versions/{versionId}
POST   /api/v1/workspaces/{workspaceId}/documents/{documentId}/versions/{versionId}:retry
POST   /api/v1/workspaces/{workspaceId}/documents/{documentId}:deactivate
DELETE /api/v1/workspaces/{workspaceId}/documents/{documentId}
GET    /api/v1/workspaces/{workspaceId}/sources/{chunkId}
GET    /api/v1/workspaces/{workspaceId}/answers/{answerId}/citations/{citationId}
```

Status response содержит `status`, `stage`, `progress`, `chunkCount`, timestamps и при ошибке стабильный `errorCode`.

`DELETE` сначала создаёт tombstone и немедленно исключает документ из retrieval, затем ставит retention-aware cleanup job через outbox. Retry создаёт новую попытку существующей version, но не новую логическую версию. Конкурирующие изменения защищаются optimistic concurrency token/ETag.

### Участники workspace

```http
GET    /api/v1/workspaces/{workspaceId}/members?cursor=...
PUT    /api/v1/workspaces/{workspaceId}/members/{userId}
DELETE /api/v1/workspaces/{workspaceId}/members/{userId}
```

Изменение membership доступно Admin, пишется в audit и применяется к следующему запросу. Нельзя удалить/понизить последнего Admin без атомарного назначения другого.

### Вопросы

```http
POST /api/v1/workspaces/{workspaceId}/questions
Content-Type: application/json
Accept: application/json
Idempotency-Key: uuid
```

```json
{
  "question": "Почему возникает InvalidOperationException при оформлении заказа?",
  "conversationId": null,
  "filters": {
    "service": ["orders-api"],
    "applicableVersion": ["2.4"]
  },
  "responseMode": "complete"
}
```

```json
{
  "questionId": "uuid",
  "answerId": "uuid",
  "answer": "Причина ... [S1]. Исправление ... [S2].",
  "status": "answered",
  "confidence": {
    "level": "high",
    "evidenceCoverage": 1.0,
    "sourceCount": 2
  },
  "citations": [
    {
      "citationId": "uuid",
      "label": "S1",
      "chunkId": "uuid",
      "documentId": "uuid",
      "documentVersionId": "uuid",
      "title": "Orders API runbook",
      "section": "Checkout failures",
      "page": 3,
      "quote": "...",
      "sourceUrl": "/api/v1/workspaces/{workspaceId}/answers/{answerId}/citations/{citationId}"
    }
  ],
  "traceId": "00-..."
}
```

Для streaming клиент передаёт `Accept: text/event-stream`. Минимальные SSE events: `metadata`, `answer.delta`, `citation`, `usage`, `completed`, `error`. Событие `completed` содержит итоговые ID и confidence. Ошибка после начала потока передаётся событием, а не сменой HTTP status.

Каждое SSE-событие имеет монотонный `id` внутри question run. Частичные deltas не являются durable history: после разрыва клиент получает состояние через `GET /api/v1/workspaces/{workspaceId}/questions/{questionId}`. Повтор исходного POST с тем же idempotency key никогда не запускает второй LLM-вызов: он возвращает тот же run или его status/`Location`. Для `Failed/Cancelled` используется явный retry endpoint с новым idempotency key; новая попытка связывается с исходным вопросом.

При отсутствии достаточных доказательств endpoint возвращает `200 OK` и штатный результат:

```json
{
  "questionId": "uuid",
  "answerId": "uuid",
  "status": "insufficient_evidence",
  "answer": null,
  "confidence": { "level": "low", "evidenceCoverage": 0.0, "sourceCount": 0 },
  "citations": [],
  "traceId": "00-..."
}
```

Вопрос имеет конфигурируемый предел символов/токенов; число значений каждого filter и размер generation context ограничены сервером. `conversationId` обязан принадлежать тому же пользователю и workspace, иначе возвращается `404` без подтверждения существования чужого ресурса.

```http
GET /api/v1/workspaces/{workspaceId}/conversations?cursor=...
GET /api/v1/workspaces/{workspaceId}/conversations/{conversationId}/messages?cursor=...
GET /api/v1/workspaces/{workspaceId}/questions/{questionId}
POST /api/v1/workspaces/{workspaceId}/questions/{questionId}:retry
```

### Формат ошибок

Использовать `application/problem+json` только для ошибок транспорта, валидации, доступа и инфраструктуры. Стабильные коды: `unsupported_file_type`, `file_too_large`, `empty_document`, `document_not_ready`, `workspace_access_denied`, `indexing_failed`, `provider_unavailable`, `rate_limit_exceeded`, `quota_exceeded`, `idempotency_conflict`, `concurrency_conflict`. `insufficient_evidence` — статус успешного доменного ответа, а не Problem Details.

## 8. Модель доступа

| Действие | Admin | Editor | Reader |
|---|:---:|:---:|:---:|
| Управлять участниками и ролями | Да | Нет | Нет |
| Загружать новую версию документа | Да | Да | Нет |
| Деактивировать документ / повторить индексацию | Да | Да | Нет |
| Запросить физическое удаление документа | Да | Нет | Нет |
| Просматривать документы и chunks | Да | Да | Да |
| Задавать вопросы | Да | Да | Да |
| Смотреть собственную историю | Да | Да | Да |
| Смотреть чужую историю | Нет | Нет | Нет |

Требования безопасности:

- не доверять `organizationId`, переданному клиентом;
- проверять membership на каждом endpoint и в retrieval repository;
- исключить IDOR для document, version, chunk, conversation и message;
- использовать параметризованный SQL;
- ограничивать тип, размер, число страниц и распакованный объём файла;
- не выполнять активное содержимое PDF и не передавать сырой документ в prompt;
- секреты поступают из environment/secret store и не журналируются;
- логи и traces не содержат файл, полный prompt или ответ по умолчанию;
- предусмотреть PostgreSQL Row-Level Security как дополнительный слой после стабилизации схемы, но не считать его заменой application authorization.
- применять composite foreign keys/unique constraints, не позволяющие связать ресурсы разных организаций;
- не различать `403/404` таким образом, который позволяет подтвердить существование чужого ресурса;
- проверять tenant isolation не только в final answer, но и в candidates, reranker input, LLM prompt, cache keys и telemetry;
- шифровать transport, database/object storage и резервные копии; ключи управляются вне репозитория;
- namespace object key и cache key включает organization/workspace; дедупликация по checksum между tenant'ами запрещена, чтобы не создавать side channel;
- антивирусная проверка может быть добавлена перед публикацией документа; до этого неизвестные/активные форматы запрещены allow-list политикой.

## 9. Нефункциональные требования

### Производительность и масштабирование

- Расчётный профиль: до 20 организаций, 200 workspace, 20 000 активных документов и 2 млн активных chunks.
- Нагрузка: до 25 одновременных вопросов на инсталляцию (не более 5 на workspace), 20 uploads/мин и 10 параллельных indexing jobs.
- P95 полного ответа ≤ 10 с при прогретом приложении и штатном провайдере.
- P95 первого SSE-события ≤ 3 с.
- Upload endpoint отвечает ≤ 2 с без учёта передачи файла.
- Начальный лимит файла: 25 MiB, не более 300 страниц PDF и 2 млн извлечённых символов; значения конфигурируются.
- API масштабируется горизонтально без локального session state.
- Worker использует lease/visibility timeout и ограниченную конкурентность.
- Vector index: HNSW как начальный выбор; параметры проверяются на реальном объёме.
- P95 возраста обычной indexing job в очереди ≤ 15 минут при расчётном профиле; крупные документы измеряются отдельно.
- Все SLO измеряются с зафиксированными provider/model, hardware, dataset, concurrency и длительностью теста.
- Rate limit, provider concurrency limit и cost budget применяются отдельно к организации/workspace и глобально. Очереди вопросов и индексации не должны взаимно исчерпывать общий bulkhead.

Путь масштабирования:

1. Один PostgreSQL cluster, HNSW и обязательные tenant/workspace predicates; измерять filtered ANN recall и query plans.
2. Настроить HNSW/iterative scan, connection pools, batching и autovacuum; добавить read replica только для допустимо stale read-нагрузки.
3. При нарушении SLO после тюнинга — partitioning по tenant-группам или изоляция крупных tenant'ов в отдельные database/shard. Маршрутизация сохраняет единый application contract.
4. Переход на отдельный vector service допускается только после измеренного ограничения PostgreSQL и оформляется отдельным ADR; domain/API contracts не должны зависеть от pgvector-specific типов.

### Надёжность

- At-least-once обработка job с идемпотентными стадиями.
- Exponential backoff с jitter только для временных ошибок провайдера.
- Максимум попыток и dead-letter/failed state для постоянных ошибок.
- Cancellation и timeout для каждого внешнего вызова.
- Транзакционная публикация active version.
- Целевая доступность MVP API: 99,5% в пределах оговорённого периода эксплуатации.
- Transactional outbox, reconciliation зависших jobs и cleanup orphaned blobs.
- Circuit breaker и bulkhead для каждого внешнего provider; retry не применяется к validation/auth/permanent errors.
- Graceful shutdown прекращает получение новых jobs, продлевает/освобождает lease и не публикует частичный индекс.

### Восстановление и хранение

- Целевые значения MVP: RPO ≤ 24 часа, RTO ≤ 4 часа. Для production-пилота значения должны быть пересмотрены владельцем продукта.
- PostgreSQL и object storage включаются в согласованный backup set; восстановление проверяется не реже одного раза перед release и далее по расписанию.
- Retention по умолчанию: активные исходники/chunks — срок жизни документа; audit metadata — 180 дней; технические traces — 30 дней. Tenant policy может только ужесточить сроки без отдельного согласования.
- Tombstoned документ немедленно исключается из поиска. Physical deletion удаляет blob, chunks, embeddings и provider-side artifacts; результат подтверждается audit event.
- Citation snapshot подчиняется той же privacy/deletion policy, что и источник, даже если это снижает воспроизводимость старого ответа.

### Совместимость и воспроизводимость

- Поддерживаемая БД должна иметь установленное расширение `vector`.
- Миграции схемы версионируются вместе с кодом.
- `IndexProfile` фиксирует parser/chunker/embedding model; смена размерности требует нового индекса или переиндексации.
- Активный profile переключается blue/green; rollback window и cleanup старого профиля конфигурируются.
- Все timestamps хранятся в UTC; UI отвечает за локализацию.

## 10. Наблюдаемость

Один trace связывает API-запрос и дочерние этапы. Для фоновой задачи trace context переносится через job metadata.

Обязательные spans:

- `document.upload`, `document.extract`, `document.chunk`, `embedding.batch`, `index.publish`;
- `question`, `retrieval.vector`, `retrieval.fts`, `retrieval.fusion`, `rerank`, `llm.generate`, `citation.validate`.

Обязательные метрики:

- длительность и количество ошибок по стадиям;
- очередь, возраст job, retries и failed jobs;
- число извлечённых символов/chunks и время индексации;
- retrieval candidate count, top score, Recall@K в offline evaluation;
- prompt/completion tokens, вызовы, rate limits и оценочная стоимость;
- TTFT и полная latency ответа;
- доля insufficient-evidence и невалидных citations.

Теги с высокой кардинальностью (`documentId`, `questionId`, полный user ID) не должны быть metric labels; их можно хранить в защищённых traces/logs. Содержимое документов допускается в диагностике только при явном безопасном режиме.

## 11. Ошибки и деградация

| Ситуация | Поведение |
|---|---|
| PDF не содержит извлекаемого текста | `Failed/ocr_required`; OCR не запускается в MVP |
| Embedding provider временно недоступен | Retry с backoff; версия не публикуется частично |
| LLM недоступна | `503 provider_unavailable`; retrieval run сохраняется; опционально вернуть найденные источники |
| Reranker недоступен | Конфигурируемая деградация к fusion ranking с отметкой в trace |
| Релевантных chunks нет | `insufficient_evidence` с пустым или явно обозначенным списком кандидатов |
| Citation не прошла проверку | Один ограниченный repair attempt, затем безопасное сокращение/отказ |
| Клиент оборвал SSE | Отменить generation при возможности и корректно завершить/пометить сообщение |
| Один файл повреждён | Только его version/job переходит в `Failed` |
| Outbox временно не публикуется | Бизнес-состояние сохраняется; dispatcher/reconciliation повторяет публикацию без дубликатов |
| Удаление blob временно не удалось | Tombstone продолжает исключать документ из retrieval; cleanup повторяется и создаёт alert после лимита |
| Новый index profile не прошёл evaluation | Активным остаётся прежний profile; новый получает `Failed`/не активируется |
| Provider rate limit | Уважать retry hint, применять bounded retry/queue; не занимать worker бесконечным ожиданием |

## 12. Предлагаемая структура solution

```text
src/
  OpsMind.Api/             HTTP, auth, SSE, composition root
  OpsMind.Worker/          background indexing host
  OpsMind.Application/     use cases and ports
  OpsMind.Domain/          entities, policies, value objects
  OpsMind.Infrastructure/  PostgreSQL, object storage, AI providers
tests/
  OpsMind.UnitTests/
  OpsMind.IntegrationTests/
  OpsMind.ArchitectureTests/
  OpsMind.EvaluationTests/
docs/
```

Это ориентир, а не требование к количеству проектов. Границы важнее механического следования Clean Architecture.

## 13. Проверка требований

Минимальный набор автоматических проверок:

- unit: chunk boundaries, fusion, confidence, citation validation, access policies;
- integration с настоящим PostgreSQL/pgvector: migrations, vector + FTS query, workspace filters, atomic publication;
- API: upload/new version/status/question/source/history/membership/deletion, pagination, idempotency и Problem Details;
- security: IDOR и межтенантный retrieval во всех candidates/cache/prompt/citations, а также composite tenant constraints;
- worker: outbox, reconciliation, retry, lease expiry, idempotency, cancellation, orphan cleanup и poison document isolation;
- contract tests адаптеров AI-провайдеров;
- offline evaluation из `evaluation-dataset.md` с сохранением версии модели и конфигурации.
- load tests на расчётном профиле с filtered ANN recall, P95/TTFT, backlog и восстановлением после provider throttling;
- backup/restore и проверка physical deletion.

## 14. Последовательность реализации

1. Solution skeleton, PostgreSQL/pgvector, tenant/workspace и auth context.
2. Upload, object storage, версии, transactional outbox и фоновые jobs.
3. Парсеры, chunking и inspection endpoint.
4. Embeddings и vector retrieval с обязательным workspace filter.
5. Grounded generation, citations, insufficient-evidence.
6. История и SSE.
7. PostgreSQL FTS, hybrid fusion и reranker.
8. Материализованный evaluation runner, калибровка параметров и quality gates.
9. Load tests, backup/restore, deletion verification и проверка расчётного профиля.
