using System.IO;
using System.Text;
using System.Threading.Channels;

namespace VPNGate.Desktop;

// UI callers only enqueue; a single background writer owns disk I/O. The display keeps a bounded tail.
internal sealed class BufferedSessionLog : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<string> _tail = new();
    private readonly Channel<string> _fileLines;
    private readonly Task _writer;
    private readonly string _path;
    private long _version, _dropped;

    public BufferedSessionLog(string path)
    {
        _path = path;
        _fileLines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        }, _ => Interlocked.Increment(ref _dropped));
        _writer = Task.Run(WriteFileAsync);
    }
    public void Write(string line)
    {
        lock (_gate)
        {
            _tail.Enqueue(line); while (_tail.Count > 300) _tail.Dequeue();
            _version++;
            _fileLines.Writer.TryWrite(line);
        }
    }
    public string? Snapshot(ref long displayedVersion)
    {
        lock (_gate)
        {
            if (displayedVersion == _version) return null;
            displayedVersion = _version;
            return string.Join(Environment.NewLine, _tail);
        }
    }
    private async Task WriteFileAsync()
    {
        try
        {
            while (await _fileLines.Reader.WaitToReadAsync())
            {
                var batch = new StringBuilder();
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) batch.AppendLine($"[日志] 高流量期间省略了 {dropped} 行，保留后续日志。");
                for (var i = 0; i < 256 && _fileLines.Reader.TryRead(out var line); i++) batch.AppendLine(line);
                if (File.Exists(_path) && new FileInfo(_path).Length > 5 * 1024 * 1024)
                    await File.WriteAllTextAsync(_path, "");
                await File.AppendAllTextAsync(_path, batch.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _fileLines.Writer.TryComplete();
            lock (_gate)
            {
                _tail.Enqueue($"[日志] 无法写入日志文件：{exception.Message}");
                while (_tail.Count > 300) _tail.Dequeue(); _version++;
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _fileLines.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
    }
}
