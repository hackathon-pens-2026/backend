using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignIt.Modules.Email.Services;

namespace SignIt.Infrastructure.Email;

// Penjadwal reminder SLA: hanya mengantre untuk tugas Active yang overdue; pengiriman oleh EmailDeliveryWorker.
public sealed class WorkflowReminderWorker(IServiceScopeFactory scopes, ResetEmailOptions options,
    TimeProvider clock, ILogger<WorkflowReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var emails = scope.ServiceProvider.GetRequiredService<WorkflowEmailService>();
                var queued = await emails.DispatchDueRemindersAsync(clock.GetUtcNow(), stoppingToken);
                if (queued > 0) logger.LogInformation("Reminder workflow di-queue: {Count}.", queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Workflow reminder worker gagal ({ExceptionType}).", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.ReminderPollSeconds), clock, stoppingToken);
        }
    }
}
