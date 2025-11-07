namespace BlazorAutoRendering.AppUser;

public interface IAppUserService
{
    /// <summary>
    /// Create a new <see cref="AppUser"/> record in the database.
    /// </summary>
    /// <param name="idpName">The name of the OIDC Identity Provider (IDP).</param>
    /// <param name="idpSubject">The "sub" claim value from the IDP.</param>
    /// <param name="displayName">The user's display name.</param>
    /// <returns>The newly created <see cref="AppUser"/>.</returns>
    Task<AppUser> CreateAppUserAsync(string idpName, string idpSubject, string displayName);

    /// <summary>
    /// Find an <see cref="AppUser"/> with the given <paramref name="idpName"/> and <paramref name="idpSubject"/>.
    /// </summary>
    /// <param name="idpName">The name of the OIDC Identity Provider (IDP).</param>
    /// <param name="idpSubject">The "sub" claim value from the IDP.</param>
    /// <returns>The found <see cref="AppUser"/> or null if not found.</returns>
    Task<AppUser?> FindUserByExternalIdAsync(string idpName, string idpSubject);

    /// <summary>
    /// Get all <see cref="AppUser"/> records from the database.
    /// </summary>
    Task<List<AppUser>> GetAllUsersAsync();

    /// <summary>
    /// Delete an <see cref="AppUser"/> record from the database.
    /// </summary>
    /// <param name="userId">The application ID of the user to delete.</param>
    Task DeleteUserAsync(Guid userId);
}