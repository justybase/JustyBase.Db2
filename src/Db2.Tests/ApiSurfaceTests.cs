using System.Reflection;
using IBM.Data.Db2;
using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>
/// Offline public-API snapshots for the two shipped assemblies. Each snapshot is committed under
/// <c>api/</c>; regenerate with API_SURFACE_UPDATE=1 only after a reviewed API change.
/// The snapshot includes public and protected members, because protected members are extension
/// points for subclassing consumers.
/// </summary>
public sealed class ApiSurfaceTests
{
    private const string NativeSnapshot = "JustyBase.Db2.txt";
    private const string CompatSnapshot = "JustyBase.Db2.IBMCompat.txt";

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void NativePublicApiSurfaceIsFrozen() =>
        AssertSurfaceIsFrozen(typeof(Db2Config).Assembly, NativeSnapshot);

    [Fact]
    public void IbmCompatPublicApiSurfaceIsFrozen() =>
        AssertSurfaceIsFrozen(typeof(DB2Connection).Assembly, CompatSnapshot);

    private static void AssertSurfaceIsFrozen(Assembly assembly, string snapshotName)
    {
        var surfacePath = Path.Combine(RepoRoot, "api", snapshotName);
        var actual = BuildSurface(assembly);
        if (Environment.GetEnvironmentVariable("API_SURFACE_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(surfacePath)!);
            File.WriteAllText(surfacePath, actual);
            return;
        }

        Assert.True(File.Exists(surfacePath),
            $"API surface snapshot missing at {surfacePath}; regenerate with API_SURFACE_UPDATE=1");
        var expected = File.ReadAllText(surfacePath);
        if (expected == actual) return;

        var expectedLines = expected.Split('\n').Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
        var actualLines = actual.Split('\n').Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
        var removed = expectedLines.Except(actualLines).Take(40);
        var added = actualLines.Except(expectedLines).Take(40);
        Assert.Fail(
            $"Public API surface of {assembly.GetName().Name} changed. Review the change, then regenerate with API_SURFACE_UPDATE=1.\n"
            + $"Removed:\n{string.Join('\n', removed)}\nAdded:\n{string.Join('\n', added)}");
    }

    private static string BuildSurface(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"type {TypeKind(type)} {type.FullName}");
            foreach (var member in SurfaceMembers(type)) lines.Add($"  {member}");
        }
        return string.Join('\n', lines) + "\n";
    }

    private static IEnumerable<string> SurfaceMembers(Type type)
    {
        var members = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var constructor in type.GetConstructors(flags).Where(IsSurface).OrderBy(Render, StringComparer.Ordinal))
            members.Add(Render(constructor));
        foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName && IsSurface(m)).OrderBy(Render, StringComparer.Ordinal))
            members.Add(Render(method));
        foreach (var property in type.GetProperties(flags).Where(IsSurface).OrderBy(p => p.Name, StringComparer.Ordinal))
            members.Add(Render(property));
        foreach (var field in type.GetFields(flags).Where(f => !f.IsSpecialName && IsSurface(f)).OrderBy(f => f.Name, StringComparer.Ordinal))
            members.Add(Render(field));
        foreach (var @event in type.GetEvents(flags).Where(IsSurface).OrderBy(e => e.Name, StringComparer.Ordinal))
            members.Add(Render(@event));
        return members;
    }

    // Public and protected (including protected internal) members form the subclassing contract.
    private static bool IsSurface(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
    private static bool IsSurface(FieldInfo field) => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
    private static bool IsSurface(EventInfo @event) =>
        (@event.AddMethod is not null && IsSurface(@event.AddMethod)) || (@event.RemoveMethod is not null && IsSurface(@event.RemoveMethod));
    private static bool IsSurface(PropertyInfo property) =>
        (property.GetMethod is not null && IsSurface(property.GetMethod)) || (property.SetMethod is not null && IsSurface(property.SetMethod));

    private static string TypeKind(Type type) =>
        type.IsEnum ? "enum"
        : type.IsValueType ? "struct"
        : type.IsInterface ? "interface"
        : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
        : "class";

    private static string Access(MethodBase method) =>
        method.IsPublic ? "" : method.IsFamilyOrAssembly ? "protected internal " : "protected ";

    private static string Render(MethodBase method)
    {
        var name = method.IsConstructor ? ".ctor" : method.Name;
        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{Render(p.ParameterType)} {p.Name}"));
        var prefix = Access(method) + (method.IsStatic ? "static " : "");
        var returnType = method is MethodInfo info ? $"{Render(info.ReturnType)} " : "";
        var arity = method.IsGenericMethodDefinition ? $"`{method.GetGenericArguments().Length}" : "";
        return $"{prefix}{returnType}{name}{arity}({parameters})";
    }

    private static string Render(PropertyInfo property)
    {
        var getter = property.GetMethod is { } get && IsSurface(get) ? "get;" : "";
        var setter = property.SetMethod is { } set && IsSurface(set) ? (IsInitOnly(set) ? "init;" : "set;") : "";
        return $"{Render(property.PropertyType)} {property.Name} {{ {getter}{setter} }}";
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string Render(FieldInfo field) => $"{(field.IsStatic ? "static " : "")}{Render(field.FieldType)} {field.Name}";
    private static string Render(EventInfo @event) => $"event {Render(@event.EventHandlerType!)} {@event.Name}";

    private static string Render(Type type)
    {
        if (type.IsByRef) return $"{Render(type.GetElementType()!)}&";
        if (type.IsArray) return $"{Render(type.GetElementType()!)}[]";
        if (type.IsGenericParameter) return type.Name;
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var definition = type.GetGenericTypeDefinition();
        var name = (definition.FullName ?? definition.Name);
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        var arguments = string.Join(", ", type.GetGenericArguments().Select(Render));
        return $"{name}<{arguments}>";
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Db2.Driver", "Db2.Driver.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Cannot locate the JustyBase.Db2 repository root from " + AppContext.BaseDirectory);
    }
}
