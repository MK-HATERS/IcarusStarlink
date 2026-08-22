namespace IcarusStarlink.Core.Server;

/// <summary>File-based storage for saved FTP sites (host/port/username/remote path) — mirrors INexusWatchlistStore's own shape. Passwords are never stored here; see CredentialTargets.FtpSite.</summary>
public interface IFtpSiteStore
{
    IReadOnlyList<FtpSiteProfile> GetAll();

    void Save(FtpSiteProfile site);

    void Delete(Guid id);
}
