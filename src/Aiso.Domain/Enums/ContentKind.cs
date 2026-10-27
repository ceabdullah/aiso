namespace Aiso.Domain.Enums;

/// <summary>What a content item becomes once published.</summary>
public enum ContentKind
{
    /// <summary>A web page or FAQ section on the brand's site.</summary>
    Article,

    /// <summary>A social media post (caption and image idea).</summary>
    SocialPost,

    /// <summary>App Store description and "What's New" text.</summary>
    AppStoreText,
}
