namespace Jibo.Cloud.Infrastructure.Audio;

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed record ExternalProcessResult(int ExitCode, string StdOut, string StdErr);
