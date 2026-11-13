# OpsMind — эпики и последовательность разработки

**Статус:** рабочий roadmap MVP  
**Версия:** 1.0 
**Связанные документы:** [Product requirements](product-requirements.md), [System requirements](system-requirements.md), [Architecture](architecture.md), [Evaluation dataset](evaluation-dataset.md)

## 1. Назначение

Документ определяет крупные этапы разработки OpsMind и их обязательную последовательность. Он отвечает на вопрос «что реализовывать следующим», но пока не дробит работу на user stories и технические задачи.

Каждый эпик должен завершаться работающей и проверяемой возможностью. Новый эпик начинается после прохождения exit criteria предыдущего, если явно не указано иное. Это защищает проект от ситуации, когда LLM-интеграция уже есть, а безопасного retrieval, измеримого качества или надёжной индексации ещё нет.

## 2. Как читать roadmap

- `EPIC-00` завершён: foundation-сервисы собираются, миграция применена и повторно выполнена на чистом PostgreSQL с pgvector, API health endpoints проверены, unit/architecture tests проходят.
- Порядок ниже является порядком зависимостей, а не оценкой длительности.
- Tests, tenant isolation, observability, migrations и документация входят в каждый соответствующий эпик и не откладываются до конца.
- `EPIC-12` и `EPIC-13` являются итоговыми release gates, а не первым моментом, когда в проекте появляются безопасность и эксплуатационные требования.
- Детальные задачи будут создаваться внутри эпика только перед началом его реализации.

## 3. Общая последовательность

```text
EPIC-00  Инженерный фундамент
   ↓
EPIC-01  Tenant, identity и управление доступом
   ↓
EPIC-02  Надёжная загрузка документов и статус обработки
   ↓
EPIC-03  Parsing, нормализация и chunking
   ↓
EPIC-04  Embeddings и векторный индекс
   ↓
EPIC-05  Семантический поиск и просмотр источника
   ↓
EPIC-06  Evaluation baseline и измерение качества
   ↓
EPIC-07  Grounded-ответ и citations
   ↓
EPIC-08  Гибридный поиск
   ↓
EPIC-09  Reranking, evidence gate и confidence
   ↓
EPIC-10  История диалогов и SSE streaming
   ↓
EPIC-11  Жизненный цикл знаний и blue/green reindex
   ↓
EPIC-12  Security hardening и adversarial-проверки
   ↓
EPIC-13  Эксплуатационная готовность и выпуск MVP
```

### Контрольные точки

| Milestone | После эпика | Полученный результат |
|---|---|---|
| M0 — Development platform | EPIC-00 | Проект воспроизводимо собирается и запускает локальную инфраструктуру |
| M1 — Secure ingestion | EPIC-03 | Пользователь безопасно загружает документ и получает проверяемые chunks |
| M2 — Searchable knowledge | EPIC-06 | Семантический поиск работает и имеет измеренный baseline качества |
| M3 — Core RAG | EPIC-09 | Система выдаёт доказуемый ответ через hybrid retrieval и reranking |
| M4 — Product flow | EPIC-10 | Доступен полный пользовательский сценарий с историей и streaming |
| M5 — Release candidate | EPIC-13 | Пройдены security, quality, load и recovery gates MVP |

## 4. Каталог эпиков

### EPIC-00. Инженерный фундамент

**Цель:** сделать локальную разработку, сборку, тестирование и диагностику проекта воспроизводимыми.

**Включает:**

- текущую структуру solution и правила project references;
- central package management, analyzers, nullable и warnings policy;
- Docker Compose с PostgreSQL/pgvector, object storage и OpenTelemetry Collector;
- `OpsMind.Migrator`, базовые migrations и startup checks;
- health checks, базовые `ActivitySource`/`Meter` conventions;
- CI-команды build/test и первые architecture tests;
- документированный запуск из чистого checkout.

**Зависимости:** отсутствуют.

**Exit criteria:** чистый checkout запускается одной документированной последовательностью команд; API, Worker и Migrator стартуют; migration и smoke tests проходят; секреты не находятся в репозитории.

**Текущий статус:** завершён. API, Worker и Migrator собираются; миграция создаёт `vector` и базовые schemas, успешно применяется и повторно запускается без изменений на чистом PostgreSQL; API liveness/readiness отвечают `200`; Worker запускается; 8 unit/architecture tests проходят. На текущем локальном томе PostgreSQL обнаружена старая инициализация: контейнер настроен на роль `opsmind`, но такой роли в сохранённом кластере нет. Это состояние существующего локального тома; оно не воспроизводится на чистой БД и не требует менять миграцию.

