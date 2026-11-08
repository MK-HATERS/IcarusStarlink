using FluentFTP;

namespace IcarusStarlink.Storage.Tests.Server;

public class FluentFtpPackageTests
{
    // FluentFTP before 55.1.0 accepted any server certificate on FTPS data connections (listings,
    // uploads, downloads). 55.1.0 checks them, and IsDataConnection came with that fix, so this
    // fails if the package is ever moved back below it. Looked up by name on purpose: nameof
    // would turn a downgrade into a compile error instead of a failing test.
    [Fact]
    public void FluentFtp_ChecksCertificatesOnDataConnections()
    {
        Assert.NotNull(typeof(FtpSslValidationEventArgs).GetProperty("IsDataConnection"));
    }
}
