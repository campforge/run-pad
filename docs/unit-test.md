To design and write C# unit tests with xUnit correctly, you need to follow clear standards for design, naming, and code isolation. A unit test should verify one isolated logical scenario in the smallest unit of code (usually a class method), without touching external dependencies such as databases or third-party APIs.
------------------------------
## 1. Architecture and solution structure

* Separate projects: tests always live in a separate project (for example, MyProject.Tests) so that test libraries do not end up in the production build.
* Mirrored structure: the folder structure of the test project must fully mirror the structure of the main project. If the class under test is in MyProject/Services/OrderService.cs, the test class should be in MyProject.Tests/Services/OrderServiceTests.cs.
* One class — one test class: for each production class, create one test class with the Tests suffix.

------------------------------
## 2. Naming rules
The name of a test method should serve as documentation. It must clearly state what is being tested, under which conditions, and what result is expected.
Recommended pattern: [MethodUnderTest]_[Scenario/Conditions]_[ExpectedResult]

* Bad: TestCalculator, CheckUser.
* Good:
   * CalculateTotal_ValidItems_ReturnsCorrectSum
   * Withdraw_InsufficientFunds_ThrowsInvalidOperationException
   * GetUser_WhenUserDoesNotExist_ReturnsNull

------------------------------
## 3. Structuring the test body (the AAA pattern)
Every unit test should be visually and logically split into three blocks separated by a blank line.

```csharp
[Fact]
public void Deposit_ValidAmount_UpdatesBalance()
{
    // Arrange
    // Create the object, configure dependencies and the initial state
    var account = new BankAccount(initialBalance: 100);
    var depositAmount = 50;

    // Act
    // Call ONE method under test
    account.Deposit(depositAmount);

    // Assert
    // Check that the system reached the expected state
    Assert.Equal(150, account.Balance);
}
```

------------------------------
## 4. What you SHOULD test

* Business logic: any algorithms, calculations, formulas, and data transformation rules.
* Boundary values: edge states (overflow, empty strings string.Empty, null, negative numbers, minimum and maximum list elements).
* Error handling: validation of input parameters. If a method must throw an exception on invalid data, this must be verified with Assert.Throws.
* Code branches (code coverage): every branch of if/else, switch, and loops must be covered by tests.

------------------------------
## 5. What you should NOT test

* Third-party libraries and frameworks: do not test whether Entity Framework, HttpClient, or the standard List.Add() method works correctly. Trust their authors.
* Auto-properties: plain getters and setters such as public string Name { get; set; } are pointless to test; they contain no logic.
* Configuration files: reading appsettings.json is verified during integration testing, not by unit tests.
* Direct interaction with the outside world: databases, the file system, network requests, the system clock. All of these dependencies must be isolated.

------------------------------
## 6. Isolation and dependencies (DIP & mocking)
If the class under test depends on other services, they must not be called directly. Instead, the class should accept interfaces through its constructor (Dependency Inversion Principle), and in the test those interfaces are replaced with mocks (using libraries such as Moq).

```csharp
[Fact]
public async Task OrderProcess_SufficientStock_SendsNotification()
{
    // Arrange
    var warehouseMock = new Mock<IWarehouseService>();
    var notifierMock = new Mock<INotificationService>();

    // Configure the mock's behavior: the warehouse reports the item is in stock
    warehouseMock.Setup(w => w.HasStock(It.IsAny<int>())).Returns(true);

    var orderService = new OrderService(warehouseMock.Object, notifierMock.Object);

    // Act
    await orderService.ProcessOrderAsync(orderId: 42);

    // Assert
    // Check that the notification method was called exactly once
    notifierMock.Verify(n => n.SendEmailAsync(It.IsAny<string>(), "Order Processed"), Times.Once);
}
```

------------------------------
## 7. Test lifecycle in xUnit
Unlike other frameworks (NUnit, MSTest), xUnit has no [SetUp] and [TearDown] attributes. Isolation works at the level of class instantiation:

* Class constructor: runs before each test. Shared objects are initialized here (Arrange).
* The IDisposable interface: the Dispose() method runs after each test. Resources are cleaned up here.
* The IClassFixture<T> and ICollectionFixture<T> interfaces: used when you need one shared context for a group of tests (for example, spinning up a database in a Docker container for integration tests).

------------------------------
## 8. Checklist for a good unit test

   1. Fast: tests should run in milliseconds.
   2. Deterministic: a test either always passes or always fails. The result must not depend on execution order, the current time, or the weather.
   3. Isolated: tests must not affect each other. Do not use shared static variables without resetting their state.
   4. Readable: the test code should be simpler and clearer than the code under test.


------------------------------
## 9. Libraries and frameworks

### Allowed
- XUnit
- XUnit.Assertion
- Moq

### Strictly forbidden
- NUnit
- FluentAssertions
- NSubstitute

### Rationale

This rule has a very solid practical basis. Architects of large systems and experienced engineers often deliberately avoid these libraries in production. They are usually promoted for their attractive syntax, while their hidden long-term problems are overlooked.
Here is why these tools cause problems on real projects:
#### FluentAssertions problems

* Performance degradation: under the hood the library makes heavy use of reflection to parse expressions and deep-compare objects. In large projects running thousands of tests, FluentAssertions significantly inflates build time and CI/CD pipeline run time.
* Increased memory allocations: because chains of "fluent" objects are created for every assertion, extra memory is allocated on the heap, which leads to frequent garbage collection (GC) during test runs.
* Implicit comparison behavior: the Should().BeEquivalentTo() method often masks the real structure of objects. Tests may pass where data types do not match, or fail with extremely complex error logs that are hard to understand.
* A more complex codebase: the standard xUnit Assert is concise and understandable to any C# developer out of the box. FluentAssertions forces the team to learn yet another surrogate DSL.

#### NSubstitute problems

* Hidden bugs due to extension methods: NSubstitute is built entirely on extension methods (for example, .Returns()). If you accidentally call a method on null or get the compiler context wrong, the test may behave unpredictably and produce false positives.
* Thread-safety issues: because of its particular call-interception mechanism (via a static context at runtime), NSubstitute often misbehaves when tests run in parallel, which leads to flaky tests.
* Type safety: unlike the explicit Mock<T> syntax, where you clearly see the object being configured, NSubstitute makes the code too "magical", which complicates refactoring with the IDE itself (for example, when renaming interface methods).

Plain xUnit and strict Moq force you to write explicit, predictable, and fast test code that will not break when the .NET version is upgraded. This position reflects a mature engineering approach.
Deep object comparisons, for which FluentAssertions is often mistakenly pulled in, can be expressed concisely with standard xUnit facilities instead.
