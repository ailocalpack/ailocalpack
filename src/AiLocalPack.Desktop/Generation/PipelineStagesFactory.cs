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
/// the toolkit server as this app in its hidden mode; one of the player's own models runs through <see cref="Agents.ILlmHost"/> (<see cref="ToolkitServerProgram.DesktopApp"/>), and the Build
/// stage's font swap, local pack store and test run; the Translate stage runs as many chunks at once as Settings → AI
/// agents says (<see cref="AppSettings.TranslateWorkers"/>, read when the job starts or resumes).
/// </summary>
public sealed class PipelineStagesFactory(
    IProcessRunner runner,
    OsEnvironment environment,
    AgentInstaller installer,
    RecipeTools tools,
    GenerationEstimator estimator,
    FontFixer fonts,
    LocalPackStore localPacks,
    SettingsStore settings,
    IGameTestRunner testRunner,
    GameViews views,
    Agents.ILlmHost llms) : IGenerationStagesFactory, IFixRunnerFactory
{
    public GenerationStages Create(Game game, GenerationJobEntry entry)
    {
        var (agent, build) = AgentAndBuild(game, entry);
        var services = new PipelineServices(game, agent, tools, estimator, Build: build,
            Translate: new TranslateOptions(Workers: settings.Current.TranslateWorkerCount));
        return GenerationPipeline.Create(services);
    }

    /// <summary>Fixes with AI of the job's translation (task 75): the job's agent and the Build stage.</summary>
    IFixRunner IFixRunnerFactory.Create(Game game, GenerationJobEntry entry)
    {
        var (agent, build) = AgentAndBuild(game, entry);
        var stage = new BuildStage(game, agent, tools, build);
        return new FixRunner(game, agent, stage, localPacks, TimeProvider.System, stage.TestRunServices);
    }

    private (PipelineAgent Agent, BuildServices Build) AgentAndBuild(Game game, GenerationJobEntry entry)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(entry);
        var agentId = entry.Job.Agent;
        var adapter = (Core.Llm.LlmConnections.IsLlmId(agentId)
                ? llms.CreateAdapter(agentId) ?? throw new InvalidOperationException($"The model \"{agentId}\" was removed in Settings → AI agents: add it again to go on.")
                : AgentAdapters.Create(agentId, runner, environment, installer.ManagedDirectory(agentId)))
            ?? throw new InvalidOperationException($"The agent \"{agentId}\" is unknown to this version.");
        var app = AppLaunch.ForCurrentProcess(environment, Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location)
            ?? throw new InvalidOperationException("The app's own path is unknown, so the agent can't start its toolkit server.");
        var build = new BuildServices(fonts, localPacks, testRunner, environment, views);
        return (new PipelineAgent(adapter, ToolkitServerProgram.DesktopApp(app, environment)), build);
    }
}
