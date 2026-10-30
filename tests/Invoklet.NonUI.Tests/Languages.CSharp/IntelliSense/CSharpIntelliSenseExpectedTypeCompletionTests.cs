using Xunit;

namespace Invoklet.NonUI.Tests;

// An argument of a call or constructor, and the value of a property in an object initializer, have a
// type the code already names: completion offers what fits it and leaves out the variables that don't.
public class CSharpIntelliSenseExpectedTypeCompletionTests
{
    private const string Declarations = """
        public enum Status { Active, Closed }
        public static class Outer
        {
            public enum Inner { Active }
            public static void Set(Inner value) { }
        }
        public class Address { }
        public class Customer
        {
            public Customer() { }
            public Customer(Address address) { }
            public Customer(string name, Address address) { }
            public Address Home { get; set; } = new();
            public Address? Previous;
            public string Name = "";
            public Status State;
            public int Age;
        }
        public class Premium : Customer { }
        public interface IShape { }
        public class Square : IShape { }
        public abstract class Blob { }
        public static class Helpers { }
        public static class Api
        {
            public static void Save(Customer customer) { }
            public static void Draw(IShape shape) { }
            public static void SetState(Status state) { }
            public static void Maybe(int? count) { }
            public static void Take(int count) { }
            public static void Many(params int[] counts) { }
            public static void Toggle(bool enabled) { }
            public static void Label(string text) { }
            public static void Echo<T>(T value) { }
            public static void Pick(Address address) { }
            public static void Pick(Status status, int count) { }
        }

        """;

    private const string Locals = """
        var customer = new Customer();
        var premium = new Premium();
        var address = new Address();
        var square = new Square();
        var name = "n";
        var total = 5;
        var status = Status.Active;

        """;

