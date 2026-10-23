namespace PingPilot.Models;

/// <summary>Один результат проверки. LatencyMs == null — проверка не удалась.</summary>
public readonly record struct PingSample(DateTime Time, long? LatencyMs, string Status);
