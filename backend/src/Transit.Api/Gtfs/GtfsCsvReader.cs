using System.Text;

namespace MageRide.Transit.Gtfs;

/// <summary>
/// A forward-only RFC 4180 reader over one GTFS text file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written, and streaming, on purpose.</b> A national <c>stop_times.txt</c> is half a
/// million rows; the validator reads it once and the importer reads it again, and neither may
/// hold it. Nothing here materialises more than a single record, so both passes cost one buffer.
/// </para>
/// <para>
/// <b>RFC 4180 rather than <c>Split(',')</c>.</b> GTFS route long names carry commas
/// ("Colombo, Fort – Kandy") and quoted fields may contain newlines, so a line-oriented split
/// silently shifts every column right for exactly the feeds most likely to be uploaded here.
/// </para>
/// <para>
/// Real feeds are not clean: a UTF-8 BOM on the header, spaces around values, a short row where
/// the trailing optional columns were simply omitted. Each is tolerated rather than rejected —
/// BR-32.1's quality gate is referential, and a feed refused for a BOM is a support ticket.
/// </para>
/// </remarks>
internal sealed class GtfsCsvReader : IDisposable
{
    private readonly TextReader _reader;
    private readonly Dictionary<string, int> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fields = [];
    private readonly StringBuilder _field = new();
    private readonly char[] _buffer = new char[64 * 1024];

    private int _bufferLength;
    private int _bufferPosition;
    private bool _exhausted;

    private GtfsCsvReader(TextReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// 1-based row number of the record just read, <b>counting the header as row 1</b> — so it is
    /// the line number an operator sees in a spreadsheet, which is what a
    /// <c>FeedIssue.row</c> has to be to be usable.
    /// </summary>
    public long Row { get; private set; }

    /// <summary>The header, in file order.</summary>
    public IReadOnlyList<string> Header { get; private set; } = [];

    /// <summary>Opens the file and consumes its header. Returns null for an empty file.</summary>
    public static GtfsCsvReader? Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // detectEncodingFromByteOrderMarks strips a UTF-8 BOM; leaveOpen because the caller owns
        // the zip entry stream.
        var text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var reader = new GtfsCsvReader(text);

        if (!reader.ReadRecord())
        {
            reader.Dispose();
            return null;
        }

        var header = new string[reader._fields.Count];

        for (var index = 0; index < reader._fields.Count; index++)
        {
            var name = reader._fields[index];
            header[index] = name;

            // First wins: a duplicated column is a broken feed, but the first occurrence is the
            // one a reader that split on commas would have used, so this reads the same file.
            _ = reader._columns.TryAdd(name, index);
        }

        reader.Header = header;

        return reader;
    }

    /// <summary>Whether the file declares a column.</summary>
    public bool Has(string column) => _columns.ContainsKey(column);

    /// <summary>Advances to the next record. False at end of file.</summary>
    public bool Read() => ReadRecord();

    /// <summary>
    /// The field's value, or null when the column is absent, the row is short, or the value is
    /// blank. <b>Blank and absent are the same answer</b>, which is what GTFS means by an optional
    /// field: <c>trip_headsign,,</c> and no <c>trip_headsign</c> column are the same feed.
    /// </summary>
    public string? this[string column]
    {
        get
        {
            if (!_columns.TryGetValue(column, out var index) || index >= _fields.Count)
            {
                return null;
            }

            var value = _fields[index];

            return value.Length == 0 ? null : value;
        }
    }

    private bool ReadRecord()
    {
        _fields.Clear();

        // Skip blank lines rather than reporting them as rows of one empty field — a trailing
        // newline at the end of a file would otherwise be an error on every feed ever exported.
        while (true)
        {
            var next = Peek();

            if (next < 0)
            {
                return false;
            }

            if (next is '\r' or '\n')
            {
                ConsumeNewline();
                continue;
            }

            break;
        }

        while (true)
        {
            _fields.Add(ReadField());

            var next = Peek();

            if (next < 0)
            {
                break;
            }

            if (next == ',')
            {
                _bufferPosition++;
                continue;
            }

            ConsumeNewline();
            break;
        }

        Row++;

        return true;
    }

    private string ReadField()
    {
        _field.Clear();

        var quoted = Peek() == '"';

        if (quoted)
        {
            _bufferPosition++;

            while (true)
            {
                var next = Next();

                if (next < 0)
                {
                    // Unterminated quote at end of file: take what there is. Refusing here would
                    // fail the whole upload on a truncated last line and say nothing useful.
                    break;
                }

                if (next != '"')
                {
                    _field.Append((char)next);
                    continue;
                }

                if (Peek() == '"')
                {
                    _bufferPosition++;
                    _field.Append('"');
                    continue;
                }

                break;
            }
        }
        else
        {
            while (true)
            {
                var next = Peek();

                if (next < 0 || next == ',' || next is '\r' or '\n')
                {
                    break;
                }

                _bufferPosition++;
                _field.Append((char)next);
            }
        }

        // Trimmed for both shapes: real exports pad columns to line them up, and an id with a
        // trailing space matches nothing on either side of a referential check.
        return _field.ToString().Trim();
    }

    private void ConsumeNewline()
    {
        var next = Peek();

        if (next == '\r')
        {
            _bufferPosition++;
            next = Peek();
        }

        if (next == '\n')
        {
            _bufferPosition++;
        }
    }

    private int Peek()
    {
        if (_bufferPosition < _bufferLength)
        {
            return _buffer[_bufferPosition];
        }

        return Fill() ? _buffer[_bufferPosition] : -1;
    }

    private int Next()
    {
        var next = Peek();

        if (next >= 0)
        {
            _bufferPosition++;
        }

        return next;
    }

    private bool Fill()
    {
        if (_exhausted)
        {
            return false;
        }

        _bufferLength = _reader.Read(_buffer, 0, _buffer.Length);
        _bufferPosition = 0;

        if (_bufferLength > 0)
        {
            return true;
        }

        _exhausted = true;

        return false;
    }

    public void Dispose() => _reader.Dispose();
}
