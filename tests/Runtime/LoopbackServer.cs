using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// A test HTTP server on <c>http://127.0.0.1:&lt;a free port&gt;/</c> (<see cref="HttpListener"/>, no packages): it answers
/// each path as scripted (<see cref="Respond"/>, else 404), can hold its answer or its body until a task completes, write a
/// body slowly, drop the connection part way through, announce another length than it sends, go down (refusing
/// connections) and come back on its port, and records every request (method, path, headers, body) as it arrives.
/// Disposing it releases whatever it holds, stops the listener and waits for every request it is answering.
/// <para>
/// The managed <see cref="HttpListener"/> of macOS and Linux sends a whole (empty) answer when a response is aborted
/// before its first byte, so a connection is only ever dropped after some of the body; a server that does not answer at
/// all is <see cref="Down"/>.
/// </para>
/// </summary>
sealed class LoopbackServer : IAsyncDisposable
{
    /// <summary>A request as it arrived.</summary>
    /// <param name="Method">Such as <c>GET</c>.</param>
    /// <param name="Path">The path, without the query, such as <c>/rpc/Switch.GetStatus</c>.</param>
    /// <param name="Target">The path with the query, as sent.</param>
    /// <param name="Headers">Its headers by name, in any case; repeated ones joined with <c>,</c>.</param>
    /// <param name="Body">Its body; empty for none.</param>
    public sealed record Request(string Method, string Path, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    /// <summary>What the server answers on a path.</summary>
    public sealed class Reply
    {
        public int Status { get; init; } = 200;
        public byte[] Body { get; init; } = [];
        /// <summary>Headers to send besides the length, such as <c>Content-Type</c>.</summary>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }
        /// <summary>The length announced when it is not the body's, such as a file said to be larger than it is; the connection is dropped after the body.</summary>
        public long? ContentLength { get; init; }
        /// <summary>Sent in chunks, with no length announced.</summary>
        public bool Chunked { get; init; }
        /// <summary>Awaited before anything is sent: a server that takes the request and does not answer, until it completes.</summary>
        public Task? Before { get; init; }
        /// <summary>With <see cref="Hold"/>: the bytes of the body sent (with the headers) before the server stops sending.</summary>
        public int HoldAt { get; init; }
        /// <summary>Awaited once <see cref="HoldAt"/> bytes of the body are sent: a body that stalls part way, until it completes.</summary>
        public Task? Hold { get; init; }
        /// <summary>The bytes of the body sent (with the headers) before the connection is dropped; at least 1.</summary>
        public int? DropAt { get; init; }
        /// <summary>How many writes the body is sent in, with <see cref="Pause"/> between them.</summary>
        public int Pieces { get; init; } = 1;
        public TimeSpan Pause { get; init; }

        public static Reply Text(string text, int status = 200) => new() { Status = status, Body = Encoding.UTF8.GetBytes(text) };
    }

    readonly HttpListener listener;
    readonly CancellationTokenSource disposing = new();
    readonly Dictionary<string, Reply> replies = new(StringComparer.Ordinal);
    readonly List<Request> requests = [];
    readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    readonly List<(string Path, int Count, TaskCompletionSource Reached)> waiting = [];
    readonly HashSet<Task> answering = [];
    readonly List<Task> accepting = [];

    LoopbackServer(HttpListener listener, int port)
    {
        this.listener = listener;
        Port = port;
        accepting.Add(Accept());
    }

    /// <summary>Starts a server on a free port of 127.0.0.1.</summary>
    public static LoopbackServer Start()
    {
        for (var attempt = 1; ; attempt++)
        {
            // HttpListener takes no port 0: a port free a moment ago is taken, and another tried should it be gone.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return new LoopbackServer(listener, port);
            }
            catch (HttpListenerException) when (attempt < 10) { listener.Close(); }
        }
    }

    public int Port { get; }

    /// <summary>The address of a path (and query) on the server, such as <c>Url("/power")</c>.</summary>
    public string Url(string target) => $"http://127.0.0.1:{Port}{target}";

