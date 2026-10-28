using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Knx.App.Services;

namespace Knx.App.Views;

/// <summary>UI-owned decoded image leases shared by all views of one immutable resource reader.</summary>
internal static class ParameterImageCache
{
    internal const long MaximumPixels = 16_000_000;
    private static readonly ConditionalWeakTable<ParameterResourceReader, ReaderCache> Readers = new();

    internal static ImageLease Acquire(ParameterItemViewModel row)
    {
        if (row.Resources is not { } reader || row.Type?.PictureResourceId is not { } id)
            return new(ParameterResourceStatus.Missing);
        return Readers.GetValue(reader, source => new(source)).Acquire(id);
    }

    internal sealed class ImageLease(ParameterResourceStatus status, Bitmap? bitmap = null, bool budgetExceeded = false, Action? release = null) : IDisposable
    {
        private Action? _release = release;
        public ParameterResourceStatus Status { get; } = status;
        public Bitmap? Bitmap { get; } = bitmap;
        public bool BudgetExceeded { get; } = budgetExceeded;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class ReaderCache(ParameterResourceReader reader)
    {
        private sealed class Entry(Bitmap bitmap, long pixels)
        { public Bitmap Bitmap { get; } = bitmap; public long Pixels { get; } = pixels; public int References = 1; }
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private long _pixels;

        public ImageLease Acquire(string id)
        {
            if (_entries.TryGetValue(id, out var cached))
            { cached.References++; return Lease(id, cached); }
            var content = reader.ReadImage(id);
            if (content.Status != ParameterResourceStatus.Available || content.Bytes is null) return new(content.Status);
            var pixels = (long)content.Width * content.Height;
            if (pixels <= 0 || pixels > MaximumPixels - _pixels) return new(ParameterResourceStatus.TooLarge, budgetExceeded: true);
            try
            {
                using var input = new MemoryStream(content.Bytes, false);
                var bitmap = new Bitmap(input);
                var entry = new Entry(bitmap, pixels);
                _entries.Add(id, entry); _pixels += pixels;
                return Lease(id, entry);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException or OverflowException)
            { return new(ParameterResourceStatus.Invalid); }
        }

        private ImageLease Lease(string id, Entry entry) => new(ParameterResourceStatus.Available, entry.Bitmap,
            release: () =>
            {
                if (--entry.References != 0) return;
                _entries.Remove(id); _pixels -= entry.Pixels; entry.Bitmap.Dispose();
            });
    }
}
