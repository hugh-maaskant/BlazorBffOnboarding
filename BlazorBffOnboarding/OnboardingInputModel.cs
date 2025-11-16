    using System.ComponentModel.DataAnnotations;

namespace BlazorBffOnboarding;

/// <summary>
/// The data to be gathered during new user onboarding.
/// </summary>
public class OnboardingInputModel
{
    public const int MinDisplayNameLength =  5;
    public const int MaxDisplayNameLength = 50;

    [Required]
    [MinLength(MinDisplayNameLength, ErrorMessage = "Display name is too short, minimum is 5 characters.")]
    [StringLength(MaxDisplayNameLength, ErrorMessage = "Display name is too long, maximum is 50 characters.")]
    [RegularExpression(@"^[\p{L}\p{N}\s-._]+$", ErrorMessage = "Display name can only contain letters, numbers, spaces, and basic punctuation.")]
    public string? DisplayName { get; set; }
}