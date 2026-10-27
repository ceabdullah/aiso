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

    /// <summary>
    /// Alt text for an image on the brand's site. <see cref="Entities.ContentItem.Question"/> holds the image address and
    /// <see cref="Entities.ContentItem.LiveUrl"/> the page it is on.
    /// </summary>
    ImageAltText,
}
