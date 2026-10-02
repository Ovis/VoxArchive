using System.Diagnostics;
using VoxArchive.Infrastructure;

namespace VoxArchive.IntegrationTests;

public sealed class ProcessCatalogServiceTests
{
    [Test]
    public async Task GetRunningProcessesAsync_CurrentProcessBuildsExecutableNameFromProcessName()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var sut = new ProcessCatalogService();

        var processes = await sut.GetRunningProcessesAsync();

        var current = processes.Single(x => x.ProcessId == currentProcess.Id);
        Assert.That(current.ApplicationName, Is.EqualTo(currentProcess.ProcessName));
        Assert.That(current.ExecutableName, Is.EqualTo($"{currentProcess.ProcessName}.exe"));
    }
}
