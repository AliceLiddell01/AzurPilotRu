# PR lifecycle: body, Draft/Ready, merge, конфликты и cleanup

Этот файл владеет порядком действий после подтверждённой публикации commit.
Контракт и границы задаёт `SKILL.md`; push postcondition описан в
[`publication.md`](publication.md).

## 1. Identity PR

Одна самостоятельная repository task имеет **один** PR.

1. Сначала установи, существует ли PR для текущей task branch: используй
   структурированное чтение GitHub connector либо `gh pr view --json ...`.
2. Если PR существует — обновляй его.
3. Если отсутствует — создай новый.

Не создавай второй PR только потому, что начался новый подшаг того же work item,
и не переключайся молча на другой PR.

## 2. Источник body PR

В обычной локальной работе source body — ignored repository-local Markdown:

```text
.codex/local/pr/<task>/body.md
```

`<task>` — короткий смысловой идентификатор work item, не PR number, SHA,
roadmap/stage number или имя пользователя.

`/.codex/local/` уже является ignored boundary репозитория. Перед использованием
source проверь фактом, что файл не tracked и действительно ignored.

Не вводи renderer/schema/template engine только ради PR body: Markdown достаточно.

### Recovery существующего PR

Если PR уже существует, а локальный source body отсутствует или его синхронность
не доказана:

1. прочитай **полный** опубликованный body;
2. восстанови локальный source из него без потери разделов;
3. перечитай source;
4. только затем вноси новую mutation.

Если local и remote расходятся и неизвестно, какая сторона новее, не
перезаписывай их автоматически: сначала сравни и примири различия.

В среде без локального checkout, где работа выполняется только через
авторизованный GitHub connector, published PR body является рабочим remote source;
каждая mutation всё равно требует полного read-back после записи.

## 3. Структура body

Содержательной структурой владеет [`pr-body.md`](pr-body.md). Body описывает
фактическое состояние work item, а не первоначальный замысел.

Не помещай туда transcript, полный журнал команд, временный roadmap или
provider-specific review infrastructure, которой в репозитории ещё нет.

## 4. Create и edit

Новый PR по умолчанию создаётся **Draft**, если пользователь или отдельный
контракт текущего work item явно не требует другого состояния.

Локальный путь через `gh`:

```bash
gh pr create --draft --base <base> --head <branch> --title <title> --body-file <путь>
gh pr edit <number> --title <title> --body-file <путь>
```

Флаги проверяй по фактической установленной версии `gh`.

В локальном workflow body передаётся из проверенного файла, а не собирается
одноразовым heredoc/stdin непосредственно в команду публикации.

В connector-only среде допустима эквивалентная mutation через GitHub connector,
если пользователь разрешил удалённую запись и после неё выполняется полный
read-back.

## 5. Повторное чтение опубликованного PR

После create/edit перечитай remote PR и проверь:

```text
repository
base
head
current head SHA
Draft/Ready state
title
body
```

Для локального workflow сравни remote body с локальным source с учётом только
нормализации переводов строк. Иные расхождения должны быть устранены.

## 6. Ready

Draft переводится в Ready только по явной команде пользователя либо когда это
следует из отдельного явного контракта work item. Push или зелёный CI сами по
себе Ready не разрешают.

## 7. Merge

```text
успешный push ≠ разрешение на merge
зелёный CI ≠ разрешение на merge
Ready PR ≠ разрешение на merge
```

Merge выполняется только после отдельной текущей явной команды пользователя на
конкретный PR.

Перед merge заново проверь:

- repository/base/head и точный текущий HEAD;
- обязательные checks/reviews **для текущего HEAD**;
- mergeability и отсутствие unresolved blockers;
- фактическую merge policy репозитория;
- что операция не обходит gates через admin/force.

Merge method не хардкодится в skill. Если CLI/API позволяет связать mutation с
expected head SHA, используй optimistic concurrency/lease, чтобы не слить
сдвинувшийся PR.

После merge прочитай фактический state `merged` и merge SHA. Exit code write
операции сам по себе не является доказательством remote postcondition.

## 8. Base advancement и конфликты

Если base продвинулась или возник конфликт:

1. установи merge-base и фактические изменения обеих сторон;
2. разрешай конфликт по смыслу, не применяя whole-file `ours`/`theirs` как
   универсальную стратегию;
3. generated/derived output пересоздавай из owner-source;
4. опубликованную task branch синхронизируй без force и без переписывания
   опубликованной истории; rebase допустим только для ещё не опубликованных
   commits;
5. проверь отсутствие unmerged paths/активной merge/rebase operation;
6. после нового HEAD считай прежние CI/review evidence stale и проверь состояние
   заново;
7. актуализируй PR body и выполни read-back.

## 9. Post-merge cleanup

После **подтверждённого** merge допустим cleanup собственного work item:

- удалить локальные временные артефакты;
- удалить собственную task branch только после доказанного сохранения результата;
- не удалять неизвестные чужие branches/artifacts;
- не переходить автоматически к принудительному удалению, если безопасное
  удаление отказывается.

## 10. Завершённое состояние work item

До отдельного разрешения на merge ожидаемое состояние обычной публикуемой задачи:

```text
commit опубликован, remote SHA подтверждён
существует ровно один PR для task branch
PR перечитан после последней mutation
body отражает фактическое текущее состояние
Draft/Ready state соответствует ожидаемому
merge не выполнен без отдельной текущей команды
```
