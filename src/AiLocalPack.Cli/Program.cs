using AiLocalPack.Cli;
using AiLocalPack.Core.Storage;

return CliApp.Build(OsEnvironment.Current).Parse(args).Invoke();
