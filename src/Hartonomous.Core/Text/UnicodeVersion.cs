namespace Hartonomous.Core.Text;

/// <summary>
/// Unicode version the substrate's text primitives target. Bumping this is an
/// explicit substrate event: segmentation, normalization, and property tables
/// are re-seeded, and a new <c>text_segmentation_profile</c> entity is created
/// so prior-version segmentation decisions remain queryable.
/// </summary>
public enum UnicodeVersion
{
    U160 = 160,
}