---

### EPIC-01. Tenant, identity и управление доступом

**Цель:** создать границу безопасности до появления документов и RAG-кода.

**Включает:**

- `User`, `Organization`, `OrganizationMember`, `Workspace`, `WorkspaceMember`;
- dev authentication и provider-neutral mapping identity claims;
- роли `Admin`, `Editor`, `Reader` и authorization policies;
- обязательный `AccessScope` для application use cases;
- tenant columns, composite foreign keys и PostgreSQL RLS;
- управление участниками workspace и audit metadata;
- negative API/DB tests для cross-tenant, cross-workspace и IDOR.

**Зависимости:** EPIC-00.

**Exit criteria:** пользователь без нужного membership не может обнаружить или прочитать ресурс другого workspace; это подтверждено на уровне API и реальной PostgreSQL.

---

### EPIC-02. Надёжная загрузка документов и статус обработки

**Цель:** реализовать безопасный путь `upload → queued job → status`, пока ещё с тестовым обработчиком содержимого.

**Включает:**

- модели `Document`, `DocumentVersion`, `DocumentIndex`, `IndexProfile` и их состояния;
- `IBlobStorage` и immutable-хранение исходных файлов;
- allow-list форматов, лимиты размера, checksum и базовую валидацию;
- upload, status и retry API;
- idempotency keys для изменяющих запросов;
- transactional outbox-as-queue, lease, retry, backoff и poison-job isolation;
- Worker, reconciliation и audit событий индексации.

**Зависимости:** EPIC-01.

**Exit criteria:** загрузка возвращает `Queued` без ожидания обработки; повтор команды и crash/retry не создают дубликаты; сбой одного файла не блокирует остальные jobs.

---

### EPIC-03. Parsing, нормализация и chunking

**Цель:** преобразовать поддерживаемый документ в стабильные, адресуемые фрагменты с корректными metadata и координатами источника.

**Включает:**

- registry реализаций `IDocumentParser` для Markdown, TXT и PDF с text layer;
- безопасную обработку malformed, empty и OCR-required файлов;
- canonical document representation;
- нормализацию текста без потери координат источника;
- hierarchical chunking с heading path, overlap, token limits и metadata;
- версионируемую конфигурацию parser/chunker в `IndexProfile`;
- persistence chunks и внутренний inspection endpoint;
- indexing fixtures и проверки I01–I08 из evaluation-документа.

**Зависимости:** EPIC-02.

**Exit criteria:** каждый поддерживаемый fixture либо создаёт ожидаемые chunks с воспроизводимыми координатами, либо завершается стабильным error code; частичные chunks не публикуются.

---

### EPIC-04. Embeddings и векторный индекс

**Цель:** построить воспроизводимый и атомарно публикуемый semantic index.

**Включает:**

- application port для embeddings и адаптер через `Microsoft.Extensions.AI`;
- deterministic fake provider для тестов;
- batch processing, timeout, retry, rate-limit handling и usage accounting;
- `EmbeddingModel`, dimensions/model validation и `ChunkEmbedding`;
- pgvector и HNSW indexes с operational migration;
- staging и атомарную публикацию полностью готового `DocumentIndex`;
- идемпотентность повторной индексации и cleanup незавершённых данных.

**Зависимости:** EPIC-03.

**Exit criteria:** готовый документ имеет полный набор embeddings одной модели и размерности; неполный индекс не участвует в поиске; повторная обработка не создаёт дубликаты.

---

### EPIC-05. Семантический поиск и просмотр источника

**Цель:** получить первый полезный retrieval без LLM и доказать корректную tenant-фильтрацию до ранжирования.

**Включает:**

- embedding пользовательского вопроса;
- vector top-K retrieval с фильтрами workspace, service, version и date;
- только активные версии документов и активный `IndexProfile`;
- детерминированный порядок при равных score;
- `RetrievalRun` и диагностические candidates/scores;
- endpoint поиска фрагментов;
- endpoint открытия source chunk с соседним контекстом и координатами;
- filtered ANN recall и tenant-security integration tests.

**Зависимости:** EPIC-04.

**Exit criteria:** пользователь находит релевантные chunks и открывает исходный фрагмент, а недоступный chunk отсутствует уже среди candidates, а не отфильтровывается после retrieval.

---

### EPIC-06. Evaluation baseline и измерение качества

**Цель:** заменить субъективное впечатление от поиска воспроизводимыми метриками до подключения генерации.

