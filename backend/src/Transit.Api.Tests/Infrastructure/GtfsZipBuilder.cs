using System.IO.Compression;
using System.Text;

namespace MageRide.Transit.Tests.Infrastructure;

/// <summary>
/// Builds real GTFS zips in memory, so every C057 test posts the artefact BR-32.1 describes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corridor is the same one C056 asserts routing against</b> — Colombo Fort → Kottawa on
/// route 138, at real coordinates — so "activation swaps the live tables and transit-svc serves the
/// new routes" can be asserted end to end: this builds the feed, the endpoint activates it, and
/// <c>GET /v1/transit/options</c> on the running service is what says whether it worked.
/// </para>
/// <para>
/// <b>A builder rather than a fixture file</b>, because most of these tests are about a feed that
/// is wrong in one specific way, and the interesting part of each is the single line that differs
/// from a feed that would pass.
/// </para>
/// </remarks>
internal sealed class GtfsZipBuilder
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    private string? _prefix;

    /// <summary>A feed that passes BR-32.1 with no errors and no warnings.</summary>
    public static GtfsZipBuilder Valid(string feedVersion = "2026-07-01")
    {
        var builder = new GtfsZipBuilder();

        builder._files["agency.txt"] =
            """
            agency_id,agency_name,agency_url,agency_timezone
            SLTB,Sri Lanka Transport Board,https://sltb.lk,Asia/Colombo
            """;

        // The High Level Road corridor at its actual coordinates.
        builder._files["stops.txt"] =
            """
            stop_id,stop_name,stop_lat,stop_lon
            FORT,Colombo Fort,6.9344,79.8428
            MRD,Maradana,6.9297,79.8656
            NUG,Nugegoda,6.8649,79.8997
            MHR,Maharagama,6.8482,79.9265
            KTW,Kottawa,6.8410,79.9653
            """;

        builder._files["routes.txt"] =
            """
            route_id,agency_id,route_short_name,route_long_name,route_type
            R138,SLTB,138,Colombo Fort - Kottawa,3
            """;

        builder._files["trips.txt"] =
            """
            route_id,service_id,trip_id,trip_headsign,direction_id,shape_id
            R138,WEEKDAY,T138-1,Kottawa,0,S138
            R138,WEEKDAY,T138-R,Colombo Fort,1,S138R
            """;

        builder._files["stop_times.txt"] =
            """
            trip_id,arrival_time,departure_time,stop_id,stop_sequence
            T138-1,06:00:00,06:00:00,FORT,1
            T138-1,06:10:00,06:10:00,MRD,2
            T138-1,06:30:00,06:30:00,NUG,3
            T138-1,06:40:00,06:40:00,MHR,4
            T138-1,06:55:00,06:55:00,KTW,5
            T138-R,18:00:00,18:00:00,KTW,1
            T138-R,18:15:00,18:15:00,MHR,2
            T138-R,18:25:00,18:25:00,NUG,3
            T138-R,18:45:00,18:45:00,MRD,4
            T138-R,18:55:00,18:55:00,FORT,5
            """;

        // Ends well past the 30-day horizon BR-32.1 warns inside of, so a valid feed is warning-free.
        builder._files["calendar.txt"] =
            """
            service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
            WEEKDAY,1,1,1,1,1,0,0,20260101,20271231
            """;

        builder._files["shapes.txt"] =
            """
            shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence
            S138,6.9344,79.8428,1
            S138,6.9297,79.8656,2
            S138,6.8649,79.8997,3
            S138,6.8482,79.9265,4
            S138,6.8410,79.9653,5
            S138R,6.8410,79.9653,1
            S138R,6.8482,79.9265,2
            S138R,6.8649,79.8997,3
            S138R,6.9297,79.8656,4
            S138R,6.9344,79.8428,5
            """;

        builder._files["feed_info.txt"] =
            $"""
            feed_publisher_name,feed_publisher_url,feed_lang,feed_version
            MageRide,https://mageride.lk,en,{feedVersion}
            """;

        return builder;
    }

    /// <summary>Replaces one file wholesale.</summary>
    public GtfsZipBuilder With(string file, string content)
    {
        _files[file] = content;

        return this;
    }

    /// <summary>Appends a row to an existing file.</summary>
    public GtfsZipBuilder Append(string file, string row)
    {
        _files[file] = _files[file].TrimEnd('\n') + "\n" + row;

        return this;
    }

    public GtfsZipBuilder Without(string file)
    {
        _files.Remove(file);

        return this;
    }

    /// <summary>Wraps every entry in a directory, the way "zip the GTFS folder" produces.</summary>
    public GtfsZipBuilder InFolder(string prefix)
    {
        _prefix = prefix.TrimEnd('/') + "/";

        return this;
    }

    public byte[] Build()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in _files)
            {
                var entry = archive.CreateEntry(_prefix + name, CompressionLevel.Fastest);

                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                writer.Write(content.ReplaceLineEndings("\n"));
                writer.Write('\n');
            }
        }

        return buffer.ToArray();
    }
}