    /// <summary>From now on answers requests for <paramref name="path"/> (without a query) with <paramref name="reply"/>.</summary>
    public void Respond(string path, Reply reply) { lock (replies) replies[path] = reply; }

    /// <summary>Every request recorded since the start or the last <see cref="Clear"/>, in the order they arrived.</summary>
    public Request[] Requests() { lock (requests) return [.. requests]; }

    /// <summary>How many requests for <paramref name="path"/> arrived since the start.</summary>
    public int Count(string path) { lock (requests) return counts.GetValueOrDefault(path); }

    /// <summary>Ends once <paramref name="count"/> requests for <paramref name="path"/> arrived since the start, or fails after five seconds.</summary>
    public Task Requested(string path, int count = 1)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (requests)
        {
            if (counts.GetValueOrDefault(path) >= count) return Task.CompletedTask;
            waiting.Add((path, count, reached));
        }
        return reached.Task.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(ended =>
        {
            if (ended.IsFaulted) throw new Exception($"timed out waiting for request {count} of {path}; {Count(path)} arrived");
        }, TaskScheduler.Default);
    }

    /// <summary>Stops listening: a connection to the port is refused, as to a device that is off, until <see cref="Up"/>.</summary>
    public void Down() { lock (accepting) listener.Stop(); }

    /// <summary>Listens on the port again after <see cref="Down"/>.</summary>
    public void Up()
    {
        lock (accepting)
        {
            listener.Start();
            accepting.Add(Accept());
        }
    }

    /// <summary>Forgets the requests recorded so far (the counts stay).</summary>
    public void Clear() { lock (requests) requests.Clear(); }

    async Task Accept()
    {
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (disposing.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            lock (answering)
            {
                var answer = Task.Run(() => Answer(context));
                answering.Add(answer);
                _ = answer.ContinueWith(ended => { lock (answering) answering.Remove(ended); }, TaskScheduler.Default);
            }
        }
    }

    async Task Answer(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            using var body = new MemoryStream();
            await request.InputStream.CopyToAsync(body, disposing.Token);
            var path = request.Url!.AbsolutePath;
            var headers = request.Headers.AllKeys.OfType<string>().ToDictionary(name => name, name => string.Join(",", request.Headers.GetValues(name) ?? []), StringComparer.OrdinalIgnoreCase);
            Record(new Request(request.HttpMethod, path, request.RawUrl ?? path, headers, body.ToArray()));
            Reply reply;
            lock (replies) reply = replies.GetValueOrDefault(path) ?? new Reply { Status = 404 };
            await Send(reply, response);
        }
        // A client that went away, or the server disposed while it waited: nothing to answer.
        catch (Exception) { try { response.Abort(); } catch { } }
    }

    void Record(Request request)
    {
        List<TaskCompletionSource> reached = [];
        lock (requests)
        {
            requests.Add(request);
            var count = counts[request.Path] = counts.GetValueOrDefault(request.Path) + 1;
            foreach (var wait in waiting.Where(w => w.Path == request.Path && w.Count <= count).ToArray())
            {
                waiting.Remove(wait);
                reached.Add(wait.Reached);
            }
        }
        foreach (var signal in reached) signal.TrySetResult();
    }

    async Task Send(Reply reply, HttpListenerResponse response)
    {
        if (reply.Before != null) await reply.Before.WaitAsync(disposing.Token);
        if (reply.DropAt is < 1) throw new ArgumentException("A connection is dropped after at least one byte of the body");
        response.StatusCode = reply.Status;
        foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>())
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) response.ContentType = value;
            else response.Headers[name] = value;
        if (reply.Chunked) response.SendChunked = true;
        else response.ContentLength64 = reply.ContentLength ?? reply.Body.Length;
        var end = Math.Min(reply.DropAt ?? reply.Body.Length, reply.Body.Length);
        // Where the writes end: each piece, the hold and the drop.
        var cuts = new SortedSet<int> { end };
        for (var piece = 1; piece < reply.Pieces; piece++) cuts.Add(reply.Body.Length * piece / reply.Pieces);
        if (reply.Hold != null) cuts.Add(reply.HoldAt);
        var output = response.OutputStream;
        var at = 0;
        foreach (var cut in cuts.Where(cut => cut <= end))
        {
            if (cut > at)
            {
                await output.WriteAsync(reply.Body.AsMemory(at, cut - at), disposing.Token);
                await output.FlushAsync(disposing.Token);
                at = cut;
            }
            if (reply.Hold != null && cut == reply.HoldAt) await reply.Hold.WaitAsync(disposing.Token);
            else if (cut < end && reply.Pause > TimeSpan.Zero) await Task.Delay(reply.Pause, disposing.Token);
        }
        if (reply.DropAt != null || (reply.ContentLength is { } announced && announced != reply.Body.Length)) response.Abort();
        else response.Close();
    }

    /// <summary>Releases what it holds, stops listening and waits for the requests it answers.</summary>
    public async ValueTask DisposeAsync()
    {
        disposing.Cancel();
        try { listener.Stop(); } catch { }
        listener.Close();
        Task[] loops;
        lock (accepting) loops = [.. accepting];
        await Task.WhenAll(loops);
        Task[] left;
        lock (answering) left = [.. answering];
        await Task.WhenAll(left).WaitAsync(TimeSpan.FromSeconds(5));
        lock (requests)
            foreach (var wait in waiting) wait.Reached.TrySetCanceled();
        disposing.Dispose();
    }
}

