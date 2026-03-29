namespace DeerHunter.Models;

public sealed record ObservedLine(
    string Source,
    string Text,
    string? ExternalLogName = null);
