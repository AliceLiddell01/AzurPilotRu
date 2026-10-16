---
name: azurpilot-tui-harness
description: >-
  Проверяет визуальные изменения настоящего операторского TUI через постоянный ConPTY/xterm.js
  Harness: baseline, актуальная сборка, cells/assertions, PNG и самостоятельный осмотр. Применяй
  при визуально значимых изменениях TUI и проверке его ввода/resize; не заменяет physical Windows
  Terminal и не разрешает live mutation.
---

# Визуальная проверка production TUI

Подготовка, команды, профили, safety и evidence contract принадлежат
[`docs/testing/tui-harness.md`](../../../docs/testing/tui-harness.md).

1. До визуальной правки собери текущий EXE и сохрани baseline нужного состояния через поддерживаемый
   сценарий либо управляемую сессию. Выбери assertions и размер по изменяемому поведению.
2. Выполни одну логически целостную правку, собери App и открой **новую** ConPTY-сессию. Изменившийся
   EXE/DLL нельзя проверять старой сессией. Не заменяй production EXE тестовым renderer.
3. Получи новый PNG/cells/evidence. Проверь assertions по ячейкам и фактический результат цвета,
   синхронизации, font availability и размеров. `BLOCKED` не превращай в `PASS` ожиданием наугад.
4. Самостоятельно открой PNG доступным image-viewing инструментом. Запиши визуальный признак,
   путь/build identity и `PASS/FAIL/BLOCKED`. Если инструмент не принимает изображение, осмотр
   `BLOCKED`: существующий PNG или текст cells не доказывают vision.
5. Сопоставь before/fix/after и только затем переходи к следующей визуальной правке. Не затирай
   предыдущие checkpoints. После серии запусти `verify.ps1` и релевантные production tests.
6. Закрой принадлежащие сессии. Отчёт разделяет deterministic, browser-visual и physical evidence.

Default safe запрещает child processes и product mutations. `--providers read-only` разрешает
настоящее read-only inventory при закрытом production mutation guard. Live environment запускай
только при текущем явном разрешении exact target; подробные ограничения — в документе-владельце.
Не запускай дополнительные агенты при запрете пользователя. Не объявляй xterm PNG подтверждением
поведения физического Windows Terminal.
