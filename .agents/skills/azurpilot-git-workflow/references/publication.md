# Публикация: ветка, staging, commit, push и remote postcondition

Этот файл владеет порядком действий обычной публикуемой задачи до подтверждённого
удалённого состояния. Контракт и границы задаёт `SKILL.md`.

## 1. Установить фактическое состояние checkout

Перед mutation установи repository identity и текущее состояние:

```bash
git rev-parse --show-toplevel
git remote -v
git rev-parse --abbrev-ref HEAD
git status --porcelain
git rev-parse HEAD
```

Перед публикацией обнови remote refs:

```bash
git fetch <remote> --prune
```

Если checkout относится не к ожидаемому repository либо нельзя однозначно
установить текущую ветку/upstream, не угадывай и не переключай ветки молча.

## 2. Ветка

### Новая самостоятельная задача

1. Установи фактический remote из upstream текущей ветки; если upstream ещё нет,
   используй фактическое состояние `git remote -v`, а не предположение `origin`.
2. Определи default/base branch из удалённого состояния:

   ```bash
   git ls-remote --symref <remote> HEAD
   ```

3. Обнови базу безопасно.
4. Создай смысловую task branch. Имя отражает работу; номера PR, SHA, roadmap,
   stage/phase и локальные данные пользователя в имя не попадают.
5. Не подмешивай неизвестные пользовательские изменения.

### Продолжение существующей задачи

Продолжай существующую task branch и связанный PR. Новая ветка не создаётся
только потому, что работа получила новый подшаг или новый prompt.

### Default branch

Прямая разработка в default branch запрещена repository contract. Если запрос
конфликтует с этим инвариантом, назови конфликт и используй task branch вместо
молчаливого нарушения.

## 3. Verification и staging

1. Выполни применимые проверки от их фактического владельца:
   `.codex/context/verification.md` и профильные owner-documents затронутой
   области. Этот skill не копирует verification matrix.
2. Просмотри итоговый diff целиком.
3. Добавь в индекс только пути текущего work item:

   ```bash
   git add -- <путь> [<путь> ...]
   ```

4. Перечитай staged diff:

   ```bash
   git diff --cached --stat
   git diff --cached
   git status --porcelain
   ```

Случайные staged/untracked изменения пользователя не добавляются, не коммитятся
и не удаляются. `.codex/local/` не попадает в индекс. Если принадлежность
изменения задаче неясна, это blocker для автоматического staging.

## 4. Commit

- Commit описывает фактическое изменение.
- Сохраняй устойчивый conventional style репозитория, если он существует.
- Conventional prefix/scope остаются точными техническими идентификаторами;
  человеческая формулировка следует языковой политике проекта.
- В сообщение не попадают текущий PR number, SHA, roadmap/stage number и разовый
  план.
- Несвязанные локальные изменения не включаются.

Пустой commit не используется как способ «закрыть» задачу. Он допустим только
если отдельный реальный workflow явно владеет таким контрактом.

## 5. Push

Публикуй текущую task branch явно в ожидаемый remote/ref. Голый `git push`
не является контрактом skill, потому что зависит от локальной конфигурации Git.

Перед push прочитай удалённую ссылку. Если remote ref существует и расходится с
локальным состоянием, сначала fetch и исследуй расхождение; чужие commits не
перезаписываются.

```bash
git ls-remote --refs <remote> refs/heads/<branch>

# первая публикация
git push --set-upstream <remote> refs/heads/<branch>:refs/heads/<branch>

# последующая публикация
git push <remote> refs/heads/<branch>:refs/heads/<branch>
```

Force-push не используется без отдельного явного основания.

## 6. Remote postcondition

Публикация подтверждена только при:

```text
expected local HEAD == actual SHA соответствующей remote branch
```

Читай фактическую удалённую ссылку:

```bash
git rev-parse HEAD
git ls-remote --refs <remote> refs/heads/<branch>
```

Remote-tracking ref сам по себе недостаточен: он может быть stale. Расхождение
означает, что ожидаемое состояние не опубликовано.

## 7. Неизвестный результат сетевой mutation

Если push или другая network mutation завершилась таймаутом/обрывом/неясным
ответом:

1. сначала прочитай фактический remote state;
2. не повторяй mutation вслепую;
3. повторяй запись только если доказано, что ожидаемое изменение не применилось;
4. не перезаписывай новые чужие commits.

То же правило действует для create/edit PR: неизвестный результат сначала
разрешается чтением remote state. Второй PR для того же base/head — дефект.

## 8. Запрещено без отдельного текущего разрешения

- force-push;
- rebase/amend уже опубликованной истории;
- удаление неизвестных remote branches;
- публикация другой ветки вместо текущего work item;
- публикация несвязанных локальных изменений;
- смена base/PR, закрытие PR или merge;
- обход обязательных checks/reviews административными или force-механизмами.

## 9. Завершённое состояние публикации

```text
staged diff соответствует задаче
commit создан в task branch
push выполнен явно в ожидаемый remote/ref
actual remote SHA равен expected local HEAD
```

После этого переходи к [`pr-lifecycle.md`](pr-lifecycle.md).
