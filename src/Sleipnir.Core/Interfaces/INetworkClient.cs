using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sleipnir.Core.Models;

namespace Sleipnir.Core.Interfaces;

public interface INetworkClient
{
    /// <param name="publicOnly">Connect to public addresses only, or throw <see cref="DestinationNotPublicException"/>.</param>
    Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default, bool publicOnly = false);

    /// <param name="publicOnly">Connect to public addresses only, or throw <see cref="DestinationNotPublicException"/>.</param>
    Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken cancellationToken = default, bool publicOnly = false);
}