**Включает:**

- materialized fixtures и JSONL manifest из `evaluation-dataset.md`;
- smoke, development и holdout splits;
- evaluation runner с HitRate@K, Recall@K, MRR и category breakdown;
- сохранение commit, dataset version, index profile, model IDs, параметров и результатов;
- проверки indexing, tenant isolation и source coordinates;
- первую baseline-конфигурацию vector retrieval;
- CI smoke gate и отдельный запуск полного evaluation.

**Зависимости:** EPIC-05. Подготовка fixtures может начинаться в EPIC-03.

**Exit criteria:** один и тот же snapshot/config даёт воспроизводимый retrieval result; baseline сохранён; регрессия поиска и tenant leakage автоматически обнаруживаются.

---

### EPIC-07. Grounded-ответ и citations

**Цель:** реализовать первый полный сценарий «вопрос → доказуемый ответ» без streaming и без преждевременной оптимизации retrieval.

**Включает:**

- provider-neutral chat adapter и deterministic fake;
- формирование `GroundingContext` только из разрешённых retrieved chunks;
- system prompt, в котором документы являются недоверенными данными;
- базовый evidence gate и результат `insufficient_evidence`;
- генерацию структурированного ответа;
- structural citation validation: chunk, quote, coordinates и принадлежность context;
- snapshot ответа, citations, model/prompt/config versions, tokens и стоимость;
- endpoint `POST /api/questions` с complete JSON response.

**Зависимости:** EPIC-06.

**Exit criteria:** каждый существенный факт ответа подтверждён валидной citation из generation context; при отсутствии доказательств система не дополняет ответ знаниями модели.

---

### EPIC-08. Гибридный поиск

**Цель:** одинаково хорошо находить смысловые совпадения и точные инженерные идентификаторы.

**Включает:**

- PostgreSQL FTS representation и GIN indexes;
- отдельные vector и lexical candidate pools;
- fusion через RRF;
- deduplication и document/section diversity;
- сохранение ranks/scores каждого retrieval stage;
- сравнение `vector only`, `FTS only` и `hybrid` на evaluation-наборе;
- query-plan и latency проверки на расчётном объёме.

**Зависимости:** EPIC-07.

**Exit criteria:** hybrid retrieval не нарушает tenant filters и детерминизм, улучшение подтверждено evaluation по общим вопросам и exact identifiers без неприемлемой регрессии latency.

---

### EPIC-09. Reranking, evidence gate и confidence

**Цель:** улучшить порядок доказательств и выдавать пользователю объяснимую оценку достаточности ответа.

**Включает:**

- provider-neutral `IReranker` и fallback без reranker;
- reranking ограниченного, уже авторизованного candidate set;
- калибруемый evidence gate после reranking;
- confidence `high`, `medium`, `low` на наблюдаемых сигналах;
- машиночитаемые confidence reasons;
- timeout, circuit breaker, degraded mode и cost accounting;
- ablation `hybrid` против `hybrid + reranker`;
- калибровку thresholds на development split без подглядывания в holdout.

**Зависимости:** EPIC-08.

**Exit criteria:** reranker даёт измеримое улучшение или обоснованно отключается; недоступность provider не нарушает безопасность; confidence не является самооценкой LLM.

---

### EPIC-10. История диалогов и SSE streaming

**Цель:** завершить пользовательский flow, сохранив проверяемость ответа и корректное поведение при разрыве соединения.

**Включает:**

- `Conversation`, `Message` и личную историю пользователя;
- authorization/IDOR checks для conversation, message, answer и citation;
- single-turn retrieval: история пока не переписывает запрос;
- paragraph-buffered SSE events и финальный authoritative result;
- порядок событий, error contract, cancellation и client disconnect;
- неизменяемые citation snapshots для исторических ответов;
- API и contract tests для JSON/SSE/history.

**Зависимости:** EPIC-09.

**Exit criteria:** пользователь получает потоковый ответ, затем видит тот же итоговый ответ в своей истории; частичный поток не сохраняется как успешно завершённый; чужая история недоступна.

---

### EPIC-11. Жизненный цикл знаний и blue/green reindex

**Цель:** безопасно обновлять, удалять и переиндексировать знания без публикации неполного или непроверенного индекса.

**Включает:**

- загрузку новой версии существующего документа;
- атомарное переключение active version;
- deactivate, tombstone, retention и physical cleanup;
- сохранение или удаление citation snapshot согласно retention policy;
- reconciliation orphaned blobs/chunks/embeddings;
- building/active/retired lifecycle `IndexProfile`;
- blue/green reindex, catch-up изменений и evaluation gate перед activation;
- atomic profile activation, rollback и cleanup старого индекса.

