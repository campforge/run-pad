# DotPad

Десктопный C# "scratchpad"/scripting IDE на Avalonia UI (net10.0) — в одном пространстве с LINQPad,
RoslynPad и NetPad. Редактирует и запускает `.rpad`-файлы: самодокументируемые Markdown-документы с
YAML front matter и одним исполняемым C#-блоком кода.

- Архитектура, команды сборки/тестов, стиль кода — `docs/ARCHITECTURE.md`.
- Известные баги, ограничения и план дальнейшей работы — `ANALYSIS.md` (разбор причин) и `TODO.md`
  (актуальный список пунктов).
- Полная спецификация формата `.rpad` — `docs/SpecificationRPad.md`.

Почему Avalonia UI, а не другие UI-фреймворки? Я искал открытый и поддерживаемый сообществом UI с возможностью форкать, изменять и делать пул-реквесты в кодовую базу.

Особенности C#
- Взят netcoredbg не просто так: на текущий момент это лучший открытый отладчик .NET, который поддерживается сообществом и Samsung.

## Вдохновение и используемые компоненты

- Отладчик: [netcoredbg](https://github.com/Samsung/netcoredbg) (через DAP).
- Референсы при проектировании: [NetPad](https://github.com/tareqimbasher/NetPad),
  [RoslynPad](https://github.com/roslynpad/roslynpad).
- DI: [Avalonia DI guide](https://docs.avaloniaui.net/docs/guides/implementation-guides/how-to-implement-dependency-injection)
  (`Microsoft.Extensions.DependencyInjection`, композиция — `App.axaml.cs`).

## Формат файла

За основу взят Markdown-формат — это позволяет файлу со скриптом быть самодокументируемым: YAML
front matter (версия, язык, target SDK, kind, пакеты, референсы, usings) плюс один блок ```` ```csharp run ````
с самим кодом, остальной Markdown вокруг сохраняется как есть.

## DotPad.Cli — запуск `.rpad` из консоли без GUI

`src/main/DotPad.Cli` — лёгкий консольный раннер: компилирует и выполняет run-блок `.rpad`-файла
напрямую, без Avalonia и без отладчика (тот же «быстрый путь», что и кнопка Run в самом DotPad, без
DAP/`netcoredbg` вокруг). Годится для CI/автоматизации или когда полноценный редактор не нужен.
Поддерживает и `language: csharp`, и `language: vb` — front matter (`packages`/`references`/
`namespaces`/`kind`) учитывается так же, как в GUI.

Запуск прямо из исходников:

```bash
dotnet run --project src/main/DotPad.Cli -- path/to/script.rpad
```

Либо сборка и запуск отдельно (сборка называется `dotpad.dll`):

```bash
dotnet build src/main/DotPad.Cli -c Release
dotnet src/main/DotPad.Cli/bin/Release/net10.0/dotpad.dll path/to/script.rpad
```

(Рядом лежит и нативный apphost `dotpad`/`dotpad.exe` — запускается без `dotnet` в префиксе, но
только если .NET установлен туда, откуда apphost умеет его находить; при нестандартной установке
(например, через Homebrew/Linuxbrew) он не найдёт рантайм — тогда используйте вариант с `dotnet
... .dll` выше, он работает всегда.)

Обычный вывод скрипта и `Console.Error`/ошибки компиляции идут в stdout/stderr соответственно;
`.Dump()` рендерится прямо в терминале через [Spectre.Console](https://spectreconsole.net/) —
таблицы, цвет по типу значения (строки/числа/null/enum/дата и т.д.), вложенные коллекции/объекты
превращаются во вложенные таблицы. Цвет включается автоматически только когда вывод реально идёт в
терминал (не в файл/пайп). Коды выхода: `0` — успех и ничего не попало в stderr; `1` — ошибка
(неверные аргументы, файл не найден/не парсится как `.rpad`, нет установленного SDK, ошибка
компиляции, либо сам скрипт написал что-то в stderr во время выполнения); `130` — прервано Ctrl+C.

## Известные проблемы

Если при открытии/работе с проектом видите `error CS0009` про метаданные какого-нибудь
`System.*.dll` (обычно `*.Native.dll`, например `System.IO.Compression.Native.dll`) — это известный,
пока не исправленный баг в резолюции референсов для IntelliSense на Windows, см. `TODO.md` пункт 44.
На саму компиляцию/запуск скрипта (`dotnet build`) это не влияет — баг только в редакторе.
