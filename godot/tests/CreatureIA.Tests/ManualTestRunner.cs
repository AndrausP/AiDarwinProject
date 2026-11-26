using System.Reflection;
using NUnit.Framework;

namespace CreatureIA.Tests;

/// <summary>
/// Minimal fallback runner for restricted environments where VSTest cannot start its testhost.
/// Normal development must continue to use <c>dotnet test</c>.
/// </summary>
public static class ManualTestRunner
{
    public static int Main()
    {
        var failures = new List<string>();
        var testTypes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => type.GetCustomAttribute<TestFixtureAttribute>() is not null);

        foreach (var type in testTypes)
        {
            var instance = Activator.CreateInstance(type);
            foreach (var method in type.GetMethods().Where(method => method.GetCustomAttribute<TestAttribute>() is not null))
            {
                try
                {
                    method.Invoke(instance, null);
                    Console.WriteLine($"PASS {type.Name}.{method.Name}");
                }
                catch (TargetInvocationException exception)
                {
                    failures.Add($"FAIL {type.Name}.{method.Name}: {exception.InnerException?.Message ?? exception.Message}");
                }
            }
        }

        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Manual test run: {failures.Count} failure(s).");
        return failures.Count == 0 ? 0 : 1;
    }
}
