# SOLID guidelines

Apply these principles to new and changed production code. They are design checks, not a request to
split every existing class or introduce an interface for every helper. Preserve working behavior and
test coverage when moving responsibilities.

## Class and dependency boundaries

- **Single responsibility:** a class should have one clear reason to change. When a view model or
  control mixes presentation, file access, compilation, and network work, move the distinct
  operation behind a focused service or collaborator.
- **Open/closed:** add a new language, connection driver, or behavior through the existing host,
  registry, or strategy boundary where one exists. Avoid another type-name switch scattered through
  callers. Do not build a plugin framework for a single new case.
- **Liskov substitution:** an implementation must honor its interface's observable contract,
  including cancellation, error behavior, and ownership of resources. Do not make callers detect a
  concrete subtype to get correct behavior.
- **Interface segregation:** expose the small set of operations a consumer actually needs. Split
  broad interfaces when independent consumers depend on unrelated methods; do not add one-method
  interfaces for internal helpers without a real boundary.
- **Dependency inversion:** business and presentation decisions depend on contracts for external
  effects such as files, processes, network, and dialogs. Construct concrete implementations in the
  composition root; pass dependencies explicitly instead of using static service lookups or hidden
  `new` calls in the middle of a workflow.

## Practical checks for a change

1. Name the class that owns the new behavior and the reason it would change.
2. Follow the existing `src/core` → `src/languages` → `src/main` dependency direction; controls
   stay language-agnostic.
3. Check every caller before moving a method. Keep its validation, cancellation, error handling,
   and resource cleanup at the new boundary.
4. If an isolated unit test requires a real filesystem, network, compiler, or UI control, inspect
   the production dependency boundary. Use an integration or UI test for the real interaction.
5. Prefer the smallest existing interface or service that fits. Add an abstraction only when it
   separates a real responsibility or external dependency.

See [MVVM guidelines](MVVM_GUIDELINES.md) for UI ownership and
[unit testing guidelines](UNIT_TESTING_GUIDELINES.md) for test isolation.
