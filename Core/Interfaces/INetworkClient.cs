using System.IO;
using System.Threading.Tasks;

namespace DotNetDownloadManager.Core.Interfaces;

public interface INetworkClient
{
    Task<long> GetFileSizeAsync(string url);
    Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null);
}
