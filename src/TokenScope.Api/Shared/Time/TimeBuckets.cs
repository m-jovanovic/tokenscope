namespace TokenScope.Api.Shared.Time;

public static class TimeBuckets
{
    public static DateTime Floor(DateTime value, int bucketSeconds)
    {
        var bucketTicks = bucketSeconds * TimeSpan.TicksPerSecond;
        var remainder = (value.Ticks - DateTime.UnixEpoch.Ticks) % bucketTicks;
        return new DateTime(value.Ticks - remainder, DateTimeKind.Utc);
    }

    public static DateTime Ceiling(DateTime value, int bucketSeconds)
    {
        var bucketStart = Floor(value, bucketSeconds);
        return bucketStart == value ? value : bucketStart.AddSeconds(bucketSeconds);
    }
}
