# Unit Testing Guidelines for .NET

**Status:** Mandatory team standard
**Audience:** C#/.NET developers and code reviewers

These guidelines apply when adding or changing Invoklet unit tests. UI, integration, and end-to-end
tests have separate scopes. Existing upstream test suites in the AvaloniaEdit and MarkView.Avalonia
forks retain their own conventions. This standard does not require rewriting unchanged tests.

## 1. Purpose and scope

A unit test verifies the **public contract of one concrete production class**, the **System Under Test (SUT)**, in isolation. The contract includes returned values, observable state changes, exceptions, and required interactions with dependencies.

- Test public methods through a **real SUT instance** in every test.
- Do not mock or partially mock the SUT (`Mock<SutClass>`, `CallBase`, or interception of SUT methods).
- Do not test private members directly or use reflection to access them. Cover their behavior through public methods.
- Do not test dependency implementations, databases, external services, compilers, operating systems, network calls, file systems, or third-party library behavior in unit tests.
- Use **integration tests** to verify real component/infrastructure interactions and **end-to-end tests** to verify complete workflows, as needed.

Simple DTOs, enums, immutable values, and domain data objects may be created directly as test data. Calls to the SUT's own methods execute normally; **only its injected dependencies are mocked**.

## 2. Design requirements: SOLID

The SUT must follow SOLID principles and have clear responsibilities and explicit dependencies. Where the design prevents isolated testing, **refactor production code before writing the tests**.

Common design problems include hidden service construction, direct infrastructure access, mixed responsibilities, and tightly coupled concrete dependencies. Introduce focused abstractions at dependency boundaries; **do not create interfaces for every helper class by default**.

Do not work around design problems by accessing private members, changing access modifiers solely for tests, partially mocking the SUT, or using real infrastructure.

## 3. Dependencies: strict allowlist

| Library | Permitted use |
| --- | --- |
| **xUnit** | Test framework and all assertions (`Assert`) |
| **Moq** | All dependency mocks, setups, and interaction verification (`Verify`) |
| **AutoFixture** | **Optional and recommended** for generating test data **inside fixture classes only** |

Standard .NET libraries and essential test SDK, runner, and coverage packages are allowed.

**Forbidden:** FluentAssertions, Shouldly, NSubstitute, FakeItEasy, Bogus, and any other third-party assertion, mocking, data-generation, or testing helper library without an explicit change to this standard. Do not introduce new packages without approval.

When using AutoFixture, prefer **explicit values for contract-critical fields**; generated data must not make the scenario ambiguous or nondeterministic. AutoFixture must not create or mock the SUT or replace Moq.

## 4. Dependency isolation

**Every injected dependency of the SUT must be replaced with a Moq mock.** Do not instantiate its real implementation, even when it seems lightweight. No production database, HTTP/gRPC client, broker, file access, or process execution is permitted in a unit test.

- Configure dependency responses with Moq `Setup`, `Returns`, `Throws`, and their async equivalents.
- Verify only **contract-relevant** interactions, such as a required save, notification, argument, or absence of a forbidden side effect.
- Avoid verifying incidental internal call sequences.
- Control clock, randomness, and other nondeterministic inputs through injected abstractions or explicitly supplied values.

## 5. Fixture contract

For each SUT, create a dedicated pair:

```text
src/Orders/OrderService.cs
tests/Orders/OrderServiceTests.cs
tests/Orders/OrderServiceFixture.cs
```

The **fixture** owns:

- All DI mocks and their `Setup` configuration.
- All test-data construction, with named factory methods and optional AutoFixture use.
- A `CreateSut()` method that returns a **new real SUT** wired to those mocks.

The **test** owns:

- A **fresh fixture instance per test** (`new OrderServiceFixture()`).
- Selection and execution of the public SUT method.
- Every `Assert`, `Verify`, and `VerifyNoOtherCalls` call, directly inside the test method's Assert section.

Never put assertions or verifications in the fixture or hidden assertion helpers. The fixture must not execute the method under test. Do not share mutable fixtures, mocks, or test data across tests; do not use `IClassFixture<T>` or `ICollectionFixture<T>` to share such state.

## 6. Test-writing conventions

- Name tests `Method_Scenario_ExpectedResult`.
- Use explicit **Arrange / Act / Assert** sections.
- One test covers one meaningful behavior; multiple related assertions are allowed.
- Use `[Fact]` for individual cases and `[Theory]` for variations of the same behavior.
- Cover success, failure, and boundary scenarios according to the public contract.
- Place all Moq `Verify` calls in **Assert**, not in fixtures.
- `await` async operations; avoid `.Wait()`, `.Result`, and arbitrary `Thread.Sleep`.
- Keep tests independent, deterministic, readable, and fast.
- Assert expected results explicitly; never duplicate the production algorithm to calculate expected values.

## 7. Example

The example assumes `Order`, `OrderStatus`, `IOrderRepository`, and `INotificationService` are existing production types.

**Production class** — `OrderService.cs`:

```csharp
public sealed class OrderService(
    IOrderRepository repository,
    INotificationService notifications)
{
    public async Task CompleteAsync(long id, CancellationToken ct)
    {
        var order = await repository.GetAsync(id, ct)
            ?? throw new InvalidOperationException("Order not found");

        order.Complete();
        await repository.SaveAsync(order, ct);
        await notifications.SendCompletedAsync(id, ct);
    }
}
```

**Fixture** — `OrderServiceFixture.cs`:

```csharp
public sealed class OrderServiceFixture
{
    public Mock<IOrderRepository> Repository { get; } = new();
    public Mock<INotificationService> Notifications { get; } = new();

    public Order CreateOrder(long id = 42) => new(id);

    public void SetupOrder(Order order) => Repository
        .Setup(x => x.GetAsync(order.Id, It.IsAny<CancellationToken>()))
        .ReturnsAsync(order);

    public OrderService CreateSut() =>
        new(Repository.Object, Notifications.Object);
}
```

**Unit test** — `OrderServiceTests.cs`:

```csharp
public sealed class OrderServiceTests
{
    [Fact]
    public async Task CompleteAsync_ExistingOrder_SavesAndNotifies()
    {
        // Arrange
        var fixture = new OrderServiceFixture();
        var order = fixture.CreateOrder();
        fixture.SetupOrder(order);
        var sut = fixture.CreateSut();

        // Act
        await sut.CompleteAsync(order.Id, CancellationToken.None);

        // Assert
        Assert.Equal(OrderStatus.Completed, order.Status);
        fixture.Repository.Verify(
            x => x.SaveAsync(order, It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.Notifications.Verify(
            x => x.SendCompletedAsync(order.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
```

The fixture arranges dependencies and data; the real SUT performs the action; the test makes all assertions and verifications.

## 8. Code review checklist

- [ ] One concrete SUT and its public contract are tested.
- [ ] Each test uses a real SUT; no partial SUT mocks or private-member access.
- [ ] SOLID problems are corrected in production code instead of bypassed.
- [ ] **All** injected dependencies are Moq mocks; no real infrastructure is used.
- [ ] Every test creates its own fixture; all DI setup and test-data creation live there.
- [ ] All `Assert`, `Verify`, and `VerifyNoOtherCalls` remain directly in test methods.
- [ ] Tests follow AAA, cover meaningful cases, and are deterministic.
- [ ] Only approved libraries are used; AutoFixture is optional and limited to fixture-based data generation.
- [ ] Tests compile and pass, and fail when the public contract is broken.

**Rule of thumb:** The **fixture prepares**; the **real SUT acts**; the **test asserts and verifies**.