/// <summary>
/// A server on <c>127.0.0.1:&lt;a free port&gt;</c> that takes each connection, reads the request and never answers: a
/// request to it ends only when the client gives up, which it sees as the connection closing. It records each request's
/// target and counts the connections still open.
/// </summary>
sealed class SilentServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource disposing = new();
    readonly List<string> targets = [];
    readonly HashSet<Task> reading = [];
    readonly Task accepting;
    int open;

    SilentServer()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = Accept();
    }

    /// <summary>Starts a server on a free port of 127.0.0.1.</summary>
    public static SilentServer Start() => new();

    public int Port { get; }

    /// <summary>The address of a path (and query) on the server.</summary>
    public string Url(string target) => $"http://127.0.0.1:{Port}{target}";

    /// <summary>The connections their clients have not closed.</summary>
    public int Open => Volatile.Read(ref open);

    /// <summary>The target (path and query) of each request whose first line arrived, in order.</summary>
    public string[] Targets() { lock (targets) return [.. targets]; }

    /// <summary>Forgets the targets recorded so far.</summary>
    public void Clear() { lock (targets) targets.Clear(); }

    async Task Accept()
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(disposing.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref open);
            lock (reading)
            {
                var task = Task.Run(() => Read(client));
                reading.Add(task);
                _ = task.ContinueWith(ended => { lock (reading) reading.Remove(ended); }, TaskScheduler.Default);
            }
        }
    }

    // Reads until the client closes the connection, recording the request line.
    async Task Read(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var head = new StringBuilder();
                var recorded = false;
                int read;
                while ((read = await stream.ReadAsync(buffer, disposing.Token)) > 0)
                {
                    if (recorded) continue;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    if (head.ToString().IndexOf("\r\n", StringComparison.Ordinal) is var end and >= 0)
                    {
                        var line = head.ToString(0, end).Split(' ');
                        lock (targets) targets.Add(line.Length > 1 ? line[1] : "");
                        recorded = true;
                        head.Clear();
                    }
                }
            }
        }
        catch (Exception) { }
        finally { Interlocked.Decrement(ref open); }
    }

    /// <summary>Stops listening, closes the connections still open and waits for them.</summary>
    public async ValueTask DisposeAsync()
    {
        disposing.Cancel();
        listener.Stop();
        await accepting;
        Task[] left;
        lock (reading) left = [.. reading];
        await Task.WhenAll(left).WaitAsync(TimeSpan.FromSeconds(5));
        disposing.Dispose();
    }
}
