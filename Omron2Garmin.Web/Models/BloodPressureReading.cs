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
/// Represents a weight/body composition reading from OMRON scale
/// </summary>
public class WeightReading
{
    public DateTime Timestamp { get; set; }
    public double WeightKg { get; set; }
    public double? Bmi { get; set; }
    public string? Category { get; set; }  // "Normal weight", "Overweight", etc.
    public double? SkeletalMusclePercent { get; set; }
    public double? BodyFatPercent { get; set; }
    public int? VisceralFatLevel { get; set; }
    public string? DeviceModel { get; set; }
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

/// <summary>
/// Result of weight sync operation
/// </summary>
public class WeightSyncResult
{
    public int TotalReadings { get; set; }
    public int Uploaded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<WeightReading> UploadedReadings { get; set; } = [];
}

/// <summary>
/// Combined result for parsing OMRON CSV with multiple data types
/// </summary>
public class OmronParseResult
{
    public List<BloodPressureReading> BloodPressureReadings { get; set; } = [];
    public List<WeightReading> WeightReadings { get; set; } = [];
}
