using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Tns.Tools.Terrain;

/// <summary>
/// Adds up how long a bake's threads spend on each kind of work, to see where the time goes.
/// Work is counted per thread, so the shares add up to all threads' time, not the clock's.
/// </summary>
public sealed class WorkTimer
{
    private sealed class Bucket
    {
        public long Ticks;
        public long Count;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public long Start() => Stopwatch.GetTimestamp();

    public void Stop(string what, long started)
    {
        Bucket bucket = _buckets.GetOrAdd(what, _ => new Bucket());
        Interlocked.Add(ref bucket.Ticks, Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref bucket.Count);
    }

    public void Print(string title)
    {
        double total = _buckets.Values.Sum(b => (double)b.Ticks);
        if (total <= 0) return;
        Console.WriteLine($"  {title}: where the threads' time went ({total / Stopwatch.Frequency / 60.0:0.0} thread-minutes in all)");
        foreach (var (what, bucket) in _buckets.OrderByDescending(b => b.Value.Ticks))
        {
            double seconds = bucket.Ticks / (double)Stopwatch.Frequency;
            Console.WriteLine($"    {what,-46} {bucket.Count,9:#,0} times  {seconds / 60.0,7:0.0} min  {100.0 * bucket.Ticks / total,5:0.0}%  " +
                              $"{1000.0 * seconds / Math.Max(bucket.Count, 1),7:0.00} ms each");
        }
    }
}
