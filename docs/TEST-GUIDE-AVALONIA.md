# UI Testing Guidelines

## General principles

This guide defines a single style for writing UI tests for the project on **.NET 10**, **Avalonia 12**, and **Avalonia.Headless.XUnit**.

All UI tests must use the **official Avalonia.Headless.XUnit package**. Other UI testing approaches are allowed only after agreement.

The main goals of the tests:

* Verify user scenarios.
* Verify that the UI works correctly.
* Verify the interaction between View and ViewModel.
* Protect against regressions.
* Support safe refactoring.

---

# Stack

UI tests use the following stack:

* Avalonia.Headless.XUnit
* xUnit
* Avalonia 12
* .NET 10

All tests must run in headless mode without opening real windows.

---

# Test attributes

UI tests may use only:

```csharp
[AvaloniaFact]

[AvaloniaTheory]
```

Using the regular xUnit attributes is forbidden:

```csharp
[Fact]

[Theory]
```

Reason: `AvaloniaFact` and `AvaloniaTheory` automatically run tests on the Avalonia UI thread and correctly initialize the headless platform.

---

# What to test

## Required

* data display;
* bindings;
* button clicks;
* text input;
* element state changes;
* tab switching;
* element visibility;
* menus;
* dialogs;
* navigation;
* user input handling;
* hotkeys;
* drag & drop (if used);
* behavior after commands execute.

---

## Do not test

Do not write tests for:

* Avalonia's internal mechanisms;
* standard controls (`Button`, `TextBox`, `Grid`, etc.);
* external libraries;
* visual styling (unless snapshot testing is used);
* platform logic.

A test must verify the application's behavior, not the framework's.

---

# Test structure

Every test must contain three logical blocks with comments.

```csharp
// Arrange
// prepare data

// Act
// perform the action

// Assert
// verify the result
```

The `// Arrange`, `// Act`, and `// Assert` comments are mandatory even in the shortest tests.

---

# One test — one scenario

Each test must verify only one scenario.

If several independent actions need to be verified, write several separate tests.

---

# Test naming

Use the following pattern:

```text
Method_Should_ExpectedBehavior_When_Condition
```

For example:

```text
Save_Should_SaveDocument_When_DataIsValid

Login_Should_ShowError_When_PasswordIsInvalid

Delete_Should_RemoveItem_When_UserConfirms
```

---

# Working with the UI

Interact with the interface only through the public Avalonia.Headless.XUnit API.

Finding elements:

```csharp
window.FindControl<T>("ControlName");
```

Text input:

```csharp
window.KeyTextInput("Hello");
```

Keyboard:

```csharp
window.KeyPress(Key.Enter);
window.KeyRelease(Key.Enter);
```

Mouse:

```csharp
window.MouseDown(point, MouseButton.Left);
window.MouseUp(point, MouseButton.Left);
```

Focus:

```csharp
control.Focus();
```

Avoid changing control properties directly when the same action could be performed by the user.

---

# Assertions

Every test must contain at least one meaningful `Assert`.

Prefer specialized assertions:

* `Assert.True`
* `Assert.False`
* `Assert.Equal`
* `Assert.NotNull`
* `Assert.Null`
* `Assert.Contains`
* `Assert.ThrowsAsync`

---

# Asynchronous code

Use:

* `async Task`
* `await`

Do not use:

* `.Wait()`
* `.Result`
* `Task.Delay(...).Wait()`
* `Thread.Sleep()`

---

# Dependencies

All external dependencies must be mocked.

Recommended libraries:

* Moq
* NSubstitute

UI tests must not depend on:

* the network;
* a database;
* the file system;
* external APIs.

---

# Good practices

* Use Avalonia.Headless.XUnit for all UI tests.
* Always use `AvaloniaFact` or `AvaloniaTheory`.
* Follow the `Arrange → Act → Assert` structure.
* Always add the `// Arrange`, `// Act`, and `// Assert` comments.
* Verify one scenario per test.
* Use clear test names.
* Simulate real user actions through the headless API.
* Move shared setup into a helper, fixture, or factory.
* Use the application's public API.
* Keep tests independent and deterministic.

---

# Bad practices

It is forbidden to:

* use `[Fact]` or `[Theory]` for UI tests;
* use `Thread.Sleep()`;
* use `.Wait()` and `.Result`;
* combine several scenarios in one test;
* change UI state directly instead of through user interaction;
* access private class members;
* duplicate setup code between tests;
* make tests depend on each other;
* test Avalonia itself instead of the application's logic.

---

# Checklist

Before finishing a test, make sure that:

* `AvaloniaFact` or `AvaloniaTheory` is used;
* the `// Arrange`, `// Act`, and `// Assert` blocks are present;
* only one scenario is verified;
* the name follows the `Method_Should_ExpectedBehavior_When_Condition` pattern;
* there is no `Thread.Sleep`, `.Wait()`, or `.Result`;
* external dependencies are mocked;
* UI interaction goes through the Avalonia.Headless.XUnit API;
* there is at least one meaningful `Assert`;
* the test does not depend on other tests;
* the test can run in parallel with the others.
