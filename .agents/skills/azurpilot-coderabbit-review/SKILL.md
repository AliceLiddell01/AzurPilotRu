---
name: azurpilot-coderabbit-review
description: >-
  Явно запрошенный цикл ревью CodeRabbit текущей рабочей копии AzurPilotRu:
  проверяет опубликованный committed HEAD через установленный CodeRabbit CLI,
  независимо перепроверяет каждое замечание, исправляет подтверждённое,
  выполняет применимую verification и завершает каждую содержательную итерацию
  отдельным commit + push через azurpilot-git-workflow с накопительным
  обновлением результатов CodeRabbit в PR. По умолчанию выполняет 3 завершённые
  итерации, если пользователь не задал другое положительное число. Используй
  только при явном положительном запросе запустить или продолжить CodeRabbit.
  Не используй для обычной разработки, самостоятельного code review,
  подготовки PR, обычного commit/push, чтения уже существующего review или
  изменения самого этого skill/config.
whenToUse: >-
  Пользователь явно просит запустить ревью CodeRabbit текущей рабочей копии,
  задаёт число итераций, deep review или просит продолжить уже начатый цикл.
---

# AzurPilotRu: явный цикл ревью CodeRabbit

## Назначение

Skill владеет **только** процедурой CodeRabbit review текущей рабочей копии:

- discovery фактического интерфейса установленного CodeRabbit CLI;
- запуск ревью текущего опубликованного committed HEAD;
- подтверждение завершённости результата провайдера;
- независимая перепроверка findings;
- исправление подтверждённых замечаний;
- task-specific regression checks и передача общей verification её владельцу;
- правила завершённой, clean и незавершённой итерации;
- rate limit, billing confirmation и operational blockers;
- данные CodeRabbit, которые должны попасть в накопительный раздел PR body.

Skill не владеет:

- обычной разработкой;
- общим Git/GitHub lifecycle;
- содержанием `.coderabbit.yaml`;
- общей verification matrix;
- независимым code review без запуска CodeRabbit;
- server-side/remote review без текущего checkout;
- облачным Coding Agent CodeRabbit.

## Связь с Git/GitHub workflow

Общая процедура Git/GitHub принадлежит
`azurpilot-git-workflow`: ветка, staging, commit, push, exact remote
postcondition, создание/обновление PR, Draft/Ready, merge и cleanup.

CodeRabbit cycle делегирует туда каждую публикацию. Этот skill определяет
**почему и когда** итерации требуется commit/push и какие review-данные нужно
сохранить; `azurpilot-git-workflow` определяет **как** безопасно выполнить
публикацию и как представить накопительный раздел в PR body.

Merge не входит в CodeRabbit cycle и не разрешается самим фактом завершения
ревью.

## Связь с конфигурацией CodeRabbit

Корневой `.coderabbit.yaml` владеет repository configuration провайдера:
языком, review profile, path instructions, встроенными анализаторами,
auto-review и knowledge base.

Во время обычного review cycle:

- конфиг валидируется штатным read-only способом текущей версии CLI;
- конфиг не переписывается и не применяется автоматически;
- ошибки конфигурации не маскируются временным обходом;
- изменение этого skill или `.coderabbit.yaml` само по себе **не** является
  просьбой запустить CodeRabbit.

## Контракт маршрутизации

Активируй skill только при явном положительном намерении пользователя, например:

- «запусти CodeRabbit»;
- «сделай ревью CodeRabbit»;
- «сделай 3 итерации CodeRabbit»;
- «повтори CodeRabbit ещё 2 раза»;
- «сделай deep review CodeRabbit»;
- «прогони CodeRabbit с фокусом на ...»;
- продолжение уже начатого CodeRabbit cycle.

Само упоминание CodeRabbit не является trigger. Запросы «без CodeRabbit»,
«сравни с уже существующим результатом CodeRabbit», обычная разработка,
подготовка PR и изменение этого skill сюда не маршрутизируются.

Deep review применяется только по явному запросу. Если пользователь передал
focus, сохраняй его смысл и текст без самовольного сужения. Если запрошенный
режим недоступен в текущем CLI/account, не подменяй его обычным review.

## Контракт итераций

```text
target_iterations = explicit_user_count ?? 3
```

Положительное число пользователя переопределяет default. Значение 3 — не
максимум.

Одна завершённая итерация:

