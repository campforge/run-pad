# DotPad

Десктопный C# "scratchpad"/scripting IDE на Avalonia UI (net10.0) — в одном пространстве с LINQPad,
RoslynPad и NetPad. Редактирует и запускает `.rpad`-файлы: самодокументируемые Markdown-документы с
YAML front matter и одним исполняемым C#-блоком кода.

- Архитектура, команды сборки/тестов, стиль кода — `docs/ARCHITECTURE.md`.
- Известные баги, ограничения и план дальнейшей работы — `ANALYSIS.md` (разбор причин) и `TODO.md`
  (актуальный список пунктов).
- Полная спецификация формата `.rpad` — `docs/SpecificationRPad.md`.

Почему Avalonia UI, а не другие UI фреймворки? Я искал открытый и поддерживаемый сообществом UI с возможностью форкать и изменять иделать пул реквесты в кодовую базу.

Особенности С#
- Взят netcoredbg не просто так, на текущий помент это лучщий открытый отладчик .net, который поддерживается сообществом и samsung.
- 

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

## Известные проблемы

Если при открытии/работе с проектом видите `error CS0009` про метаданные какого-нибудь
`System.*.dll` (обычно `*.Native.dll`, например `System.IO.Compression.Native.dll`) — это известный,
пока не исправленный баг в резолюции референсов для IntelliSense на Windows, см. `TODO.md` пункт 44.
На саму компиляцию/запуск скрипта (`dotnet build`) это не влияет — баг только в редакторе.
