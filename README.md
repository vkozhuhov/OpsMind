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

- [Продуктовые требования](docs/product-requirements.md)
- [Системные требования и архитектура](docs/system-requirements.md)
- [Архитектура реализации](docs/architecture.md)
- [Evaluation-набор](docs/evaluation-dataset.md)

## Статус

Solution skeleton и базовые правила сборки подготовлены. Следующий этап — локальная инфраструктура PostgreSQL/pgvector и первая вертикаль `upload -> indexing -> status`.

