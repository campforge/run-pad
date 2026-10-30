# MVVM guidelines

These rules apply to new and changed Invoklet UI code. Follow the existing behavior of nearby code
when refactoring an older screen; a contribution does not have to rewrite unrelated screens.

## Responsibilities

- **View (AXAML and code-behind):** creates controls, handles Avalonia-only events and focus,
  selection, layout, drag-and-drop, and other visual behavior. It binds to the view model. Keep
  file, network, compiler, and domain decisions out of the view.
- **View model:** owns screen state, validation, presentation decisions, and user commands. Expose
  observable properties and commands to the view. Do not depend on a concrete window or control.
- **Service or model:** owns file, network, process, compiler, persistence, and domain operations.
  Return results or errors that the view model can present. Keep services independent of Avalonia
  controls unless they are specifically UI infrastructure, such as `IWindowService`.

The view may translate a UI event into a view-model command or call when a binding cannot express
it cleanly. A code-behind handler should not become a second implementation of the same feature.

## UI thread rule

Do not run business or domain operations on the UI thread. This includes synchronous file and
network access, process calls, compilation, decompilation, parsing or hashing large inputs, and other
work that can block or grow with user data. A view or view model starts the operation through a
service; the service performs blocking or CPU-heavy work away from the UI thread. An `async` method
alone does not move synchronous work off that thread.

Use the UI thread only to read or update UI-bound state and controls. When background work finishes,
apply its result on the UI thread after checking cancellation and that the owning window or tab is
still current. Show progress and errors through view-model state instead of freezing the interface.

## Project conventions

- Use **ReactiveUI** (`ReactiveObject`, observable properties, and `ReactiveCommand`); do not add
  CommunityToolkit.Mvvm or another MVVM framework.
- Register services, windows, and view models in `src/main/Invoklet/App.axaml.cs`, the composition
  root. Supply dependencies through constructors. Do not look up services from a global provider
  inside views or view models.
- Put views in `Windows` or `UserControls` and their view models in the matching `Models` folder.
  Keep `src/controls` language-agnostic; language-specific behavior comes from language hosts.
- Use `ReactiveCommand.CreateFromTask` for asynchronous user actions. Await work, report failures,
  and observe command errors. Do not let an `async void` event handler propagate an exception.
- Give subscriptions, event handlers, cancellation tokens, and long-running work a clear owner.
  Unsubscribe, cancel, or dispose them when that owner closes. Prevent a late result from updating
  a different or closed screen.
- Keep shared presentation state in the owning view model or service. A view should not silently
  create a competing copy of settings, selection, or unsaved data.

## Review checklist

- Can the feature's state and command be understood without reading a view's event handlers?
- Does the view contain only behavior that needs an Avalonia control or visual coordinate?
- Are infrastructure operations behind a service boundary, with errors shown to the user?
- Can any operation on the UI thread block or grow with script, file, or network size?
- Are subscriptions and asynchronous work released or cancelled with their owner?
- Do UI tests cover the binding or event path, while unit tests cover isolated view-model behavior?

See [SOLID guidelines](SOLID_GUIDELINES.md) for class and dependency boundaries and
[unit testing guidelines](UNIT_TESTING_GUIDELINES.md) for new or changed unit tests.
