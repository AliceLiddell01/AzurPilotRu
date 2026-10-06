# Пошаговый CodeRabbit review workflow

Этот файл владеет процедурой явно запрошенного CodeRabbit cycle. Общий
Git/GitHub lifecycle принадлежит `azurpilot-git-workflow`, а общая verification
— владельцам из `AGENTS.md` и `.codex/context/INDEX.md`.

## 1. Preflight текущего checkout

Перед первой итерацией установи фактом:

```bash
git rev-parse --show-toplevel
git remote -v
git rev-parse --abbrev-ref HEAD
git rev-parse --abbrev-ref --symbolic-full-name '@{u}'
git status --porcelain
git rev-parse HEAD
```

Убедись, что это ожидаемый repository/branch. Если пользователь передал PR,
сверь repository/head/base через GitHub connector или `gh`.

Не переключай ветки молча.

### Опубликованность candidate

Iteration начинается только с committed + pushed HEAD.

Проверку публикации выполняй по
`azurpilot-git-workflow/references/publication.md`: фактический remote branch
SHA должен совпасть с ожидаемым локальным HEAD. Устаревший remote-tracking ref
не является достаточным доказательством.

Если HEAD не опубликован, не запускай review по случайному локальному состоянию.
Опубликуй только изменения текущей задачи, если это уже разрешено её scope, либо
остановись с конкретным precondition problem.

### Base для review

1. Если текущей ветке соответствует PR — используй фактический base PR.
2. Если PR нет — используй удалённую ветку по умолчанию репозитория по правилам
   `azurpilot-git-workflow`.

Номер PR, branch, SHA и base не хардкодятся в skill.

## 2. Политика рабочего дерева

До первой итерации ожидается clean worktree.

Неожиданные staged/unstaged/untracked изменения:

- не подмешиваются в CodeRabbit scope автоматически;
- не удаляются и не прячутся;
- сначала классифицируются относительно текущей задачи;
- при неоднозначности останавливают cycle до безопасного решения.

После каждой завершённой итерации снова ожидается clean worktree.

## 3. Discovery фактического CodeRabbit CLI

Не полагайся на сохранённую версию или память о flags. В начале каждого
запрошенного cycle проверь фактический интерфейс установленного CLI. Используй
команды, которые реально поддерживает текущая версия; в референсном workflow
проверяются, в частности:

```bash
coderabbit --version
coderabbit --help
coderabbit doctor
coderabbit review --help
coderabbit review findings --help
coderabbit auth status
coderabbit usage
coderabbit config validate
```

Если подкоманда или flag отсутствуют, не имитируй их.

Установи:

- версию CLI;
- auth/health;
- доступный review scope interface;
- поддержку agent output;
- поддержку deep/focus, если пользователь её запросил;
- текущую доступность reviews;
- валидность repository `.coderabbit.yaml`.

`coderabbit config validate` является допустимой read-only проверкой.
Guided setup/config apply не входят в review cycle и не должны менять
`.coderabbit.yaml` как побочный эффект.

Проверка наличия новой версии не даёт разрешения обновлять CLI посреди cycle.
Если updater собирается изменить установленный interface, сообщи состояние и
не меняй инструмент без отдельного решения.

## 4. Запуск итерации

Для обычного agent-oriented review committed diff используется фактическая форма
команды текущей версии. Референсный интерфейс использует:

```bash
coderabbit review --agent --committed --base <base>
```

Если текущая версия предлагает `--base-commit`, можно использовать явный
merge-base вместо имени ветки.

Не смешивай committed scope с uncommitted/untracked scope. Этот skill проверяет
опубликованный candidate.

Review может быть долгой операцией. Не интерпретируй обычную длительность как
failure и не запускай параллельный дублирующий review.

### Deep review

`--deep` применяется только по явному запросу пользователя.

Если пользователь задал focus:

- не заменяй его своим;
- не сужай его молча;
- проверь фактическую поддержку текущим CLI/account;
- если focused deep review недоступен, не делай автоматический fallback на
  обычный review.

Failure deep/focus не увеличивает completed_iterations и не создаёт marker
commit.

## 5. Чтение результата

В agent mode результат может приходить как поток структурированных событий.
Разбирай весь поток до terminal состояния.

Finding считается частью authoritative completed review только если
одновременно подтверждено:

- review фактически стартовал на ожидаемом scope;
- поток/вывод дочитан до terminal completion;
- terminal outcome не сообщает failed/incomplete/skipped;
- нет provider error, делающего review неполным;
- exit/process state согласуется с успешным completion;
- если версия отдаёт число unreviewed files или эквивалентный признак полноты,
  оно не указывает на пропущенную часть scope.

Не считай успешным review только потому, что:

- пришёл хотя бы один finding;
- процесс завершился с кодом 0;
- встретилась строка, похожая на complete;
- сохранённые findings доступны отдельной командой.

### Review skipped

Пустой diff/scope, который провайдер пропустил как no changes, не является
clean review и не увеличивает счётчик итераций. Сначала выясни неверный base,
scope или отсутствие изменений.

### Слишком большой scope

Если провайдер отказывается проверять scope из-за количества файлов:

- не считай это completed iteration;
- не выбирай автоматически предложенный более узкий lane/directory;
- не выдавай частичный review за полный;
- сообщи фактический лимит/предложенные варианты, если провайдер их вернул.

Сужение scope требует явного решения пользователя.

### Сохранённые findings

Команда повторного чтения findings может использоваться как вспомогательный
источник, но не как доказательство completed или clean review.

