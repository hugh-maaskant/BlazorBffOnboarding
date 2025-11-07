using System.ComponentModel.DataAnnotations;

namespace BlazorBffOnboarding.AppUser;

/// <summary>
/// A simple demonstration model for the AppUser database entries.
/// </summary>
public class AppUser
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    // The name of the OIDC Identity Provider (IDP)
    public string IdpName { get; set; } = null!;

    [Required]
    // The "sub" claim value from the IDP
    public string IdpSubject { get; set; } = null!;

    [Required]
    [StringLength(50)]
    // The user's display name as entered in the onboarding form
    public string DisplayName { get; set; } = null!;
}