using System.Reflection;

namespace AiLocalPack.Core.Tests;

// Placeholder so the project is never empty (an empty run fails with exit code 8). Remove once real tests exist.
public sealed class SmokeTests
{
    [Fact]
    public void Project_under_test_loads()
    {
        // Arrange
        const string name = "AiLocalPack.Core";

        // Act
        var assembly = Assembly.Load(name);

        // Assert
        assembly.GetName().Name.Should().Be(name);
    }
}
