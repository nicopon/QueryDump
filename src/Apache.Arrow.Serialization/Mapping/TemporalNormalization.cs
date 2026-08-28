using System;

namespace Apache.Arrow.Serialization.Mapping;

/// <summary>
/// Both directions of one rule: a <see cref="DateTime"/> whose <see cref="DateTimeKind"/> is
/// <see cref="DateTimeKind.Unspecified"/> is a wall clock with no zone, and nothing may invent one
/// for it. The two methods are inverses and must be changed together.
/// <para>
/// <b>Do not bypass.</b> <c>new DateTimeOffset(dt)</c> and
/// <c>TimestampArray.Builder.Append(DateTime)</c> both resolve a zone-less value against
/// <see cref="TimeZoneInfo.Local"/>, which puts the host's time zone into the data path and makes
/// the same input produce different output on different machines. <c>validate_core_boundary.sh</c>
/// rejects the constructor; <c>validate_temporal.sh</c> covers the rest by running under two
/// <c>TZ</c> values.
/// </para>
/// <para>
/// <b>One assumption.</b> For a zone-less Arrow timestamp, storing the wall clock verbatim is the
/// format's own definition ("does not represent a single moment in time… a wall clock time"). For
/// a zone-aware column that nonetheless yields an <c>Unspecified</c> value — Npgsql's binary
/// <c>timestamptz</c> export does — reading it as UTC is an assumption: correct for Npgsql, and
/// the only zone-independent reading available, but a driver returning local time would defeat it.
/// </para>
/// </summary>
public static class TemporalNormalization
{
    /// <summary>
    /// CLR → interchange. Produces the offset to store, never consulting the ambient time zone for
    /// an <see cref="DateTimeKind.Unspecified"/> input. <c>Utc</c> and <c>Local</c> values name a
    /// real instant and are converted normally.
    /// </summary>
    public static DateTimeOffset ToOffset(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : new DateTimeOffset(value);

    /// <summary>
    /// Interchange → CLR, the exact inverse of <see cref="ToOffset"/> for a zone-less column:
    /// takes the wall clock back out and marks it zone-less again, so a round trip is the identity.
    /// <para>
    /// Reads the UTC component rather than <c>value.DateTime</c>: values coming out of an Arrow
    /// timestamp array always carry a zero offset, which makes the two identical today, but only
    /// the UTC component stays correct if a value with a real offset ever reaches this path.
    /// </para>
    /// </summary>
    public static DateTime ToWallClock(DateTimeOffset value)
        => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified);
}
