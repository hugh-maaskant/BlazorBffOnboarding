using BlazorAutoRendering.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BlazorAutoRendering.AppUser;

public class AppUserService : IAppUserService
{
    private readonly ApplicationDbContext _context;

    public AppUserService(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc cref="IAppUserService.CreateAppUserAsync"/>
    /// <exception cref="InvalidOperationException">
    /// Thrown if any of the parameters is null or empty, and when the user already exists.
    /// </exception>
    public async Task<AppUser> CreateAppUserAsync(string idpName, string idpSubject, string displayName)
    {
        if (string.IsNullOrEmpty(idpName))
            throw new InvalidOperationException($"{nameof(idpName)} cannot be null or empty");
        if (string.IsNullOrEmpty(idpSubject))
            throw new InvalidOperationException($"{nameof(idpSubject)} cannot be null or empty");
        if (string.IsNullOrEmpty(displayName))
            throw new InvalidOperationException($"{nameof(displayName)} cannot be null or empty");

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            IdpName = idpName,
            IdpSubject = idpSubject,
            DisplayName = displayName
        };

        _context.AppUsers.Add(user);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException dbUpdateException)
        {
            // This will happen if the unique constraint on (IdpName, IdpSubject) is violated.
            // We treat this as an invalid operation because it should be essentially impossible
            // for a new user to have a race condition with themselves.
            throw new InvalidOperationException("A user with this external identity already exists.", dbUpdateException);
        }

        return user;
    }

    /// <inheritdoc cref="IAppUserService.FindUserByExternalIdAsync"/>
    /// <exception cref="InvalidOperationException">
    /// Thrown if any of the parameters is null or empty.
    /// </exception>
    public async Task<AppUser?> FindUserByExternalIdAsync(string idpName, string idpSubject)
    {
        if (string.IsNullOrEmpty(idpName))
            throw new InvalidOperationException("idpName cannot be null or empty");
        if (string.IsNullOrEmpty(idpSubject))
            throw new InvalidOperationException("idpSubject cannot be null or empty");

        return await _context.AppUsers
            .FirstOrDefaultAsync(u => u.IdpName == idpName && u.IdpSubject == idpSubject);
    }

    /// <inheritdoc cref="IAppUserService.GetAllUsersAsync"/>
    public async Task<List<AppUser>> GetAllUsersAsync()
    {
        return await _context.AppUsers.ToListAsync();
    }

    /// <inheritdoc cref="IAppUserService.DeleteUserAsync"/>
    /// <remarks>Returns silently if the user is not found.</remarks>
    public async Task DeleteUserAsync(Guid userId)
    {
        var user = await _context.AppUsers.FindAsync(userId);

        if (user != null)
        {
            _context.AppUsers.Remove(user);
            await _context.SaveChangesAsync();
        }
    }
}