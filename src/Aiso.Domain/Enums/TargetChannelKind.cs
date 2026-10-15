namespace Aiso.Domain.Enums;

/// <summary>The kind of external surface being audited. Targets are always treated as black boxes.</summary>
public enum TargetChannelKind
{
    CoreWebPortal = 0,
    DigitalChannel = 1,
    MobileAppStore = 2,

    /// <summary>The brand itself, independent of any one website: general questions about the bank. No page is scraped.</summary>
    Brand = 3,
}
