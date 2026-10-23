namespace ProjectPulse.Processor.Models;

public sealed record RequestHistoryTimingAverage(
    double? AverageMs,
    long SampleCount);

public sealed record RequestHistoryTimingAverages(
    RequestHistoryTimingAverage RtaPull,
    RequestHistoryTimingAverage Translation,
    RequestHistoryTimingAverage ApimRoundTrip,
    RequestHistoryTimingAverage RtaWriteback,
    RequestHistoryTimingAverage EndToEnd,
    long RtaStatusPutFailureBatchCount,
    DateTimeOffset CalculatedAtUtc);
