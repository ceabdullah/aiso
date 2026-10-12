namespace Aiso.Domain.Entities;

/// <summary>Runtime-editable key/value configuration (edited from the dashboard, read by the worker).</summary>
public class GlobalSystemSetting : EntityBase
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public string? Description { get; set; }

    public string? UpdatedBy { get; set; }
}
