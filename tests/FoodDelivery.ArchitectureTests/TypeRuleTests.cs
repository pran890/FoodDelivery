using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetArchTest.Rules;

namespace FoodDelivery.ArchitectureTests;

// Rules about TYPES, checked by NetArchTest on the compiled assemblies.
public class TypeRuleTests
{
    private static readonly Assembly _kitchen = Assembly.Load("FoodDelivery.Sandbox.Kitchen");
    private static readonly Assembly _identity = Assembly.Load("FoodDelivery.Identity");
    private static readonly Assembly _serviceDefaults = Assembly.Load("FoodDelivery.ServiceDefaults");

    [Fact]
    public void Health_checks_are_sealed_and_named_consistently()
    {
        var healthChecks = Types.InAssemblies([_kitchen, _identity]).That().ImplementInterface(typeof(IHealthCheck));

        Verify(healthChecks, healthChecks.Should().BeSealed().And().HaveNameEndingWith("HealthCheck", StringComparison.Ordinal));
    }

    [Fact]
    public void Service_defaults_knows_no_database_drivers()
    {
        // Not NetArchTest: it silently skips every type whose namespace starts with "Microsoft" or
        // "System", and our Extensions class lives in Microsoft.Extensions.Hosting.
        var references = _serviceDefaults.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.NotEmpty(references);
        Assert.DoesNotContain("Npgsql", references);
        Assert.DoesNotContain("StackExchange.Redis", references);
    }

    [Fact]
    public void Chaos_does_no_io()
    {
        var chaos = Types.InAssembly(_kitchen).That().HaveNameStartingWith("Chaos", StringComparison.Ordinal);

        Verify(chaos, chaos.ShouldNot().HaveDependencyOnAny("Npgsql", "StackExchange.Redis", "System.Net.Http"));
    }

    /// <summary>
    /// A rule that selects nothing passes without checking anything, so first make sure it selects something.
    /// </summary>
    private static void Verify(PredicateList selection, ConditionList rule)
    {
        Assert.NotEmpty(selection.GetTypes());

        var result = rule.GetResult();
        Assert.True(result.IsSuccessful, "Rule broken by: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