    // The caret is the '|' in the statement under test; declarations come after the statements.
    private static async Task<List<string>> NamesAsync(string statement, string locals = Locals)
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        var code = locals + statement + "\n" + Declarations;
        var position = code.IndexOf('|');
        code = code.Remove(position, 1);

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        return [.. result!.Items.Select(i => i.DisplayText)];
    }

    [Theory]
    [InlineData("Api.Save(cus|);")]
    [InlineData("Api.Save(|);")]
    [InlineData("Api.Save(cus|tomer);")]
    public async Task AMethodArgument_Should_OfferTheVariablesThatFit_AndNotTheOthers(string statement)
    {
        var names = await NamesAsync(statement);

        Assert.Contains("customer", names);
        Assert.Contains("premium", names); // a Premium is a Customer
        Assert.DoesNotContain("address", names);
        Assert.DoesNotContain("name", names);
        Assert.DoesNotContain("total", names);
        Assert.DoesNotContain("square", names);
    }

    [Fact]
    public async Task AMethodArgument_Should_OfferNewOfTheParameterType_AndNullForAReference()
    {
        var names = await NamesAsync("Api.Save(|);");

        Assert.Contains("new Customer", names);
        Assert.Contains("null", names);
        Assert.DoesNotContain("new Premium", names); // only the type asked for
        Assert.DoesNotContain("0", names);
    }

    [Fact]
    public async Task AfterNew_Should_OfferTheTypesThatConvertToTheParameter()
    {
        var names = await NamesAsync("Api.Save(new |);");

        Assert.Contains("Customer", names);
        Assert.Contains("Premium", names);
        Assert.DoesNotContain("Address", names);
        Assert.DoesNotContain("Square", names);
        Assert.DoesNotContain("string", names);
        Assert.DoesNotContain("customer", names); // a variable is not a type
        Assert.Contains("System", names); // a namespace may lead to one
    }

    [Fact]
    public async Task AfterNew_Should_OfferAnImplementationOfAnInterfaceParameter_ButNotAnAbstractClass()
    {
        var names = await NamesAsync("Api.Draw(new |);");

        Assert.Contains("Square", names);
        Assert.DoesNotContain("Customer", names);
        Assert.DoesNotContain("Blob", names);
        Assert.DoesNotContain("Helpers", names);
    }

    [Fact]
    public async Task AfterNew_Should_OfferAGenericTypeByItsDefinition()
    {
        var names = await NamesAsync("void Sort(System.Collections.Generic.IList<int> list) { }\nSort(new |);");

        Assert.Contains("List", names);
        Assert.DoesNotContain("Dictionary", names);
    }

    [Fact]
    public async Task AnInterfaceParameter_Should_OfferTheVariablesThatImplementIt()
    {
        var names = await NamesAsync("Api.Draw(|);");

        Assert.Contains("square", names);
        Assert.DoesNotContain("customer", names);
    }

    [Fact]
    public async Task ConstructorArguments_Should_FollowTheOverloadsThatFitTheArgumentsBefore()
    {
        var first = await NamesAsync("var made = new Customer(|);");
        Assert.Contains("address", first);
        Assert.Contains("name", first); // the first parameter of Customer(string, Address)
        Assert.DoesNotContain("total", first);
        Assert.DoesNotContain("customer", first);

        var second = await NamesAsync("var made = new Customer(name, |);");
        Assert.Contains("address", second);
        Assert.DoesNotContain("name", second);

        // No overload takes an Address first and more after it: nothing is known, so nothing is left out.
        var afterAddress = await NamesAsync("var made = new Customer(address, |);");
        Assert.Contains("total", afterAddress);
        Assert.Contains("name", afterAddress);
    }

    [Fact]
    public async Task OverloadedMethod_Should_OfferTheTypesOfEveryOverloadThatFits()
    {
        var first = await NamesAsync("Api.Pick(|);");
        Assert.Contains("address", first);
        Assert.Contains("status", first);
        Assert.DoesNotContain("name", first);

        var second = await NamesAsync("Api.Pick(status, |);");
        Assert.Contains("total", second);
        Assert.DoesNotContain("address", second);
    }

    [Fact]
    public async Task ANamedArgument_Should_UseItsOwnParameterType()
    {
        var names = await NamesAsync("var made = new Customer(address: |);");

        Assert.Contains("address", names);
        Assert.DoesNotContain("name", names);
    }

    [Fact]
    public async Task AnEnumParameter_Should_OfferItsMembersAndTheVariablesOfItsType()
    {
        var names = await NamesAsync("Api.SetState(|);");

        Assert.Contains("Status.Active", names);
        Assert.Contains("Status.Closed", names);
        Assert.Contains("status", names);
        Assert.DoesNotContain("total", names);
        Assert.DoesNotContain("null", names);
    }

    [Fact]
    public async Task ANestedEnumParameter_Should_OfferQualifiedMembers()
    {
        var names = await NamesAsync("Outer.Set(|);");

        Assert.Contains("Outer.Inner.Active", names);
    }

    [Fact]
    public async Task AParamsArgument_Should_OfferValuesOfTheElementType()
    {
        var names = await NamesAsync("Api.Many(1, |);");

        Assert.Contains("total", names);
        Assert.Contains("0", names);
        Assert.DoesNotContain("customer", names);
    }

    [Fact]
    public async Task ABoolArgument_Should_OfferBoolLiteralsOnly()
    {
        var names = await NamesAsync("Api.Toggle(|);");

        Assert.Contains("true", names);
        Assert.Contains("false", names);
        Assert.DoesNotContain("null", names);
        Assert.DoesNotContain("total", names);
    }

    [Fact]
    public async Task ANullableParameter_Should_OfferNullAndTheUnderlyingType()
    {
        var names = await NamesAsync("Api.Maybe(|);");

        Assert.Contains("null", names);
        Assert.Contains("total", names);
        Assert.Contains("0", names);
        Assert.DoesNotContain("name", names);
    }

    [Fact]
    public async Task APrimitiveParameter_Should_OfferALiteral_AndNoNull()
    {
        var number = await NamesAsync("Api.Take(|);");
        Assert.Contains("0", number);
        Assert.Contains("total", number);
        Assert.DoesNotContain("null", number);

        var text = await NamesAsync("Api.Label(|);");
        Assert.Contains("\"\"", text);
        Assert.Contains("name", text);
        Assert.Contains("null", text);
        Assert.DoesNotContain("total", text);
    }

    [Fact]
    public async Task AnInitializerValue_Should_OfferTheVariablesThatFitTheProperty()
    {
        var home = await NamesAsync("var made = new Customer { Home = | };");
        Assert.Contains("address", home);
        Assert.Contains("new Address", home);
        Assert.DoesNotContain("name", home);
        Assert.DoesNotContain("Age", home); // a member name is not a value

        var after = await NamesAsync("var made = new Customer { Age = 1, Name = n| };");
        Assert.Contains("name", after);
        Assert.Contains("\"\"", after);
        Assert.DoesNotContain("total", after);
    }

    [Fact]
    public async Task AnInitializerFieldValue_Should_OfferNullForANullableField_AndEnumMembers()
    {
        var previous = await NamesAsync("var made = new Customer { Previous = | };");
        Assert.Contains("null", previous);
        Assert.Contains("address", previous);

        var state = await NamesAsync("var made = new Customer { State = | };");
        Assert.Contains("Status.Active", state);
        Assert.Contains("status", state);
        Assert.DoesNotContain("null", state);

        var age = await NamesAsync("var made = new Customer { Age = | };");
        Assert.Contains("total", age);
        Assert.DoesNotContain("null", age);
    }

    [Fact]
    public async Task AnInitializerValue_AfterNew_Should_OfferTheTypesThatFitTheProperty()
    {
        var names = await NamesAsync("var made = new Customer { Home = new | };");

        Assert.Contains("Address", names);
        Assert.DoesNotContain("Customer", names);
    }

    [Fact]
    public async Task AMemberNamePosition_InAnInitializer_Should_StillOfferTheMembers()
    {
        var names = await NamesAsync("var made = new Customer { Age = 1, | };");

        Assert.Contains("Name", names);
        Assert.Contains("Home", names);
        Assert.DoesNotContain("Age", names);
    }

    [Fact]
    public async Task AnObjectParameter_Should_KeepEveryVariable()
    {
        var names = await NamesAsync("Console.WriteLine(|);");

        Assert.Contains("customer", names);
        Assert.Contains("total", names);
        Assert.Contains("name", names);
    }

    [Fact]
    public async Task AGenericParameter_Should_NotNarrowTheList()
    {
        var names = await NamesAsync("Api.Echo(|);");

        Assert.Contains("customer", names);
        Assert.Contains("total", names);
    }

    [Fact]
    public async Task MemberAccess_InAnArgument_Should_StayMemberCompletion()
    {
        var names = await NamesAsync("Api.Save(customer.|);");

        Assert.Contains("Name", names);
        Assert.Contains("GetHashCode", names);
        Assert.DoesNotContain("total", names);
    }

    [Fact]
    public async Task ALongerArgumentExpression_Should_KeepTheOrdinaryList()
    {
        var names = await NamesAsync("Api.Take(total + |);");

        Assert.Contains("total", names);
        Assert.Contains("name", names);
    }
}