**Зависимости:** EPIC-10.

**Exit criteria:** новая версия или профиль становится доступным только после полной индексации и quality gate; rollback атомарен; удалённый документ немедленно исключается из retrieval и затем физически очищается.

---

### EPIC-12. Security hardening и adversarial-проверки

**Цель:** доказать, что полный pipeline сохраняет границы доступа и не исполняет инструкции из пользовательского или документного контента.

**Включает:**

- полный IDOR audit всех resource IDs и endpoints;
- проверку изоляции candidates, cache, reranker, prompt, telemetry и citations;
- direct и indirect prompt injection scenarios;
- защиту parser pipeline от oversized, malformed и malicious files;
- redaction секретов и чувствительного содержимого из logs/traces/errors;
- SSRF/path traversal/content-type и decompression limits там, где применимо;
- повторную авторизацию при чтении history/source snapshots;
- автоматические security gates S01–S10 и Q18/Q27.

**Зависимости:** EPIC-11. Базовые security tests реализуются начиная с EPIC-01.

**Exit criteria:** security leakage rate равен нулю; все обязательные tenant, IDOR, citation integrity и prompt injection tests проходят на 100%.

---

### EPIC-13. Эксплуатационная готовность и выпуск MVP

**Цель:** подтвердить, что систему можно не только продемонстрировать, но и предсказуемо эксплуатировать в границах MVP.

**Включает:**

- end-to-end OpenTelemetry traces от HTTP-запроса до retrieval и AI providers;
- dashboards и alerts для latency, errors, queue backlog, tokens и стоимости;
- quotas и rate limits для upload, indexing и questions;
- timeouts, retries, bulkheads и circuit breakers;
- load tests по расчётному профилю MVP;
- backup/restore test с зафиксированными RPO/RTO;
- проверку recovery после provider throttling и worker crash;
- финальный evaluation release run и публикацию результатов;
- deployment/runbook, configuration/secrets guide и release checklist.

**Зависимости:** EPIC-12.

**Exit criteria:** достигнуты product quality gates, P95/TTFT и indexing targets; backup восстановлен; physical deletion проверено; полный сценарий MVP проходит в Docker Compose и виден в одной распределённой трассе.

## 5. Definition of Done для каждого эпика

Эпик закрывается только тогда, когда одновременно выполнены применимые условия:

- реализован заявленный end-to-end результат, а не только отдельные классы;
- пройдены unit, integration, API, security и evaluation tests соответствующего уровня;
- tenant/workspace scope применяется до чтения или передачи данных внешнему provider;
- добавлены migrations, constraints, indexes и проверен путь обновления, если меняется схема;
- добавлены traces, metrics и безопасные structured logs для нового потока;
- ошибки имеют стабильные коды и не оставляют частично опубликованное состояние;
- обновлены OpenAPI/contracts, документация и ADR для нового архитектурного решения;
- `dotnet build` и `dotnet test` проходят из чистого checkout;
- в репозитории отсутствуют секреты и provider credentials.

## 6. Покрытие продуктовых требований

| Область требований | Основные эпики |
|---|---|
| Tenant, roles и membership | EPIC-01, EPIC-12 |
| Upload, status, retry и idempotency | EPIC-02 |
| Parsing, chunks и metadata | EPIC-03 |
| Embeddings и semantic retrieval | EPIC-04, EPIC-05 |
| Evaluation и воспроизводимость | EPIC-06, EPIC-08, EPIC-09, EPIC-13 |
| Grounding, citations и abstention | EPIC-07, EPIC-09 |
| Hybrid search и reranking | EPIC-08, EPIC-09 |
| History и streaming | EPIC-10 |
| Versioning, deletion и reindex | EPIC-11 |
| Security, quotas и эксплуатация | EPIC-12, EPIC-13 |

## 7. Правило ведения в GitHub

Каждый раздел `EPIC-XX` создаётся как отдельный GitHub Issue с таким же заголовком. Внутри него связываются user stories и технические задачи текущего эпика. Одновременно активным должен быть один основной эпик; подготовительные задачи следующего допустимы только если они не обходят exit criteria текущего.

Рекомендуемые статусы: `Backlog → Ready → In progress → Validation → Done`. Эпик нельзя переводить в `Done`, пока не пройдены его exit criteria и применимые пункты общего Definition of Done.
