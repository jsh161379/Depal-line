namespace DepalAdmin.Core.Models;

/// <summary>sensor/env — 환경 센서 1묶음. 메시지_형식 v1.3.1 5장. 라파가 주기 발행 (기본 5초)</summary>
public sealed class EnvReading
{
    public string ReadingId { get; set; } = "";
    public string? RunId { get; set; }
    public string Ts { get; set; } = "";
    public string Device { get; set; } = "";
    public List<EnvValue> Readings { get; set; } = new();

    /// <summary>metric(GAS / TEMPERATURE / HUMIDITY)의 첫 값. 없으면 null</summary>
    public EnvValue? Find(string metric) =>
        Readings.FirstOrDefault(r => string.Equals(r.Metric, metric, StringComparison.OrdinalIgnoreCase));
}

public sealed class EnvValue
{
    /// <summary>센서 이름 (gas_1, temp_hum_1)</summary>
    public string Sensor { get; set; } = "";

    /// <summary>GAS / TEMPERATURE / HUMIDITY</summary>
    public string Metric { get; set; } = "";

    public double Value { get; set; }

    /// <summary>raw, C, %</summary>
    public string Unit { get; set; } = "";
}

/// <summary>sensor/env 의 metric 값</summary>
public static class EnvMetrics
{
    public const string Gas = "GAS";
    public const string Temperature = "TEMPERATURE";
    public const string Humidity = "HUMIDITY";
}
