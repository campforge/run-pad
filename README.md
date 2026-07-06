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

### error CS0009
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

### Нет системных импортов
Можно всегда делать вот так:

```
System
System.Collections
System.Collections.Generic
System.Data
System.Diagnostics
System.IO
System.Linq
System.Linq.Expressions
System.Reflection
System.Text
System.Text.RegularExpressions
System.Threading
System.Transactions
System.Xml
System.Xml.Linq
System.Xml.XPath
```
### Ошбка вкладки EditorsTabItemControl

```
fail: DotPad.App[0]
      Unobserved task exception
      System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. As a result, the unobserved exception was rethrown by the finalizer thread. (The calling thread cannot access this object because a different thread owns it.)
       ---> System.InvalidOperationException: The calling thread cannot access this object because a different thread owns it.
         at Avalonia.Threading.Dispatcher.<VerifyAccess>g__ThrowVerifyAccess|17_0()
         at Avalonia.Threading.Dispatcher.VerifyAccess()
         at Avalonia.AvaloniaObject.VerifyAccess()
         at Avalonia.AvaloniaObject.GetValue[T](StyledProperty`1 property)
         at AvaloniaEdit.TextEditor.get_Document()
         at AvaloniaEdit.TextEditor.get_Text()
         at DotPad.UserControls.EditorsTabItemControl.<>c__DisplayClass44_5.<<SetupEditor>b__12>d.MoveNext() in C:\src\dot-pad\src\main\DotPad\UserControls\EditorTabItemControl.cs:line 697
         --- End of inner exception stack trace ---
fail: DotPad.App[0]
      Unobserved task exception
      System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. As a result, the unobserved exception was rethrown by the finalizer thread. (The calling thread cannot access this object because a different thread owns it.)
       ---> System.InvalidOperationException: The calling thread cannot access this object because a different thread owns it.
         at Avalonia.Threading.Dispatcher.<VerifyAccess>g__ThrowVerifyAccess|17_0()
         at Avalonia.Threading.Dispatcher.VerifyAccess()
         at Avalonia.AvaloniaObject.VerifyAccess()
         at Avalonia.AvaloniaObject.GetValue[T](StyledProperty`1 property)
         at AvaloniaEdit.TextEditor.get_Document()
         at AvaloniaEdit.TextEditor.get_Text()
         at DotPad.UserControls.EditorsTabItemControl.<>c__DisplayClass44_5.<<SetupEditor>b__12>d.MoveNext() in C:\src\dot-pad\src\main\DotPad\UserControls\EditorTabItemControl.cs:line 697
         --- End of inner exception stack trace ---
fail: DotPad.App[0]
      Unobserved task exception
      System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. As a result, the unobserved exception was rethrown by the finalizer thread. (The calling thread cannot access this object because a different thread owns it.)
       ---> System.InvalidOperationException: The calling thread cannot access this object because a different thread owns it.
         at Avalonia.Threading.Dispatcher.<VerifyAccess>g__ThrowVerifyAccess|17_0()
         at Avalonia.Threading.Dispatcher.VerifyAccess()
         at Avalonia.AvaloniaObject.VerifyAccess()
         at Avalonia.AvaloniaObject.GetValue[T](StyledProperty`1 property)
         at AvaloniaEdit.TextEditor.get_Document()
         at AvaloniaEdit.TextEditor.get_Text()
         at DotPad.UserControls.EditorsTabItemControl.<>c__DisplayClass44_5.<<SetupEditor>b__12>d.MoveNext() in C:\src\dot-pad\src\main\DotPad\UserControls\EditorTabItemControl.cs:line 697
         --- End of inner exception stack trace ---
```
