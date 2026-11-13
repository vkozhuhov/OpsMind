# OpsMind

OpsMind — RAG-платформа для поиска по внутренней технической документации и инженерным инцидентам.

Система должна отвечать только на основании доступных пользователю документов и сопровождать фактические утверждения ссылками на конкретные фрагменты источников. Если доказательств недостаточно, OpsMind возвращает явный результат `insufficient_evidence`, а не дополняет ответ знаниями модели.

## MVP

- загрузка Markdown, TXT и текстовых PDF;
- фоновое извлечение текста, chunking и построение embeddings;
- PostgreSQL, pgvector и полнотекстовый поиск;
- hybrid retrieval и reranking;
- изоляция данных по organization/workspace;
- grounded-ответы с проверяемыми citations;
- SSE streaming и история вопросов;
- OpenTelemetry-трассировка retrieval и LLM-вызовов;
- воспроизводимый evaluation-набор.

```text
Documents -> parse -> chunk -> embed -> PostgreSQL + pgvector
                                              |
Question -> hybrid retrieval -> rerank -> LLM -> answer + citations
```

## Планируемый стек

- ASP.NET Core и .NET;
- PostgreSQL + pgvector;
- Microsoft.Extensions.AI;
- OpenAI-compatible API для embeddings и chat completion;
- PostgreSQL transactional outbox-as-queue и отдельный indexing worker;
- OpenTelemetry;
- Docker Compose.

## Документация

- [Эпики и последовательность разработки](docs/epics.md)
- [Продуктовые требования](docs/product-requirements.md)
- [Системные требования и архитектура](docs/system-requirements.md)
- [Архитектура реализации](docs/architecture.md)
- [Evaluation-набор](docs/evaluation-dataset.md)

## Локальная трассировка

### Первый запуск

Из корня репозитория:

```bash
cp deploy/.env.example deploy/.env
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d
set -a
. deploy/.env
set +a
export ConnectionStrings__OpsMind="Host=localhost;Port=$POSTGRES_PORT;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
dotnet run --project OpsMind.Migrator
dotnet run --project OpsMind.Api --launch-profile http
```

В другом терминале выполни настройку соединения и запусти Worker:

```bash
set -a
. deploy/.env
set +a
export ConnectionStrings__OpsMind="Host=localhost;Port=$POSTGRES_PORT;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
dotnet run --project OpsMind.Worker
```

API liveness доступен по `GET http://localhost:5001/health`, а readiness с проверкой PostgreSQL — по `GET http://localhost:5001/health/ready`. Миграции запускаются только отдельным `OpsMind.Migrator`, API и Worker не меняют схему при старте.

`deploy/.env` содержит локальные секреты и не попадает в Git. Для повторного старта достаточно поднять Compose и выполнить команды экспорта и запуска приложений.

### Трассировка

В Jaeger по адресу `http://localhost:16686` выбери сервис `OpsMind.Api`, чтобы увидеть трассу HTTP-запроса.

По умолчанию OTLP exporter отправляет данные в `http://localhost:4317`. Endpoint можно переопределить стандартной переменной `OTEL_EXPORTER_OTLP_ENDPOINT`. Collector принимает OTLP по gRPC на порту `4317` и HTTP на `4318`.
