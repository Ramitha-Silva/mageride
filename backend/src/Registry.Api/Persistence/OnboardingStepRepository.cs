using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary>
/// <c>registry.onboarding_steps</c> — the persisted per-step Mode-C state machine (AL-30;
/// migration 0305).
/// </summary>
/// <remarks>
/// Rows appear as steps are saved, not when the vehicle is created. "A vehicle with ≥1 saved step
/// shows Incomplete" (BR-25.4) is therefore a row count, and a vehicle nobody has started
/// onboarding has no rows at all — which is what makes <c>details</c> the resume point for a fresh
/// vehicle without storing four <c>pending_input</c> placeholders that mean nothing.
/// </remarks>
public interface IOnboardingStepRepository
{
    /// <summary>Every saved step for a vehicle, in wizard order.</summary>
    Task<IReadOnlyList<OnboardingStepRow>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Saves one step, replacing whatever was there — a re-upload overwrites (US-2.15).</summary>
    Task<OnboardingStepRow> SaveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string step,
        string status,
        string? fieldsJson,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a saved step to a new verdict without touching its stored input. Used when a step's
    /// verdict changes because something <em>else</em> did — an officer confirming a pending field
    /// (C062), or the registration number being edited under an already-verified photos step.
    /// </summary>
    Task<bool> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string step,
        string status,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOnboardingStepRepository"/>
public sealed class OnboardingStepRepository : IOnboardingStepRepository
{
    private const string Columns = "vehicle_id, step, status, fields::text AS fields, saved_at";

    public async Task<IReadOnlyList<OnboardingStepRow>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<OnboardingStepRow>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.onboarding_steps WHERE vehicle_id = @VehicleId;",
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        // Ordered here rather than in SQL: the order that matters is the wizard's, and 'details'
        // sorts after 'photos' alphabetically, so an ORDER BY step would resume drivers on the
        // wrong screen.
        return [.. rows.OrderBy(row => OnboardingSteps.Ordinal(row.Step))];
    }

    public async Task<OnboardingStepRow> SaveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string step,
        string status,
        string? fieldsJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The `::jsonb` cast is load-bearing: Npgsql sends a string parameter as `text`, and
        // Postgres will not coerce text into a jsonb column on its own.
        return await connection.QuerySingleAsync<OnboardingStepRow>(new CommandDefinition(
            $"""
             INSERT INTO registry.onboarding_steps (vehicle_id, step, status, fields, saved_at)
             VALUES (@VehicleId, @Step, @Status, @FieldsJson::jsonb, now())
             ON CONFLICT (vehicle_id, step) DO UPDATE
               SET status = EXCLUDED.status,
                   fields = EXCLUDED.fields,
                   saved_at = EXCLUDED.saved_at
             RETURNING {Columns};
             """,
            new { VehicleId = vehicleId, Step = step, Status = status, FieldsJson = fieldsJson },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string step,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.onboarding_steps
               SET status = @Status
             WHERE vehicle_id = @VehicleId AND step = @Step AND status <> @Status;
            """,
            new { VehicleId = vehicleId, Step = step, Status = status },
            transaction,
            cancellationToken: cancellationToken));

        return updated == 1;
    }
}
