using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ReforgerMcp;

public sealed class WindowCapture(Configuration configuration) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private Dictionary<string, Selection> selections = [];
    private sealed class Selection : IDisposable
    {
        public nint Handle { get; }
        public int ProcessId { get; }
        public long StartTicks { get; }
        public string Executable { get; }
        public CaptureWindow Window { get; }
        public GraphicsCaptureItem Item { get; }
        private int closed;
        private int disposed;
        public bool Closed => Volatile.Read(ref closed) != 0;

        public Selection(nint handle, int processId, long startTicks, string executable, CaptureWindow window)
        {
            Handle = handle; ProcessId = processId; StartTicks = startTicks; Executable = executable; Window = window;
            // Bind once, at selection time. Recreating from an HWND later could capture a
            // replacement window after Windows reuses that handle within the same process.
            Item = CaptureNative.CreateItem(handle);
            Item.Closed += OnClosed;
        }
        private void OnClosed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref closed, 1);
        public void Dispose()
        {
            Interlocked.Exchange(ref closed, 1);
            if (Interlocked.Exchange(ref disposed, 1) == 0) Item.Closed -= OnClosed;
        }
    }

    public object List()
    {
        var configured = new Dictionary<string, string>(configuration.CaptureExecutables);
        foreach (var (label, wb) in configuration.WorkbenchEntries) configured.Add(label == "workbench" ? label : "workbench-" + label, wb.Executable);
        var next = new Dictionary<string, Selection>();
        var unavailable = new List<object>();
        var supported = GraphicsCaptureSession.IsSupported();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var processes = new Dictionary<int, (string Label, string Path, long Start)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!configured.Values.Any(p => Path.GetFileNameWithoutExtension(p).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
                    var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Executable path unavailable.");
                    var match = configured.FirstOrDefault(p => p.Value.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (match.Key is not null)
                        processes.Add(process.Id, (match.Key, path, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                { unavailable.Add(new { processId = process.Id, error = ex.Message }); }
            }
        }
        CaptureNative.EnumWindowsCallback callback = (hwnd, _) =>
        {
            CaptureNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (!processes.TryGetValue((int)pid, out var process) || !CaptureNative.IsWindowVisible(hwnd)) return true;
            var id = Guid.NewGuid().ToString("N");
            var window = new CaptureWindow(id, process.Label, (int)pid, process.Path, Title(hwnd),
                CaptureNative.IsIconic(hwnd), expires);
            try
            {
                if (!supported) throw new InvalidOperationException("Windows Graphics Capture is unavailable.");
                next.Add(id, new(hwnd, (int)pid, process.Start, process.Path, window));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            { unavailable.Add(new { processId = (int)pid, title = window.Title, error = ex.Message }); }
            return true;
        };
        try
        {
            if (!CaptureNative.EnumWindows(callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (next.Count > 256) throw new InvalidOperationException("Too many eligible windows (limit 256). Close unused windows before listing again.");
            lock (gate)
            {
                foreach (var old in selections.Values) old.Dispose();
                selections = next;
            }
        }
        catch
        {
            foreach (var selection in next.Values) selection.Dispose();
            throw;
        }
        return new
        {
            state = configured.Count == 0 ? "not_configured" : "observed",
            supported,
            windows = next.Values.Select(s => s.Window).ToArray(),
            unavailable,
            missingExecutables = configured.Where(p => !File.Exists(p.Value)).Select(p => new { target = p.Key, executable = p.Value }).ToArray(),
            scope = "Visible top-level windows belonging to explicitly configured executable paths, including dialogs and separate clients. " +
                "Selections expire after five minutes, on another capture_windows call, or server restart. Minimized windows are listed but cannot be captured."
        };
    }

    public async Task<CapturedWindow> Screenshot(string windowId, int maxDimension, CancellationToken cancellationToken)
        => (await Sequence(windowId, maxDimension, 1, cancellationToken))[0];

    public async Task<IReadOnlyList<CapturedWindow>> Sequence(string windowId, int maxDimension, int frameCount, CancellationToken cancellationToken)
    {
        if (frameCount is < 1 or > 16) throw new ArgumentException("frameCount must be 1-16.");
        if (maxDimension is < 320 or > 3840) throw new ArgumentException("maxDimension must be 320-3840 pixels.");
        if (!await captureGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Window capture is busy.");
        try
        {
            Selection selected;
            lock (gate)
                selected = selections.GetValueOrDefault(windowId) ?? throw new ArgumentException("Unknown windowId. Call capture_windows and select one of its windows.");
            Validate(selected);
            if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("Windows Graphics Capture is unavailable on this system.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var device = CaptureNative.CreateDevice();
                var item = selected.Item;
                var size = item.Size;
                if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 33_177_600)
                    throw new InvalidOperationException("Unsupported capture dimensions (maximum 33,177,600 source pixels).");
                if ((long)size.Width * size.Height * 4 * frameCount > 256L * 1024 * 1024)
                    throw new InvalidOperationException("Sequence exceeds 256 MiB source-frame budget. Request fewer frames.");
                using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                using var session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                var arrived = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
                void FrameArrived(Direct3D11CaptureFramePool _, object __) => arrived.Writer.TryWrite(true);
                pool.FrameArrived += FrameArrived;
                var frames = new List<(SoftwareBitmap Bitmap, TimeSpan Time, DateTimeOffset AcquiredAt)>();
                try
                {
                    session.StartCapture();
                    // Copy consecutive available GPU frames before PNG encoding. Native
                    // timestamps expose skipped frames; requested count is not a frame-rate promise.
                    while (frames.Count < frameCount)
                    {
                        await arrived.Reader.ReadAsync(timeout.Token);
                        using var frame = pool.TryGetNextFrame();
                        if (frame is null) continue;
                        if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                            throw new InvalidOperationException("Window resized during capture. List windows again before another capture.");
                        if (frames.Count > 0 && frame.SystemRelativeTime <= frames[^1].Time) continue;
                        var acquiredAt = DateTimeOffset.UtcNow;
                        var copy = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(timeout.Token);
                        frames.Add((copy, frame.SystemRelativeTime, acquiredAt));
                    }
                    session.Dispose();
                    var results = new List<CapturedWindow>();
                    ulong totalBytes = 0;
                    foreach (var captured in frames)
                    {
                        var bitmap = captured.Bitmap;
                        var frameTime = captured.Time;
                        var content = size;
                        using var stream = new InMemoryRandomAccessStream();
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(timeout.Token);
                        encoder.SetSoftwareBitmap(bitmap);
                        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(content.Width, content.Height));
                        var width = Math.Max(1, (uint)Math.Round(content.Width * scale));
                        var height = Math.Max(1, (uint)Math.Round(content.Height * scale));
                        encoder.BitmapTransform.ScaledWidth = width;
                        encoder.BitmapTransform.ScaledHeight = height;
                        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                        await encoder.FlushAsync().AsTask(timeout.Token);
                        if (stream.Size > 16 * 1024 * 1024) throw new InvalidOperationException("PNG exceeds 16 MiB. Request a smaller maxDimension.");
                        totalBytes += stream.Size;
                        if (totalBytes > 64 * 1024 * 1024) throw new InvalidOperationException("Sequence exceeds 64 MiB PNG budget. Request fewer or smaller frames.");
                        var bytes = new byte[(int)stream.Size];
                        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                        {
                            var loaded = await reader.LoadAsync((uint)bytes.Length).AsTask(timeout.Token);
                            if (loaded != bytes.Length) throw new IOException("Incomplete PNG read.");
                            reader.ReadBytes(bytes);
                        }
                        Validate(selected);
                        results.Add(new(bytes, new
                        {
                            windowId, target = selected.Window.Target, processId = selected.ProcessId,
                            executable = selected.Executable, windowTitle = Title(selected.Handle),
                            capturedAt = captured.AcquiredAt, frameSystemRelativeTicks = frameTime.Ticks,
                            sourceWidth = content.Width, sourceHeight = content.Height, width, height,
                            mimeType = "image/png", backend = "Windows.Graphics.Capture",
                            scope = "One window frame, without cursor. No activation, desktop capture, scripted bridge or injection. " +
                                "Separate dialogs are separate capture_windows entries. A frame is not proof of application readiness; an application may display old or blank content."
                        }));
                    }
                    return results;
                }
                finally
                {
                    pool.FrameArrived -= FrameArrived;
                    foreach (var frame in frames) frame.Bitmap.Dispose();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new InvalidOperationException("Window capture timed out after 10 seconds. The window may be unavailable or not rendering. No alternate backend was used."); }
        }
        finally { captureGate.Release(); }
    }

    private static void Validate(Selection selection)
    {
        if (selection.Closed) throw new InvalidOperationException("Selected window closed or selection was invalidated. Call capture_windows again.");
        if (DateTimeOffset.UtcNow >= selection.Window.ExpiresAt) throw new InvalidOperationException("Window selection expired. Call capture_windows again.");
        CaptureNative.GetWindowThreadProcessId(selection.Handle, out var pid);
        if (pid != selection.ProcessId || !CaptureNative.IsWindowVisible(selection.Handle))
            throw new InvalidOperationException("Selected window closed or is no longer visible. Call capture_windows again.");
        using var process = Process.GetProcessById(selection.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != selection.StartTicks ||
            !string.Equals(process.MainModule?.FileName, selection.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selected process changed. Call capture_windows again.");
        if (CaptureNative.IsIconic(selection.Handle)) throw new InvalidOperationException("Selected window is minimized. Restore it explicitly before capture.");
        Marshal.ThrowExceptionForHR(CaptureNative.DwmGetWindowAttribute(selection.Handle, 14, out var cloaked, sizeof(int)));
        if (cloaked != 0) throw new InvalidOperationException("Selected window is cloaked or on an unavailable desktop.");
    }

    private static string Title(nint hwnd)
    {
        var title = new StringBuilder(4096);
        CaptureNative.GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var selection in selections.Values) selection.Dispose();
            selections.Clear();
        }
    }
}

public sealed record CaptureWindow(string WindowId, string Target, int ProcessId, string Executable, string Title, bool Minimized, DateTimeOffset ExpiresAt);
public sealed record CapturedWindow(byte[] Png, object Metadata);