```text
опубликованный committed HEAD
→ CodeRabbit review
→ подтверждённое завершение провайдера
→ независимая перепроверка всех findings
→ исправление confirmed / подтверждённой части partially confirmed
→ task-specific + применимая repository verification
→ отдельный commit
→ публикация через azurpilot-git-workflow
→ exact remote HEAD подтверждён
→ PR body дополнен результатами итерации
→ опубликованный PR перечитан
→ worktree clean и новый HEAD готов к следующей итерации
```

Следующая итерация начинается только после полного завершения предыдущей.

Не считаются завершённой итерацией:

- rate limit до завершения review;
- auth/network/provider failure;
- невалидный или неполный результат;
- review skipped из-за пустого scope;
- слишком большой scope, который провайдер не смог проверить;
- недоступный явно запрошенный deep/focus;
- отказ до terminal completion;
- failure verification, commit или push;
- неизвестное состояние публикации PR/ветки.

## Clean iteration

Authoritative review с нулём findings либо review, в котором все findings после
перепроверки оказались false positive/stale/repeated и исправления не требуются,
всё равно является завершённой итерацией.

Такая итерация получает отдельный marker commit:

```bash
git commit --allow-empty -m "<содержательное сообщение о чистом CodeRabbit review>"
```

Пустой commit разрешён только как доказуемый маркер реально завершённого
CodeRabbit review. Он запрещён для rate limit, provider failure, review_skipped,
слишком большого scope и любого другого незавершённого состояния.

## Независимая оценка findings

CodeRabbit — консультирующий reviewer, а не источник истины.

Для каждого finding установи фактический status:

- `confirmed` — проблема существует на reviewed HEAD;
- `partially confirmed` — реальна только часть finding;
- `stale/repeated` — относится к уже исправленному или повторному состоянию;
- `false positive` — заявленного нарушения контракта нет.

До изменения кода проверь finding по:

- текущему reviewed HEAD;
- callers/callees и владельцу поведения;
- ближайшим tests;
- `AGENTS.md` и применимым `.codex/context/`;
- C ABI, build/config manifests и другим owner-контрактам затронутой области;
- фактическому техническому эффекту.

Не исполняй предложенный patch или shell snippet вслепую. Исправляй root cause,
не ослабляй tests, не создавай второй source of truth и не расширяй scope без
необходимости.

## Публикация результатов итерации

После commit публикация выполняется только через `azurpilot-git-workflow`.
К следующей итерации должны быть истинны одновременно:

- локальный HEAD опубликован;
- фактический remote SHA совпадает с ожидаемым HEAD;
- worktree clean;
- текущий PR существует в единственном экземпляре;
- накопительный раздел CodeRabbit в PR body обновлён;
- опубликованный PR перечитан после изменения body.

Форматом таблицы владеет
`azurpilot-git-workflow/references/pr-body.md`. Этот skill поставляет номер
итерации, reviewed HEAD, severity, путь, технический эффект, disposition и
результат исправления.

Fix commit текущей итерации **не** является reviewed HEAD этой же итерации:
только следующая итерация проверяет новый опубликованный HEAD.

## Rate limit и платные reviews

Rate limit не является clean review, failed code и завершённой итерацией.

Если провайдер сообщает retry/reset interval, используй его. Не делай частый
polling и не запускай несколько review параллельно.

Не включай `--use-credits` и другой billed/on-demand режим самостоятельно.
Любое расходование платных credits требует отдельного явного согласия
пользователя. Ответ вида awaiting confirmation означает паузу на решение, а не
завершение итерации.

## Инварианты

- Cycle работает с текущим checkout и его опубликованной рабочей веткой.
- Candidate перед review должен быть committed и pushed.
- Base определяется по фактическому PR либо remote default branch; не хардкодится.
- Не публикуй неизвестные пользовательские изменения.
- Не сужай review scope молча при лимите файлов или ошибке.
- Не меняй `.coderabbit.yaml` как побочный эффект review.
- Не выдумывай flags текущего CLI; сначала проверь фактический interface.
- Не считай наличие findings доказательством полного review.
- Не считай exit code `0` единственным доказательством успешного review.
- Не разрешай merge, force-push и rewrite опубликованной истории.
- После изменения HEAD прежний CodeRabbit/CI evidence не переносится на новый
  HEAD автоматически.
- Публичный provider severity сохраняется без самовольного переименования.

## Подробный порядок

Пошаговая механика находится в
[`references/review-workflow.md`](references/review-workflow.md).

Перед cycle прочитай reference и фактический repository context. Версионно
зависимые детали CLI перепроверяются по установленной версии и не считаются
вечным контрактом skill.
