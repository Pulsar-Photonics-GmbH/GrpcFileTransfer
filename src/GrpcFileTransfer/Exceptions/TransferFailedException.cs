namespace Pulsar.GrpcFileTransfer.Exceptions;

public class TransferFailedException : Exception
{
    public string Filename { get; }

    public TransferFailedException(string filename, string message) : base($"Transfer of file {filename} failed: {message}")
    {
        Filename = filename;
    }

    public TransferFailedException(string filename, string message, Exception innerException) : base($"Transfer of file {filename} failed: {message}", innerException)
    {
        Filename = filename;
    }
}