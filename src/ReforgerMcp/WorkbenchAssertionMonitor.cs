using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReforgerMcp;

// A busy refusal is known to precede native dispatch; it is safe to defer it.
internal sealed class WorkbenchBusyException(string message) : InvalidOperationException(message);

internal sealed class WorkbenchAssertionMonitor(Configuration configuration, WorkbenchSession session,
    ILogger<WorkbenchAssertionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.WorkbenchEntries.Any()) return;
        (int Pid, long Started)? finished = null;
        object? finishedResult = null;
        string? previous = null;
        void Report(string state, int? pid, object? detail = null)
        {
            var result = new { state, processId = pid, detail };
            session.AutomaticResourceLeakSuppression = result;
            var text = JsonSerializer.Serialize(result, Configuration.Json);
            if (text == previous) return;
            previous = text;
            logger.LogInformation("Automatic resource-leak assertion suppression: {Result}", text);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            (int Pid, long Started) identity;
            try
            {
                using var process = WorkbenchSession.SelectProcess(configuration);
                identity = (process.Id, process.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception ex)
            {
                Report("waiting_for_workbench", null, ex.Message);
                continue;
            }
            if (finished == identity)
            {
                if (finishedResult is not null) session.AutomaticResourceLeakSuppression = finishedResult;
                continue;
            }
            try
            {
                var result = JsonSerializer.SerializeToElement(
                    await session.ResourceLeakAssertion("suppress", identity.Pid, stoppingToken), Configuration.Json);
                while (result.GetProperty("state").GetString() == "pending")
                {
                    Report("pending", identity.Pid, result);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    result = JsonSerializer.SerializeToElement(await session.Operation(
                        result.GetProperty("operationId").GetString()!, stoppingToken), Configuration.Json);
                }
                var state = result.GetProperty("state").GetString()!;
                Report(state, identity.Pid, state == "blocked" ? result.GetProperty("reason").GetString() : result);
                if (state != "blocked")
                {
                    finished = identity;
                    finishedResult = session.AutomaticResourceLeakSuppression;
                }
            }
            catch (WorkbenchBusyException ex)
            {
                Report("waiting_for_idle", identity.Pid, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Never replay an uncertain dispatch or repeatedly probe an incompatible binary.
                finished = identity;
                Report("unavailable", identity.Pid, ex.Message);
                finishedResult = session.AutomaticResourceLeakSuppression;
            }
        }
    }
}
