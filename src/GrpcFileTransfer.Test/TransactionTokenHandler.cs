namespace Pulsar.GrpcFileTransfer.Test;

public class TransactionTokenHandler : IDisposable
{
    public void AddDownloadToken(string token, string downloadSourcePath) => downloadTokenDict.Add(token, downloadSourcePath);
    private readonly Dictionary<string, string> downloadTokenDict = new();
    public IReadOnlyDictionary<string, string> UploadTokenDictionary => uploadTokenDict;

    public void AddUploadToken(string token, string uploadTargetPath) => uploadTokenDict.Add(token, uploadTargetPath);
    private readonly Dictionary<string, string> uploadTokenDict = new();
    public IReadOnlyDictionary<string, string> DownloadTokenDictionary => downloadTokenDict;

    public void Dispose()
    {
        foreach (var filepath in DownloadTokenDictionary.Values)
            if (File.Exists(filepath))
                File.Delete(filepath);

        foreach (var filepath in UploadTokenDictionary.Values)
            if (File.Exists(filepath))
                File.Delete(filepath);
    }
}