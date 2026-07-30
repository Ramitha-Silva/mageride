using System.Text.Json;
using MageRide.Shared.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Infrastructure;

/// <summary>
/// Everything one client heard, collected off a real hub connection.
/// </summary>
/// <remarks>
/// Payloads are read as <see cref="JsonElement"/> rather than bound to the contract records, so the
/// property names <em>on the wire</em> are asserted rather than assumed. SignalR resolves both
/// method and event names by string and the JSON protocol binds by property name — a rename on
/// either side would deserialise into silent defaults, which is the failure mode this suite exists
/// to catch.
/// </remarks>
internal sealed class LiveEvents
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, VehicleFrame> _latest = [];
    private readonly List<(Guid VehicleId, string Reason)> _removed = [];
    private readonly List<JsonElement> _rideStates = [];
    private readonly List<(Guid RideId, double Lat, double Lng)> _driverPositions = [];
    private readonly List<JsonElement> _locationRequests = [];
    private readonly List<(Guid RideId, string Status)> _packages = [];
    private readonly List<Guid> _sharesRevoked = [];

    public int Batches { get; private set; }

    public IReadOnlyCollection<Guid> Vehicles
    {
        get
        {
            lock (_gate)
            {
                return [.. _latest.Keys];
            }
        }
    }

    public IReadOnlyList<(Guid VehicleId, string Reason)> Removed
    {
        get
        {
            lock (_gate)
            {
                return [.. _removed];
            }
        }
    }

    public IReadOnlyList<JsonElement> RideStates
    {
        get
        {
            lock (_gate)
            {
                return [.. _rideStates];
            }
        }
    }

    public IReadOnlyList<(Guid RideId, double Lat, double Lng)> DriverPositions
    {
        get
        {
            lock (_gate)
            {
                return [.. _driverPositions];
            }
        }
    }

    public IReadOnlyList<JsonElement> LocationRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _locationRequests];
            }
        }
    }

    public IReadOnlyList<(Guid RideId, string Status)> Packages
    {
        get
        {
            lock (_gate)
            {
                return [.. _packages];
            }
        }
    }

    public IReadOnlyList<Guid> SharesRevoked
    {
        get
        {
            lock (_gate)
            {
                return [.. _sharesRevoked];
            }
        }
    }

    /// <summary>Subscribes to all seven server-to-client events (<c>signalr-hub.md</c> §3).</summary>
    public LiveEvents Attach(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.On<JsonElement>(Contract.Events.VehiclePositions, RecordPositions);

        connection.On<JsonElement>(Contract.Events.VehicleRemoved, element =>
        {
            lock (_gate)
            {
                _removed.Add((
                    element.GetProperty("vehicleId").GetGuid(),
                    element.GetProperty("reason").GetString() ?? string.Empty));
            }
        });

        connection.On<JsonElement>(Contract.Events.RideStateChanged, element =>
        {
            lock (_gate)
            {
                _rideStates.Add(element.Clone());
            }
        });

        connection.On<JsonElement>(Contract.Events.DriverPosition, element =>
        {
            lock (_gate)
            {
                _driverPositions.Add((
                    element.GetProperty("rideId").GetGuid(),
                    element.GetProperty("lat").GetDouble(),
                    element.GetProperty("lng").GetDouble()));
            }
        });

        connection.On<JsonElement>(Contract.Events.LocationRequestResolved, element =>
        {
            lock (_gate)
            {
                _locationRequests.Add(element.Clone());
            }
        });

        connection.On<JsonElement>(Contract.Events.PackageStatus, element =>
        {
            lock (_gate)
            {
                _packages.Add((
                    element.GetProperty("rideId").GetGuid(),
                    element.GetProperty("status").GetString() ?? string.Empty));
            }
        });

        connection.On<JsonElement>(Contract.Events.ShareRevoked, element =>
        {
            lock (_gate)
            {
                _sharesRevoked.Add(element.GetProperty("vehicleId").GetGuid());
            }
        });

        return this;
    }

    public VehicleFrame? Latest(Guid vehicleId)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(vehicleId, out var frame) ? frame : null;
        }
    }

    public bool Saw(Guid vehicleId) => Latest(vehicleId) is not null;

    public bool WasRemoved(Guid vehicleId, string reason) =>
        Removed.Any(entry => entry.VehicleId == vehicleId && entry.Reason == reason);

    /// <summary>Forgets everything, so a second phase of a test asserts on its own traffic.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _latest.Clear();
            _removed.Clear();
            _rideStates.Clear();
            _driverPositions.Clear();
            _locationRequests.Clear();
            _packages.Clear();
            _sharesRevoked.Clear();
            Batches = 0;
        }
    }

    private void RecordPositions(JsonElement batch)
    {
        lock (_gate)
        {
            Batches++;

            foreach (var element in batch.EnumerateArray())
            {
                var frame = new VehicleFrame(
                    element.GetProperty("vehicleId").GetGuid(),
                    element.GetProperty("lat").GetDouble(),
                    element.GetProperty("lng").GetDouble(),
                    Read(element, "heading")?.GetInt32(),
                    Read(element, "speed")?.GetDouble(),
                    Read(element, "type")?.GetString(),
                    Read(element, "mode")?.GetString());

                _latest[frame.VehicleId] = frame;
            }
        }
    }

    private static JsonElement? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null ? value : null;
}