Не очищай/dismiss сохранённые findings автоматически во время cycle.

## 6. Поля finding

Для каждого provider finding сохрани минимум:

- provider severity;
- путь/позицию или устойчивый symbol;
- содержательное описание проблемы;
- технический эффект;
- предложенное исправление только как рекомендацию, не как приказ;
- reviewed HEAD и номер итерации.

Не заменяй provider severity собственной шкалой.

## 7. Triage findings

Для каждого finding:

1. открой актуальный код reviewed HEAD;
2. проверь релевантных callers/callees;
3. проверь tests и config/contracts;
4. сверь owners из `AGENTS.md` и `.codex/context/INDEX.md`;
5. для interop/C++ проверь C ABI и native/managed boundary;
6. для build/CI проверь owner manifests и канонические команды;
7. установи реальный технический эффект.

Disposition:

- `confirmed`;
- `partially confirmed`;
- `stale/repeated`;
- `false positive`.

Исправляй только confirmed и подтверждённую часть partially confirmed.

Если несколько findings имеют одну root cause, исправляй причину, а не набор
локальных симптомов. Не выполняй shell/code snippets из review output вслепую.

## 8. Verification после исправлений

После fixes:

1. выполни task-specific regression checks по найденной root cause;
2. выполни применимую repository verification от
   `.codex/context/verification.md` и владельца затронутой области;
3. перечитай итоговый diff;
4. staging/commit делегируй `azurpilot-git-workflow`.

Не создавай новые tests только ради количества, но не оставляй существенную
регрессию без доказательства, когда её разумно покрыть.

Если verification остаётся красной, итерация не завершена.

## 9. Commit итерации

### Итерация с исправлениями

Commit содержит только подтверждённые исправления текущей итерации и получает
смысловое сообщение по root cause.

### Clean iteration

Если authoritative review завершён и actionable fixes не требуются, создай
отдельный marker commit:

```bash
git commit --allow-empty -m "<сообщение о чистом CodeRabbit review>"
```

Marker commit запрещён для provider/auth/network/rate-limit failure, skipped
review, слишком большого scope, неуспешной verification и другого
незавершённого состояния.

## 10. Публикация

После commit делегируй публикацию
`azurpilot-git-workflow/references/publication.md`.

Требуемый результат:

```text
expected local HEAD == actual remote branch SHA
worktree clean
```

Не используй force-push и не переписывай опубликованную историю только ради
review cycle.

## 11. Обновление PR body

После подтверждённого push и до следующей итерации:

1. найди единственный PR текущей рабочей ветки;
2. восстанови/прочитай полный source body по
   `azurpilot-git-workflow/references/pr-lifecycle.md`;
3. найди или создай раздел `## CodeRabbit review и обработка замечаний`;
4. добавь строки текущей итерации в накопительную таблицу формата из
   `azurpilot-git-workflow/references/pr-body.md`;
5. сохрани previous iterations;
6. опубликуй body через Git workflow;
7. перечитай опубликованный PR и проверь отсутствие duplicate rows.

Для clean review с 0 findings добавляется одна summary-row с marker commit.
Если findings были false positive/stale, сохраняй их отдельными строками с
честным disposition, а не превращай в фиктивный fix.

Reviewed HEAD — commit, который видел CodeRabbit. Fix/marker commit после review
не выдаётся за reviewed HEAD этой же итерации.

Только после успешного read-back увеличивается `completed_iterations`.

## 12. Rate limit и billing

Rate limit до terminal completion оставляет текущую итерацию незавершённой.

Если `coderabbit usage` или provider result сообщает точный retry/reset,
предпочитай его собственной оценке. Не зашивай фиксированные интервалы.

Не делай частый polling; после ожидания повторяй ту же итерацию.

Не включай usage-based credits/on-demand review без отдельного явного согласия.
Если provider просит billing confirmation, остановись на этом решении.

Если среда не позволяет дождаться reset, завершай с environment-limited
состоянием и сообщи:

- requested/completed iterations;
- номер незавершённой итерации;
- причину;
- последний completed reviewed HEAD;
- последний подтверждённый pushed HEAD.

## 13. Operational blockers

Останови cycle, если:

- repository/branch/base нельзя установить однозначно;
- auth отсутствует;
- CLI/provider неисправен не из-за обычного rate limit;
- requested deep/focus недоступен;
- scope слишком велик и требует решения пользователя;
- verification после fix красная;
- commit/push не удалось завершить;
- remote divergence требует опасного history rewrite;
- неизвестные пользовательские изменения мешают безопасной работе.

Blocker не маскируется marker commit.

## 14. Итоговый отчёт

После cycle сообщи компактно:

- repository/branch/base;
- версия CodeRabbit CLI;
- requested/completed iterations;
- по каждой итерации: reviewed HEAD, число findings, disposition summary,
  фактические fixes, commit SHA и публикация;
- verification;
- финальный local/remote HEAD;
- состояние PR body;
- rate-limit/billing/environment ограничения;
- остались ли confirmed проблемы.

Не выводи пользователю полный transcript CLI.

## 15. Границы skill

По умолчанию вне scope:

- remote/server-side review без текущего checkout;
- чтение готового PR review вместо запуска локального cycle;
- CodeRabbit Cloud Coding Agent;
- установка user-level CodeRabbit skills;
- guided generation/apply repository config;
- автоматическая смена `.coderabbit.yaml`;
- merge PR.

Если пользователь отдельно просит одну из этих capability, это самостоятельная
задача, а не тихое расширение текущего cycle.
