namespace Omron2Garmin.Web.Models;

/// <summary>
/// Represents a blood pressure reading
/// </summary>
public class BloodPressureReading
{
    public DateTime Timestamp { get; set; }
    public int Systolic { get; set; }
    public int Diastolic { get; set; }
    public int Pulse { get; set; }
    public bool IrregularHeartbeat { get; set; }
    public string? DeviceModel { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Result of a sync operation
/// </summary>
public class SyncResult
{
    public int TotalReadings { get; set; }
    public int Uploaded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<BloodPressureReading> UploadedReadings { get; set; } = [];
}
