using MinRepoMobile.Models;

namespace MinRepoMobile.Services;

public interface IBackgroundExtractionService
{
    bool IsRunning { get; }

    Task StartAsync(ExtractionRequest request);

    void Cancel();
}

