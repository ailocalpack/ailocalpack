using AiLocalPack.Agents;
using AiLocalPack.Agents.Install;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Background;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Pipeline;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Desktop.Generation;

/// <summary>
/// The real stages (<see cref="GenerationPipeline"/>): the job's agent (the player's copy, else the app's), which starts
/// the toolkit server as this app in its hidden mode (<see cref="ToolkitServerProgram.DesktopApp"/>), and the Build
/// stage's font swap, local pack store and test run; the Translate stage runs as many chunks at once as Settings → AI
/// agents says (<see cref="AppSettings.TranslateWorkers"/>, read when the job starts or resumes).
/// </summary>
public sealed class PipelineStagesFactory(
    IProcessRunner runner,
    OsEnvironment environment,
    AgentInstaller installer,
    RecipeTools tools,
    GenerationEstimator estimator,
    WorkingCopies copies,
    FontFixer fonts,
    LocalPackStore localPacks,
    SettingsStore settings) : IGenerationStagesFactory
{
    public GenerationStages Create(Game game, GenerationJobEntry entry)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(entry);
        var agentId = entry.Job.Agent;
        var adapter = AgentAdapters.Create(agentId, runner, environment, installer.ManagedDirectory(agentId))
            ?? throw new InvalidOperationException($"The agent \"{agentId}\" is unknown to this version.");
        var app = AppLaunch.ForCurrentProcess(environment, Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location)
            ?? throw new InvalidOperationException("The app's own path is unknown, so the agent can't start its toolkit server.");
        var build = new BuildServices(fonts, localPacks, new GameTestRunner(runner, environment.Os));
        var services = new PipelineServices(game, new PipelineAgent(adapter, ToolkitServerProgram.DesktopApp(app, environment)), tools, estimator, copies, Build: build,
            Translate: new TranslateOptions(Workers: settings.Current.TranslateWorkerCount));
        return GenerationPipeline.Create(services);
    }
}
