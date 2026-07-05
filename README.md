# DotPad
сейчас использую
avalonia ui

Дебаггер:
https://github.com/Samsung/netcoredbg

Где подглядеть:
https://github.com/tareqimbasher/NetPad
https://github.com/roslynpad/roslynpad

DI
https://docs.avaloniaui.net/docs/guides/implementation-guides/how-to-implement-dependency-injection

## ToDo 
* файл-он же проект - done
* прикрутить отладчик - done 
* прикрутить nuget - done
* разобраться со стилем - done
* добавить настройки - done

# Особенности

## Структура файла

За основу взят md формат, это позволят файлу с скриптом быть самодокументируемым


## ошибки

###
(0,0): error CS0009: Не удалось открыть файл метаданных "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.1\System.IO.Compression.Native.dll" — Образ среды предустановки не содержит управляемые метаданные.

```
    ---
    version: 1
    language: csharp
    targetFramework: net10.0
    kind: Statements
    usings:
    - System
    ---

    # Query 3

    ```csharp run
    var i = "test";
    i.Dump();
    ```

```