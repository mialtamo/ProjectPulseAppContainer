namespace ProjectPulse.Processor.Models;

public sealed record RtaWritebackResult(
    bool Attempted,
    bool? Succeeded,
    int Attempts,
    int? HttpStatusCode,
    string? FailureKind,
    string? FailureMessage);
