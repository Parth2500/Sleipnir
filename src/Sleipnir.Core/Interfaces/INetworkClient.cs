using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sleipnir.Core.Models;

namespace Sleipnir.Core.Interfaces;

public interface INetworkClient
{
    Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default);
    Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken cancellationToken = default);
}
