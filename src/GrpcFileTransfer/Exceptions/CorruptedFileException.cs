namespace Pulsar.GrpcFileTransfer.Exceptions;

public class CorruptedFileException : Exception
{
    public string Filename { get; }
    public string SourceHash { get; }
    public string TargetHash { get; }

    public CorruptedFileException(string filename, string sourceHash, string targetHash, string message) : base($"File {filename} is corrupted. Md5 hashes: Source hash: {sourceHash}, target hash: {targetHash}. {message}")
    {
        Filename = filename;
        SourceHash = sourceHash;
        TargetHash = targetHash;
    }

    public CorruptedFileException(string filename, string sourceHash, string targetHash, string message, Exception innerException) : base($"File {filename} is corrupted. Md5 hashes: Source hash: {sourceHash}, target hash: {targetHash}. {message}", innerException)
    {
        Filename = filename;
        SourceHash = sourceHash;
        TargetHash = targetHash;
    }
}