using System.Net.Http.Json;
using System.Globalization;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Operations;

/// <summary>
/// US-13.11's not-started alarm: the sweep that notices a booked departure nobody made, and the
/// push that rings in the assigned driver's app (US-13.11b).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two statements per pass, in this order.</b> Departures that were made are recorded first, so
/// nothing decides to ring an alarm about a bus that has already left; then the ones whose alarm
/// offset has passed are claimed and moved to <c>MISSED</c> in the same statement that selects
/// them. The claim is what makes the alarm exactly-once across replicas — the shape
/// notification-svc's E-01 ack sweep uses.
/// </para>
/// <para>
/// <b>The state change commits before the push is attempted.</b> A notification that failed to send
/// must not roll back the record that the departure was missed: the operator's own screen reads
/// that record, and an alarm that could not be pushed is still an alarm the Fleet Portal has to
/// show. The push is best effort and says so when it fails, which is the same split
/// <c>registry.document_notices</c> makes for E-03.
/// </para>
/// <para>
/// <b>Every replica runs this.</b> There is no lease and there must not be — a lock protecting an
/// operation the claim already makes exclusive is a second mechanism that can fail on its own.
/// </para>
/// </remarks>
internal sealed class ScheduleAlarmWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    IOptions<FleetOptions> options,
    TimeProvider clock,
    ILogger<ScheduleAlarmWorker> logger) : BackgroundService
{
    /// <summary>
    /// The notification type notification-svc rings this with.
    /// </summary>
    /// <remarks>
    /// <b>Δ C059</b> — added to <c>NotificationCatalogue</c> in the same change, with a trilingual
    /// template in migration 1905. D5' §14.4 has no row for it and <c>SCHEDULED_REMINDER</c> is not
    /// it: that one is dispatch-svc's "your ride is in 30 minutes" (US-6A.15/US-10.9), a courtesy
    /// before a booking, where this is an exception after a departure that should have happened.
    /// Adding the type without a producer is what 1902 refused to do; this component is the
    /// producer, which is why both halves land together.
    /// </remarks>
    public const string NotificationType = "SCHEDULE_NOT_STARTED";

    /// <summary>The named client for notification-svc's internal plane.</summary>
    public const string HttpClientName = "notification-svc";

    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ScheduleAlarmsEnabled)
        {
            logger.LogError(
                "Fleet:ScheduleAlarmsEnabled is false: NO SCHEDULE-NOT-STARTED ALARM WILL EVER RING (US-13.11). A "
                + "booked departure that nobody makes stays SCHEDULED for ever, which on the Fleet Portal is "
                + "indistinguishable from a departure whose time has not come.");

            return;
        }

        using var timer = new PeriodicTimer(_options.ScheduleAlarmInterval, _clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A sweep that threw must not take the host down with it.
            catch (Exception exception)
            {
                logger.LogError(exception, "The schedule-alarm sweep failed; the next pass will retry it.");
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>One pass. Public so a test can drive it without a timer.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var schedules = scope.ServiceProvider.GetRequiredService<IFleetScheduleRepository>();
        var assignments = scope.ServiceProvider.GetRequiredService<IFleetAssignmentRepository>();

        var now = _clock.GetUtcNow();

        List<DueScheduleAlarm> due;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            await schedules.MarkStartedAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                now,
                _options.ScheduleEarlyStartGrace,
                cancellationToken);

            var claimed = await schedules.ClaimMissedAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                now,
                _options.ScheduleAlarmBatchSize,
                cancellationToken);

            due = [];

            foreach (var alarm in claimed)
            {
                // Whoever was meant to be driving at the booked time, not whoever is assigned now:
                // an alarm raised at 06:20 about the 06:10 belongs to the 06:10's driver, and a
                // shift that changed in between must not redirect it.
                var drivers = await assignments.DriversCoveringAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, alarm.VehicleId, alarm.DepartAt, cancellationToken);

                var members = await schedules.ListMemberIdsAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, alarm.FleetId, cancellationToken);

                due.Add(alarm with { DriverIds = drivers, MemberIds = members });
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // Committed first, on purpose: see the class remarks.
        foreach (var alarm in due)
        {
            await RaiseAsync(alarm, cancellationToken);
        }

        return due.Count;
    }

    private async Task RaiseAsync(DueScheduleAlarm alarm, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Vehicle {Registration} of fleet {FleetId} did not start its {DepartAt} departure within "
            + "{AlarmMinutes} minute(s) (US-13.11).",
            alarm.RegistrationNumber,
            alarm.FleetId,
            alarm.DepartAt,
            alarm.NotStartedAlarmMinutes);

        if (string.IsNullOrWhiteSpace(_options.NotificationBaseUrl))
        {
            logger.LogWarning(
                "Fleet:NotificationBaseUrl is not configured, so nobody was told: the ringing alarm US-13.11b "
                + "promises in the assigned driver's app did not happen. The schedule is recorded MISSED and the "
                + "Fleet Portal can still show it.");

            return;
        }

        // Drivers and members in one call. notification-svc resolves each recipient's own language
        // and channel (D-26) — no user-facing string is composed here, which is C051's rule.
        var recipients = alarm.DriverIds.Concat(alarm.MemberIds)
            .Distinct()
            .Select(id => id.ToString())
            .ToArray();

        if (recipients.Length == 0)
        {
            logger.LogWarning(
                "Schedule {ScheduleId} was missed and there is nobody to tell: the vehicle has no driver assigned "
                + "over its departure and the organisation has no members.",
                alarm.Id);

            return;
        }

        try
        {
            var client = clients.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/notify/send")
            {
                Content = JsonContent.Create(
                    new SendNotificationBody(
                        TemplateKey: null,
                        NotificationType: NotificationType,
                        Recipients: recipients,
                        Data: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["scheduleId"] = alarm.Id.ToString(),
                            ["vehicleId"] = alarm.VehicleId.ToString(),
                            ["registrationNumber"] = alarm.RegistrationNumber,
                            ["departAt"] = alarm.DepartAt.ToString("O", CultureInfo.InvariantCulture),
                        }),
                    options: MageRideJson.Options),
            };

            if (!string.IsNullOrWhiteSpace(_options.NotificationInternalApiKey))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-MageRide-Internal-Key", _options.NotificationInternalApiKey);
            }

            request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, $"schedule-alarm:{alarm.Id}");

            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "notification-svc answered {Status} to the not-started alarm for schedule {ScheduleId}; the "
                    + "schedule stays MISSED and nobody was pushed.",
                    (int)response.StatusCode,
                    alarm.Id);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            // Best effort by design. The alarm is not re-queued: the claim already moved the
            // schedule to MISSED, and a retry loop here would ring a driver's phone about a
            // departure that is by then an hour old.
            logger.LogWarning(
                exception,
                "notification-svc could not be reached for the not-started alarm on schedule {ScheduleId}.",
                alarm.Id);
        }
    }

    /// <summary>
    /// notification-svc's <c>SendNotificationBody</c>, as far as this producer fills it.
    /// </summary>
    /// <remarks>
    /// A local copy rather than a project reference, the shape every caller of that route takes:
    /// what is coupled is the JSON on one route, not an assembly.
    /// </remarks>
    private sealed record SendNotificationBody(
        string? TemplateKey,
        string? NotificationType,
        IReadOnlyList<string> Recipients,
        IReadOnlyDictionary<string, string> Data);
}
