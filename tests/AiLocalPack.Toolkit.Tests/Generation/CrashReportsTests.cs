using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Toolkit.Tests.Generation;

/// <summary>What a Windows Application Error event tells about a crash.</summary>
public sealed class CrashReportsTests
{
    private static readonly string[] BaronyEvent =
    [
        "Event[0]:",
        "  Source: Application Error",
        "  Event ID: 1000",
        "  Description: ",
        "Faulting application name: barony.exe, version: 1.0.0.0, time stamp: 0x69cf1781",
        "Faulting module name: barony.exe, version: 1.0.0.0, time stamp: 0x69cf1781",
        "Exception code: 0xc0000409",
        "Fault offset: 0x0000000000a3b505",
        "Faulting process id: 0x3cd8",
    ];

    [Fact]
    public void The_fault_names_the_module_its_offset_and_the_exception()
    {
        // Arrange
        var lines = BaronyEvent;

        // Act
        var fault = CrashReports.Fault(lines, @"D:\Games\Barony\barony.exe");

        // Assert
        fault.Should().Be(new CrashFault("barony.exe", "1.0.0.0", "0xc0000409", "0xa3b505", InGame: true));
    }

    [Fact]
    public void A_fault_in_a_library_isnt_the_games_own_code()
    {
        // Arrange
        string[] lines = ["Faulting module name: ucrtbase.dll, version: 10.0.19041.3636, time stamp: 0x81cf5d89", "Exception code: 0xc0000409"];

        // Act
        var fault = CrashReports.Fault(lines, "barony.exe");

        // Assert
        fault!.InGame.Should().BeFalse();
    }

    [Fact]
    public void An_event_without_a_faulting_module_has_no_fault()
    {
        // Arrange
        string[] lines = ["Имя сбойного модуля: barony.exe", "Код исключения: 0xc0000409"];

        // Act
        var fault = CrashReports.Fault(lines, "barony.exe");

        // Assert
        fault.Should().BeNull();
    }
}
